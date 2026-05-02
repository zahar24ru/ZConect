# Security Policy

## Reporting a vulnerability

If you discover a security vulnerability in ZConnect, **please do not open a public GitHub issue.**

Email the maintainer directly: **<zaharovkostia@yandex.ru>**

When reporting, please include:

1. **What is vulnerable** — component (WPF client, Android viewer, signaling server, deploy script), file path if known
2. **Impact** — what can an attacker do? (credential theft, RCE, DoS, info disclosure, etc.)
3. **Reproduction** — minimal steps or a proof-of-concept
4. **Affected versions** — which ZConnect version(s) are vulnerable (Settings → About in the client)
5. **Disclosure preference** — would you like to be credited in the fix's release notes?

## Response commitment

- **Acknowledgment** — within **7 days** of your email, we'll confirm receipt
- **Triage** — within **14 days**, we'll classify severity (Critical / High / Medium / Low) and provide an estimated timeline
- **Fix** — depending on severity:
  - Critical / High → patch within **30 days**
  - Medium → patch in next minor release
  - Low → patch in next regular release cycle
- **Disclosure** — coordinated. We'll work with you on a disclosure date that allows users to update before public details are released. Default embargo is 90 days from acknowledgment.

## Scope

In-scope vulnerabilities (these we want to hear about):

- Remote code execution in client or server
- Authentication / authorization bypass (session takeover, admin panel bypass, viewer-confirmation bypass)
- Cryptographic flaws (DPAPI misuse, TURN credential generation, password hashing)
- Injection vulnerabilities (path traversal in file transfer, command injection in deploy scripts)
- Denial of service that takes the signaling server offline with low effort
- Information disclosure beyond what's in the [Privacy Policy](docs/PRIVACY_POLICY.md)
- TLS/HTTPS misconfigurations enabling MITM
- Memory safety issues in native code interop (P/Invoke, mrwebrtc.dll boundary)

Out-of-scope (these are known limitations or design decisions, not vulnerabilities):

- **Win+L lock screen clicks don't work** — Windows kernel filters synthetic input on the secure desktop. By design; would require a WHQL-signed kernel driver. Documented in `docs/ROADMAP.md`.
- **`mrwebrtc.dll 2.0.2` is from 2020** — known technical debt, migration to SIPSorcery is on the roadmap. Not a current vulnerability per se but a stale dependency.
- **Microsoft.MixedReality.WebRTC archived** — same root cause; see ROADMAP.
- **HTTP mode (Mode B in deploy guide) sends codes in plaintext** — by design, for LAN/dev only. Use HTTPS mode for public deployments.
- Self-XSS in admin panel when admin pastes attacker-controlled content into their own session
- Issues in Yandex.Metrika or `ip-api.com` — those are third-party services
- Vulnerabilities in dependencies (mrwebrtc, coturn, Caddy, .NET runtime) — please report upstream

## Coordinated disclosure

If you'd like to publicly disclose your finding (e.g., write a blog post, present at a conference), we ask that you:

1. Wait until a fix is released and users have had at least **30 days** to update
2. Coordinate the disclosure date with us — we'll publish release notes that credit you (if you want)
3. Avoid disclosing exploit details that would enable widespread attacks against unpatched users

## Recognition

If you provide a valid security report, we'll:

- Credit you in the release notes (with your preferred name/handle) — unless you prefer to remain anonymous
- Acknowledge you in this `SECURITY.md` once we have a "Hall of Fame" worth mentioning
- Send a heartfelt thank-you 🙏

ZConnect is a small open-source project without a bug bounty budget. We can't pay for findings, but we genuinely appreciate every report that improves the project's security.

## What's protected

The maintainer takes the following measures to keep the production signaling server (`connect.zconn.ru` — the community server) secure:

- HTTPS via Caddy + automatic Let's Encrypt
- Admin panel: bcrypt cost 12 password hashing + CSRF + rate-limit + IP whitelist option
- Per-session brute-force lockout (15s → 2m → 15m → 1h → permanent)
- TURN credentials rotated via RFC 7635 (HMAC short-term creds)
- Audit log of admin actions (NDJSON, 10 MB rotation × 5 keep)
- Telemetry data is anonymous heartbeat only — see [Privacy Policy](docs/PRIVACY_POLICY.md)
- VPS hardened: SSH key-only login, ufw firewall, fail2ban, automatic security updates

## Maintainer

Konstantin Zakharov · <zaharovkostia@yandex.ru>

PGP key for encrypted communication: not available yet (planned). For now, plain email is fine for initial contact — we can move to a secure channel before exchanging exploit details.
