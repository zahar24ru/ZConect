using System.Windows.Media;

namespace UiApp.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Last route/IPs logged via "ice_selected_pair" snapshot. Used to dedupe log spam
    /// when the pair didn't actually change (mrwebrtc calls OnIceCandidateObserved on every
    /// candidate exchange, but most don't promote the best-observed pair).</summary>
    private string _lastLoggedSelectedPairKey = string.Empty;

    /// <summary>R3: отдельный dedupe flag для "password_approved_viewer_ice_established" event.
    /// Эмитим один раз per connection (при первом pair promotion), независимо от того какой
    /// pair последует; повторные не нужны (session_id не меняется внутри одного connect'а).</summary>
    private bool _passwordApprovalEndpointLogged;

    private void OnIceCandidateObserved(string direction, string candidateType, string candidateIp, int candidatePort)
    {
        _uiContext.Post(_ =>
        {
            bool pairPromoted = false;
            if (string.Equals(direction, "local", StringComparison.Ordinal))
            {
                var shouldPromote = IsBetterIceType(candidateType, _localIceCandidateType);
                _localIceCandidateType = SelectPreferredIceType(_localIceCandidateType, candidateType);
                if (shouldPromote && !string.IsNullOrWhiteSpace(candidateIp))
                {
                    _localIceCandidateIp = candidateIp;
                    pairPromoted = true;
                }
            }
            else
            {
                var shouldPromote = IsBetterIceType(candidateType, _remoteIceCandidateType);
                _remoteIceCandidateType = SelectPreferredIceType(_remoteIceCandidateType, candidateType);
                if (shouldPromote && !string.IsNullOrWhiteSpace(candidateIp))
                {
                    _remoteIceCandidateIp = candidateIp;
                    pairPromoted = true;
                }
            }

            var route = InferRouteType(_localIceCandidateType, _remoteIceCandidateType);
            var routeLabel = IceRouteDisplayLabel(route, _localIceCandidateIp, _remoteIceCandidateIp);
            IcePathText  = $"ICE: {routeLabel} (local:{_localIceCandidateType}/{_localIceCandidateIp}, remote:{_remoteIceCandidateType}/{_remoteIceCandidateIp})";
            IcePathBrush = GetIcePathBrush(route);

            // Emit a structured "selected pair" log line when the best-observed pair changes.
            // Post-mortem diagnostics: которая TURN была выбрана, пошли ли мы через VPN и т.д.
            if (pairPromoted)
            {
                var key = $"{route}|{_localIceCandidateType}/{_localIceCandidateIp}|{_remoteIceCandidateType}/{_remoteIceCandidateIp}";
                if (!string.Equals(key, _lastLoggedSelectedPairKey, StringComparison.Ordinal))
                {
                    _lastLoggedSelectedPairKey = key;
                    _logService.Info("ICE", $"ice_selected_pair route={route} local={_localIceCandidateType}/{_localIceCandidateIp} remote={_remoteIceCandidateType}/{_remoteIceCandidateIp} label=\"{routeLabel}\"");

                    // R3 audit enrichment: если viewer подключился через password auto-approve —
                    // отдельный высокоприоритетный log c remote endpoint'ом. При relay это IP TURN
                    // сервера, не viewer'а напрямую, но post-hoc forensics (вместе с server-side
                    // access log'ом на этот TURN allocation) может восстановить source. Шлём
                    // event только раз per connection (после того как pair promoted до rank > 0).
                    if (_role == ConnectionRole.Host
                        && _wasAutoApprovedViaPassword
                        && !_passwordApprovalEndpointLogged
                        && !string.Equals(_remoteIceCandidateIp, "n/a", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(_remoteIceCandidateIp))
                    {
                        _passwordApprovalEndpointLogged = true;
                        _logService.Info("UnattendedAuth",
                            $"password_approved_viewer_ice_established session_id={_currentSessionId} " +
                            $"route={route} remote={_remoteIceCandidateType}/{_remoteIceCandidateIp} " +
                            $"local={_localIceCandidateType}/{_localIceCandidateIp}");
                    }
                }
            }

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

    /// <summary>
    /// User-friendly display label. Для "host" type дополнительно инспектируем IP
    /// — "host" ICE candidate означает любой directly-bound network interface,
    /// НЕ обязательно физический LAN. Может быть VPN туннель, Teredo IPv6-over-IPv4,
    /// loopback, публичный IP напрямую. Раньше всегда показывали "LAN (прямое)" —
    /// вводило в заблуждение (2026-04-18: user с VPN увидел "LAN" и не понял что
    /// идёт через VPN туннель).
    /// Для "relay" — показать IP allocated by TURN server (multi-TURN diagnostics:
    /// видно какой именно сервер из TurnServers[] был выбран mrwebrtc).
    /// </summary>
    private string IceRouteDisplayLabel(string route, string localIp, string remoteIp)
    {
        return route.ToLowerInvariant() switch
        {
            "host"  => BuildHostLabel(localIp, remoteIp),
            "srflx" => "P2P через NAT (STUN)",
            "relay" => BuildRelayLabel(localIp),
            _       => route
        };
    }

    /// <summary>
    /// Resolve relay candidate IP to configured TURN server URL (for multi-TURN diagnostic).
    /// mrwebrtc picks a relay candidate from one of IceServers — its IP is the server-allocated
    /// address (typically the TURN server's public IP for single-homed coturn). Match against
    /// primary TurnUrl + TurnServers list to show which server is actually carrying traffic.
    /// </summary>
    private string BuildRelayLabel(string localRelayIp)
    {
        if (string.IsNullOrWhiteSpace(localRelayIp) || localRelayIp == "n/a")
            return "Relay через TURN";
        if (TryExtractHost(_settings.TurnUrl, out var primaryHost)
            && string.Equals(primaryHost, localRelayIp, StringComparison.OrdinalIgnoreCase))
            return $"Relay через TURN ({primaryHost})";
        if (_settings.TurnServers is not null)
        {
            foreach (var s in _settings.TurnServers)
            {
                if (s is null) continue;
                if (TryExtractHost(s.Url, out var host)
                    && string.Equals(host, localRelayIp, StringComparison.OrdinalIgnoreCase))
                    return $"Relay через TURN ({host})";
            }
        }
        return $"Relay через TURN ({localRelayIp})";
    }

    /// <summary>Extract host из turn:host:port / turns:host:port / host:port. Returns false if empty.</summary>
    private static bool TryExtractHost(string? url, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var s = url.Trim();
        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0) s = s[(schemeIdx + 3)..];
        else if (s.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)) s = s[5..];
        else if (s.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)) s = s[6..];
        var queryIdx = s.IndexOf('?');
        if (queryIdx >= 0) s = s[..queryIdx];
        var colonIdx = s.LastIndexOf(':');
        if (colonIdx > 0 && !s.Contains("::", StringComparison.Ordinal))
            s = s[..colonIdx];
        host = s;
        return host.Length > 0;
    }

    private static string BuildHostLabel(string localIp, string remoteIp)
    {
        var localClass = ClassifyHostAddress(localIp);
        var remoteClass = ClassifyHostAddress(remoteIp);
        if (string.Equals(localClass, remoteClass, StringComparison.Ordinal))
            return $"Прямое: {localClass}";
        return $"Прямое: {localClass} ↔ {remoteClass}";
    }

    /// <summary>
    /// Classify an IP address by scope/type. Helps distinguish настоящий LAN от
    /// VPN туннеля (оба дают "host" candidate в WebRTC).
    /// </summary>
    private static string ClassifyHostAddress(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip == "n/a") return "интерфейс неизвестен";

        // IPv6 — содержит ":"
        if (ip.Contains(':'))
        {
            if (ip == "::1") return "loopback IPv6";
            if (ip.StartsWith("fe80", StringComparison.OrdinalIgnoreCase)) return "link-local IPv6 (LAN)";
            // fc00::/7 — Unique Local Address, обычно VPN (WireGuard/OpenVPN/Tailscale)
            if (ip.Length >= 2)
            {
                var prefix = ip.Substring(0, 2).ToLowerInvariant();
                if (prefix == "fc" || prefix == "fd") return "ULA IPv6 (VPN туннель)";
            }
            // 2001:0000::/32 — Teredo (IPv6-over-IPv4 tunneling через Microsoft)
            if (ip.StartsWith("2001:0:", StringComparison.OrdinalIgnoreCase)
                || ip.StartsWith("2001::", StringComparison.OrdinalIgnoreCase))
                return "Teredo IPv6 (тоннель)";
            if (ip.StartsWith("2002:", StringComparison.OrdinalIgnoreCase)) return "6to4 IPv6 (тоннель)";
            return "публичный IPv6";
        }

        // IPv4
        if (ip.StartsWith("127.")) return "loopback IPv4";
        if (ip.StartsWith("169.254.")) return "link-local IPv4 (APIPA)";
        if (ip.StartsWith("10.") || ip.StartsWith("192.168.")) return "LAN IPv4";
        if (ip.StartsWith("172."))
        {
            var parts = ip.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var second) && second >= 16 && second <= 31)
                return "LAN IPv4";
        }
        return "публичный IPv4";
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
