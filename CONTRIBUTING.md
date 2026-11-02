# Contributing

Build on Windows with the SDK listed in `global.json`. Run `dotnet restore CardGrinder.sln --locked-mode`, then a Release build and the tests before opening a pull request. Installer instructions are in [docs/DISTRIBUTION.md](docs/DISTRIBUTION.md).

Keep the app focused on remaining trading card drops. Achievement/stat modification, game injection, trading/Market automation, multi-account support and restriction bypasses are outside the project scope.

Use English in source, UI, issues and pull requests. Keep Steam transport, Badges parsing and queue behavior separate from the WPF view. Describe the problem, resulting behavior and relevant validation. Tests must stay offline and must not require a real Steam login; sanitized fixtures belong only in tests.

Never commit credentials, QR challenges, `session.bin`, user settings, private diagnostics or signing keys. See [SECURITY.md](SECURITY.md) for sensitive reports. Contributions are provided under the project's MIT license, with third-party components retaining their own licenses.
