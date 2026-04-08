using System.Windows.Media;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    private void OnIceCandidateObserved(string direction, string candidateType, string candidateIp, int candidatePort)
    {
        _uiContext.Post(_ =>
        {
            if (string.Equals(direction, "local", StringComparison.Ordinal))
            {
                var shouldPromote = IsBetterIceType(candidateType, _localIceCandidateType);
                _localIceCandidateType = SelectPreferredIceType(_localIceCandidateType, candidateType);
                if (shouldPromote && !string.IsNullOrWhiteSpace(candidateIp))
                {
                    _localIceCandidateIp = candidateIp;
                }
            }
            else
            {
                var shouldPromote = IsBetterIceType(candidateType, _remoteIceCandidateType);
                _remoteIceCandidateType = SelectPreferredIceType(_remoteIceCandidateType, candidateType);
                if (shouldPromote && !string.IsNullOrWhiteSpace(candidateIp))
                {
                    _remoteIceCandidateIp = candidateIp;
                }
            }

            var route = InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
            var routeLabel = IceRouteDisplayLabel(route);
            IcePathText  = $"ICE: {routeLabel} [hint] (local:{_localIceCandidateType}/{_localIceCandidateIp}, remote:{_remoteIceCandidateType}/{_remoteIceCandidateIp})";
            IcePathBrush = GetIcePathBrush(route);

            var item = $"{DateTime.Now:HH:mm:ss} {direction}:{candidateType} {candidateIp}:{candidatePort}";
            _iceRecentCandidates.Enqueue(item);
            while (_iceRecentCandidates.Count > 5)
            {
                _iceRecentCandidates.Dequeue();
            }
            IceRecentCandidatesText = string.Join(Environment.NewLine, _iceRecentCandidates.Reverse());
        }, null);
    }

    /// <summary>
    /// Select the BEST (most direct) ICE candidate type.
    /// WebRTC internally selects the best candidate pair, so tracking the best
    /// observed type better reflects the actual connection path.
    /// </summary>
    private static string SelectPreferredIceType(string current, string incoming)
    {
        var currentRank = IceTypeRank(current);
        var incomingRank = IceTypeRank(incoming);
        if (currentRank == 0) return incoming;  // unknown → anything is better
        if (incomingRank == 0) return current;  // don't replace with unknown
        return incomingRank < currentRank ? incoming : current;  // prefer lower rank = more direct
    }

    /// <summary>Returns true if incoming candidate type is better (more direct) than current.</summary>
    private static bool IsBetterIceType(string incoming, string current)
    {
        var currentRank = IceTypeRank(current);
        var incomingRank = IceTypeRank(incoming);
        return currentRank == 0 || (incomingRank > 0 && incomingRank < currentRank);
    }

    private static string InferRouteType(string localType, string remoteType)
    {
        if (string.Equals(localType, "relay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remoteType, "relay", StringComparison.OrdinalIgnoreCase))
        {
            return "relay";
        }
        if (string.Equals(localType, "srflx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remoteType, "srflx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(localType, "prflx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remoteType, "prflx", StringComparison.OrdinalIgnoreCase))
        {
            return "srflx";
        }
        if (string.Equals(localType, "host", StringComparison.OrdinalIgnoreCase)
            && string.Equals(remoteType, "host", StringComparison.OrdinalIgnoreCase))
        {
            return "host";
        }
        return "unknown";
    }

    /// <summary>User-friendly display label for ICE route type.</summary>
    private static string IceRouteDisplayLabel(string route)
    {
        return route.ToLowerInvariant() switch
        {
            "host"  => "LAN (прямое)",
            "srflx" => "NAT (STUN)",
            "relay" => "Relay (TURN)",
            _       => route
        };
    }

    /// <summary>
    /// Rank ICE candidate types for worst-case route detection.
    /// Higher rank = more indirect route. Used to track the "worst" observed candidate
    /// so InferRouteType can determine the actual connection path.
    /// </summary>
    private static int IceTypeRank(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "relay" => 4,
            "srflx" => 3,
            "prflx" => 2,
            "host"  => 1,
            _       => 0
        };
    }

    private static string GetIceCandidateType(string candidate)
    {
        if (candidate.Contains(" typ relay", StringComparison.OrdinalIgnoreCase)) return "relay";
        if (candidate.Contains(" typ srflx", StringComparison.OrdinalIgnoreCase)) return "srflx";
        if (candidate.Contains(" typ prflx", StringComparison.OrdinalIgnoreCase)) return "prflx";
        if (candidate.Contains(" typ host",  StringComparison.OrdinalIgnoreCase)) return "host";
        return "unknown";
    }

    private static Brush GetIcePathBrush(string route)
    {
        return route.ToLowerInvariant() switch
        {
            "host"  => Brushes.ForestGreen,
            "srflx" => Brushes.Goldenrod,
            "relay" => Brushes.DarkOrange,
            _       => Brushes.SlateGray
        };
    }
}
