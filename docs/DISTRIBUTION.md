# Building and distributing CardGrinder

## Downloadable files

- `CardGrinder-Setup.exe`: offline Windows 11 x64 installer, including the .NET runtime.
- `CardGrinder-win-x64.zip`: portable application; extract the entire folder before running it.
- `SHA256SUMS.txt`: checksums for both downloads.

The installer uses the current Windows account, adds a Start-menu entry, offers an optional desktop shortcut, and registers an uninstaller in Windows Settings. It requires no administrator rights. The default path is `%LOCALAPPDATA%\Programs\CardGrinder`. Upgrades use a stable installer AppId. A running CardGrinder instance must be closed before installing or uninstalling; the installer does not forcibly terminate farming.

Steam sessions/settings are separate at `%LOCALAPPDATA%\CardGrinder`. They are preserved across updates and uninstall/reinstall. Choose **Sign out** before uninstalling if you want the remembered Steam session removed. No user session or settings are included in any download.

## Host on your website

CardGrinder is published at [impatus.no](https://impatus.no/programs/cardgrinder).
For its signed update channel and repeatable website publishing, see
[UPDATES.md](UPDATES.md).

Upload `artifacts/release/CardGrinder-Setup.exe` to your download directory. This single file contains everything needed for installation; the portable ZIP is an alternative download.

For a link that always points at the latest GitHub release:

```html
<a href="https://github.com/impatus/CardGrinder/releases/latest/download/CardGrinder-Setup.exe">
  Download CardGrinder for Windows 11
</a>
```

Publish the matching checksum file alongside a directly hosted installer. For example:

```powershell
Get-FileHash ./CardGrinder-Setup.exe -Algorithm SHA256
```

The current installer and application are unsigned. A valid Windows code-signing certificate and a signing service/certificate configuration are needed to add a verified publisher signature. No certificate is invented or shipped in the repository. Users may see Windows reputation warnings for an unsigned, new download.

## Build locally

Install the .NET SDK version from `global.json`. Run:

```powershell
dotnet restore CardGrinder.sln --locked-mode
dotnet build CardGrinder.sln -c Release --no-restore -m:1
dotnet test tests/CardGrinder.Tests/CardGrinder.Tests.csproj -c Release --no-build --no-restore
./scripts/build-release.ps1
```

The packaging script downloads the pinned official Inno Setup compiler into `.build/tools` if necessary. It verifies both the pinned SHA-256 and the expected Authenticode publisher before installing the build tool for the current user. Set `ISCC_PATH` to use an already installed compiler instead. Compiler installation/download is a developer build step, never part of the user's CardGrinder installation.

The complete package is written to `artifacts/release`. The portable publish directory contains replaceable managed libraries, the self-contained runtime and license notices. Keep SteamKit2.dll separately replaceable and preserve all notices when redistributing.

## Publish a new version

1. Update `Version` in `Directory.Build.props`, release notes, and SDK/runtime pins as needed. Refresh NuGet lockfiles if dependencies change.
2. Run the Release checks and inspect the generated installer.
3. Commit the changes, create the matching `vX.Y.Z` tag and push the tag.
4. The Release workflow checks the version, tests, builds, and publishes the three download assets. An existing published release is left unchanged. The workflow can also be dispatched for an existing tag.

GitHub Actions pins its third-party action references to commits and uses read-only permissions for CI. Only the release job receives repository contents write permission. Releases contain no `.build`, `.packages`, `.research`, account data or signing keys.
