# Application updates

**Settings > Updates > Check for updates** checks the signed channel at
`https://impatus.no/uploads/cardgrinder/latest.json`. Checks are manual; nothing
downloads or installs automatically. Installed editions download an EXE and
portable editions a ZIP. Downloads show progress and support cancellation.

The app verifies RSA-PSS/SHA-256 release signatures using a dedicated embedded
3072-bit public key, then verifies the signed artifact size and SHA-256 before
offering it and again before opening it. Only newer stable three-part versions
for CardGrinder and HTTPS packages on the channel's origin are accepted.
Redirects, credentials in URLs, oversized metadata and oversized downloads are
rejected. Requests have bounded timeouts. Partial downloads are removed.

**Install and close CardGrinder** stops farming, opens the normal installer with
the existing install directory, then shuts down the Steam connection and app.
The installer retains login and settings. Cancelling its wizard leaves the
existing version installed. Portable editions offer **Open update folder**;
close the app and extract the complete ZIP over the portable copy. Downloads
remain in `%LOCALAPPDATA%/CardGrinder/updates`; they are not removed while an
installer may still use them. These signatures do not supply Windows Authenticode
code signing: Windows may still show an unknown publisher warning.

## Release publication

1. Update `Directory.Build.props`, `docs/RELEASE-NOTES.md` and the short catalog
   text in `docs/website-entry.json`; run tests, push and tag the new release.
2. Download the exact GitHub release assets into `artifacts/release` and verify
   `SHA256SUMS.txt`. Do not publish independently rebuilt bytes under the same
   version.
3. Run `scripts/publish-site-assets.ps1 -PrepareOnly` to prepare signed metadata.
   Run without the switch to upload immutable versioned EXE/ZIP/screenshot files
   to the website's `cardgrinder/` R2 prefix. Complete public downloads are hashed
   before `latest.json` is activated last. The site must serve it with `no-store`.
4. Run `scripts/publish-website.ps1` to also save the catalog entry with the normal
   admin API; it prompts locally for the website password and never saves it.

The scripts use the existing `impatus-web` checkout and Wrangler login, following
Playboi's publication workflow. Identical retries are supported; older channel
versions and changed files under an existing version are rejected. The private
key lives outside the repository at
`%LOCALAPPDATA%/CardGrinderReleaseSigning/private.pem`. Back it up securely. Never
upload it or reuse another app's key. Public configuration is embedded from
`src/CardGrinder.Core/UpdateChannel.json`. Key rotation requires an explicit
trust migration and a new app distribution.

Versions before 0.3.0 have no updater and need one manual installation to receive
this feature. Tests use ephemeral keys and offline HTTP handlers; they do not
contact the live channel, alter Steam sessions or execute downloaded installers.
