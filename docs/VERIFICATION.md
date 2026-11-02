# Verification

Checked for 0.2.0 on 2026-11-03: **92 tests passed on .NET 10**, Release build completed with **0 warnings and 0 errors**, and the dependency audit reported no known vulnerable packages. The public installer and portable ZIP include the pinned .NET 10 LTS runtime. The UI fixture screenshots are test artifacts, not a signed-in production session.

For 0.1.1 on 2026-10-15, live read-only checks reused the existing DPAPI-encrypted session with CardGrinder closed. Saved-session Steam authentication and the owned-games request succeeded. The original Badges URL returned HTTP 302 to a vanity-profile Badges URL; the corrected request followed it safely. The production library service successfully read all three pages and confirmed eligible owned games and explicitly completed games. No farming, game reporting, trading, inventory changes or game launches were performed during this check. No token, cookie or authenticated HTML was logged or saved.

For 0.1.2, two consecutive initialization/disposal cycles used the real WPF view model and the existing encrypted session. Both restored the Steam connection and loaded the library automatically, never displayed the QR overlay, and kept farming stopped. Repeated initialization of the same view model did not reconnect a second time. Ordinary disposal retained the saved session for the next cycle.

For 0.1.4 on 2026-10-17, a live read-only harness closed only its own Steam transport, delivered the corresponding network-interruption signal through an adapter, and let the production recovery worker reconnect with its normal delay. The first attempt restored the saved Steam session and rechecked the full live library. No QR challenge or farming occurred. System network/VPN settings were untouched. Running-queue recovery, progress preservation, repeated breaks, cancellation races and retry exhaustion were tested with controlled sessions in the automated suite; actual card receipt and a real VPN driver transition remain manual checks.

## Automated

The test suite covers:

- Normal, singular, zero, foil, seasonal and paginated Badges data.
- Rejection of login pages, public pages without private drop counts, unknown markup, conflicting counts and excessive pagination.
- Actual AppID/level/border identifiers, crafted badges without dialogs, and the last page's `page_current` span.
- Numeric-to-vanity HTTPS redirects, relative redirects, page/language preservation and request pacing on every hop.
- Blocking of cross-host/downgrade/non-Badges redirects, credentials in redirect URLs, different numeric accounts, redirect loops and excessive hops.
- Distinct authentication rejections, HTTP rate limits, and correct unavailable/loading/filtered/confirmed-empty UI states.
- Single-game queue advancement, simultaneous selection, disabled queue behavior, confirmed completion and rising available counts.
- Unknown/complete AppID rejection and the playtime preference.
- Pausing on transport loss, another playing session, request failures and missing queued games.
- Recovery of a running queue after network loss, including progress, completion, batch mode and disabled automatic queueing.
- No automatic farming for manually paused/idle queues, forced sign-out or playing conflicts.
- Cancellation during backoff, an active connection attempt and post-reconnect verification; late success cannot override Pause or Stop.
- Duplicate disconnect events, repeated interruptions during verification, bounded exhaustion and immediate stop on permanent errors.
- Network-only HTTP errors can recover before the Steam CM transport reports its own disconnect.
- Immediate presence clearing and cancellation of a refresh when Stop is requested.
- Clean engine disposal.
- Windows DPAPI round-trip, ciphertext without plaintext credentials, and saved-session deletion.
- A new storage instance sees the same encrypted session; sign-out removes its startup availability.
- Missing or unreadable saved sessions cannot trigger a background QR challenge or a network connection.
- A fresh profile without a saved session remains disconnected without errors when startup initialization is called repeatedly.
- Sanitized untrusted network errors.
- Offline rendering of a populated WPF dashboard at regular/compact sizes, without binding errors.
- The real Select all checkbox: selecting, clearing, partial selection, filtering and exclusion of completed games.
- Active games preceding other games in every sort mode, and active-row changes as the queue advances.

The executable's smoke-test mode also renders the actual empty dashboard and settings at both supported sizes. Screenshots were inspected for contrast, wrapping, spacing and clipping. Release compilation and self-contained Windows publishing are checked separately.

Installer lifecycle verification is provided by `scripts/test-installer.ps1`: it installs into a unique directory under `.build`, verifies the uninstall registration and Start-menu shortcut, performs a same-version upgrade, runs the installed app's offline UI checks, and uninstalls it. It checks that existing remembered-session/settings files retain their size and modification time. The test refuses to replace a pre-existing installation. CI runs this check before uploading artifacts or publishing a new release.

## Requires a real Steam account

Saved-session authentication and complete Badges discovery were verified against the real account as described above. Actual farming and card receipt remain unverified; elapsed time and fixture data cannot establish those outcomes. Complete the following remaining behavioral checks with your own account:

1. Scan and approve a QR in the Steam mobile app. Compare persona/avatar with Steam and cancel a second QR attempt to verify cancellation.
2. Compare several returned AppIDs and remaining counts with your own Badges page, including a completed game and, if applicable, a multi-page library.
3. Select one game with verified drops. Start and check Steam's reported game. Confirm that no game process launches.
4. Wait for an actual drop. After the next library check, compare the per-game count, session total and progress bar with Badges. Do not interpret elapsed time as a promised drop.
5. Test Pause, Resume and Stop. With two selected games, confirm automatic progression after the first game's final drop. Simultaneous mode should report only selected games.
6. Start a normal game in another Steam client. CardGrinder must pause when Steam sends the conflict callback, remain paused when the game exits, and require manual Resume.
7. While farming, briefly toggle the VPN or disconnect/reconnect the network. Verify RECONNECTING, preservation of queue/progress, and automatic continuation after successful card verification. Repeat while manually paused and verify it reconnects without farming. During recovery, test Pause, Stop and Sign out; attempts must stop and a late result must not restart farming. A long outage that exceeds five attempts must leave a visible error for manual recovery.
8. Minimize and restore via tray; pause/resume from its menu. Exit while farming and while a refresh is waiting. Verify that the separate connection ends.
9. Restart to verify encrypted session reuse; Sign out and verify `session.bin` is removed. A new connection should require QR approval.
10. Check an expired web session, Steam HTTP 429/503 and changed HTML when such conditions are available. The expected result is a visible error and paused farming, never a fabricated zero count.

The unsigned installer/EXE and undocumented Badges parser remain release limitations. Portable builds must stay together; the installer manages those files for installed builds.
