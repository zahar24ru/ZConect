namespace QualityController;

/// <summary>
/// Monitors network conditions and recommends quality preset changes.
/// Fed with periodic stats snapshots; emits quality change recommendations.
/// </summary>
public sealed class AutoQualityAdapter
{
    private static readonly string[] Ladder = { "Extra Low", "Low", "Medium", "High" };

    private readonly Action<string>? _onLog;
    private int _currentIndex = 2; // start at Medium
    private long _prevBytesSent;
    private long _prevFramesSent;
    private long _prevFramesEncoded;
    private long _prevTimestampUs;
    private int _degradeStreak;
    private int _improveStreak;
    private int _stableCount;

    // Thresholds
    private const int DegradeStreakThreshold = 3;  // consecutive bad samples to downgrade
    private const int ImproveStreakThreshold = 8;   // consecutive good samples to upgrade
    private const int StableCooldown = 5;           // samples after change before considering next change
    private const double FrameDropRatioThreshold = 0.15; // >15% frames dropped = bad

    public string CurrentPreset => Ladder[_currentIndex];

    public AutoQualityAdapter(Action<string>? onLog = null, string? initialPreset = null)
    {
        _onLog = onLog;
        if (initialPreset is not null)
        {
            var idx = Array.FindIndex(Ladder, p => string.Equals(p, initialPreset, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _currentIndex = idx;
        }
    }

    /// <summary>
    /// Feed a stats snapshot. Returns non-null preset name if quality should change.
    /// Call every ~1-2 seconds with VideoSenderStats from WebRTC.
    /// </summary>
    public string? Update(long timestampUs, long bytesSent, long framesSent, long framesEncoded)
    {
        if (_prevTimestampUs == 0)
        {
            // First sample — just store baseline
            _prevTimestampUs = timestampUs;
            _prevBytesSent = bytesSent;
            _prevFramesSent = framesSent;
            _prevFramesEncoded = framesEncoded;
            return null;
        }

        var dtUs = timestampUs - _prevTimestampUs;
        if (dtUs <= 0)
            return null;

        var dBytes = bytesSent - _prevBytesSent;
        var dFramesSent = framesSent - _prevFramesSent;
        var dFramesEncoded = framesEncoded - _prevFramesEncoded;

        _prevTimestampUs = timestampUs;
        _prevBytesSent = bytesSent;
        _prevFramesSent = framesSent;
        _prevFramesEncoded = framesEncoded;

        var dtSec = dtUs / 1_000_000.0;
        var bitrateKbps = dBytes * 8.0 / 1000.0 / dtSec;
        var targetBitrate = QualityProfiles.Resolve(CurrentPreset).BitrateKbps;

        // Frame drop ratio: encoded but not sent = congestion
        double dropRatio = 0;
        if (dFramesEncoded > 0)
            dropRatio = Math.Max(0, 1.0 - (double)dFramesSent / dFramesEncoded);

        // Determine quality signal
        var isBad = dropRatio > FrameDropRatioThreshold || bitrateKbps < targetBitrate * 0.5;
        var isGood = dropRatio < 0.03 && bitrateKbps >= targetBitrate * 0.85;

        if (_stableCount < StableCooldown)
        {
            _stableCount++;
            return null;
        }

        if (isBad)
        {
            _improveStreak = 0;
            _degradeStreak++;
            if (_degradeStreak >= DegradeStreakThreshold && _currentIndex > 0)
            {
                _currentIndex--;
                _degradeStreak = 0;
                _stableCount = 0;
                _onLog?.Invoke($"auto_quality_downgrade_to_{CurrentPreset}_bitrate_{bitrateKbps:F0}_drop_{dropRatio:P0}");
                return CurrentPreset;
            }
        }
        else if (isGood)
        {
            _degradeStreak = 0;
            _improveStreak++;
            if (_improveStreak >= ImproveStreakThreshold && _currentIndex < Ladder.Length - 1)
            {
                _currentIndex++;
                _improveStreak = 0;
                _stableCount = 0;
                _onLog?.Invoke($"auto_quality_upgrade_to_{CurrentPreset}_bitrate_{bitrateKbps:F0}_drop_{dropRatio:P0}");
                return CurrentPreset;
            }
        }
        else
        {
            // Neutral — slowly decay streaks
            _degradeStreak = Math.Max(0, _degradeStreak - 1);
            _improveStreak = Math.Max(0, _improveStreak - 1);
        }

        return null;
    }

    /// <summary>Reset when connection is re-established.</summary>
    public void Reset(string? preset = null)
    {
        _prevTimestampUs = 0;
        _prevBytesSent = 0;
        _prevFramesSent = 0;
        _prevFramesEncoded = 0;
        _degradeStreak = 0;
        _improveStreak = 0;
        _stableCount = 0;
        if (preset is not null)
        {
            var idx = Array.FindIndex(Ladder, p => string.Equals(p, preset, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _currentIndex = idx;
        }
    }
}
