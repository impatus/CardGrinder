# Security

Report sensitive problems using [GitHub's private vulnerability reporting](https://github.com/impatus/CardGrinder/security/advisories/new). Do not include Steam passwords, live QR challenges, tokens, cookies, encrypted session files or personal screenshots in public issues.

The latest published release is the supported version. CardGrinder uses a self-contained .NET runtime, so application releases must also update the bundled runtime when security fixes are available. Runtime and installer-tool versions are pinned in `build.json`; the SDK is pinned in `global.json`.

The application stores a remembered session with Windows DPAPI for the current user. It does not provide isolation from other programs already running as that same Windows user. The repository and release assets must never contain `session.bin`, signing certificates or private diagnostic captures.

Releases are currently unsigned. SHA-256 checksums detect download corruption; they are not a publisher signature. Do not disable Windows security software to install this application.

CardGrinder makes no guarantee about Valve's acceptance of third-party automation or VAC safety. Steam authentication, playing-session restrictions and rate limits are respected.
