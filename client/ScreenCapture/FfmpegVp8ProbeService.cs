using System.Diagnostics;
using QualityController;

namespace ScreenCapture;

public sealed class FfmpegVp8ProbeService
{
    public async Task<(bool ok, string message)> RunProbeAsync(
        string ffmpegPath,
        string outputDirectory,
        QualityProfile profile,
        DisplaySource? display = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            return (false, "ffmpeg.exe не найден");
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, $"probe_{DateTime.Now:yyyyMMdd_HHmmss}.webm");

        var captureArgs = BuildCaptureArgs(profile, display);
        var args = $"-y {captureArgs} -t 2 -c:v libvpx -b:v {profile.BitrateKbps}k -deadline realtime \"{outputPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = new Process { StartInfo = psi };
        p.Start();
        await p.WaitForExitAsync(ct);

        if (p.ExitCode == 0 && File.Exists(outputPath))
        {
            return (true, $"VP8 probe OK: {outputPath}");
        }

        var err = await p.StandardError.ReadToEndAsync(ct);
        return (false, $"VP8 probe failed: {err}");
    }

    private static string BuildCaptureArgs(QualityProfile profile, DisplaySource? display)
    {
        if (display is null)
        {
            // Fallback если экран не указан: синтетический источник.
            return $"-f lavfi -i color=c=black:s={profile.Width}x{profile.Height}:r={profile.Fps}";
        }

        var captureWidth = Math.Min(profile.Width, display.Width);
        var captureHeight = Math.Min(profile.Height, display.Height);
        if (captureWidth <= 0 || captureHeight <= 0)
        {
            captureWidth = display.Width;
            captureHeight = display.Height;
        }

        return
            $"-f gdigrab -framerate {profile.Fps} " +
            $"-offset_x {display.X} -offset_y {display.Y} " +
            $"-video_size {captureWidth}x{captureHeight} " +
            "-i desktop";
    }
}
