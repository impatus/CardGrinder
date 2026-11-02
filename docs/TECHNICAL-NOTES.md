# Technical decisions

Research checked on 2026-10-15 against SteamKit2 **3.4.0**, the latest stable version listed by NuGet. This package supports .NET 8 and .NET 10. Public builds use the .NET 10 SDK pinned in global.json and the self-contained .NET 10 runtime pinned in build.json. .NET 10 LTS replaces the earlier .NET 8 development target.

## Authentication

Use `BeginAuthSessionViaQRAsync`, update the local QR on `ChallengeURLChanged`, honor the session's polling interval, and pass cancellation to `PollingWaitForResultAsync`. Authenticate the CM connection using the returned **refresh token** in `SteamUser.LogOnDetails.AccessToken`, matching SteamKit2's samples. Persistent authentication and `ShouldRememberPassword` are enabled together. A fixed per-application LoginID distinguishes the separate client.

The web cookie uses `SteamID||AccessToken`, URI-escaped and set only on direct HTTPS requests to `steamcommunity.com`. Renew the in-memory access token after eight hours using `GenerateAccessTokenForAppAsync`; the refresh token is never placed in a URL or unencrypted settings file. Automatic HTTP redirects are disabled. Up to three manual redirects are permitted only to HTTPS Badges routes on the exact same Steam Community host and default port. This supports Steam's normal numeric-profile-to-vanity-profile canonicalization. Other hosts, HTTP downgrades, credentials in URLs, non-Badges paths, different numeric accounts and redirect loops are rejected before another request is sent. The requested page and English language survive canonicalization. Responses have a 30-second timeout and an 8 MiB limit. Login is cancellable and expires after three minutes.

From 0.1.2, the normal WPF startup calls `MainViewModel.InitializeAsync` once after showing or minimizing the window. If an encrypted saved session exists, it restores that session in the background and refreshes the library. No QR overlay is opened by this startup path and no farming is started. A missing or unreadable session never falls back to an automatic QR challenge. Explicit sign-out deletes the saved session; ordinary Exit preserves it. Failed startup authentication is reported without a retry loop. The offline smoke-test path deliberately skips automatic connection.

References: [SteamKit2 package](https://www.nuget.org/packages/SteamKit2/3.4.0), [official QR sample](https://github.com/SteamRE/SteamKit/blob/3.4.0/Samples/001_AuthenticationWithQrCode/Program.cs), [official cookie sample](https://github.com/SteamRE/SteamKit/blob/3.4.0/Samples/002_WebCookie/Program.cs).

## Ownership and card discovery

The authenticated `Player.GetOwnedGames` unified message verifies ownership and supplies names/playtime, without asking for a Web API key. Authenticated `/profiles/{id}/badges/?p=N&l=english` pages provide normal-card remaining counts. Foil badges are excluded because they do not represent a separate playtime-drop allocation. Seasonal/community badges are excluded. All pages must validate; the library is intersected with owned AppIDs. The parser understands only explicit English remaining-drop text, never arbitrary digits or absent markup.

Live verification for 0.1.1 established the dialog identifier format `card_drop_info_gamebadge_{appid}_{craftedLevel}_{border}`. Border 1 denotes foil; crafted level 1 alone does not. Some crafted badges have no drop dialog, including on authenticated pages. Those rows remain unknown and are skipped. Private-data visibility is checked over all pages so a page containing only such crafted badges does not invalidate another page's explicit drop counts. A library with game badges but no private drop counts at all is rejected rather than reported as empty. Game-card overlay links are siblings of the inner badge row in Steam's HTML.

Pagination reads both `pagelink` anchors and the current page's `page_current` span. Reading only anchors undercounts the last page because its own page number is not a link.

There is no documented SteamKit2 card-drop-discovery method. This narrow HTML parser is isolated so it can be repaired if Steam changes its markup. Current selector conventions were cross-checked against the maintained [ArchiSteamFarm CardsFarmer source](https://github.com/JustArchiNET/ArchiSteamFarm/blob/main/ArchiSteamFarm/Steam/Cards/CardsFarmer.cs); no ASF implementation or authentication secrets are bundled. Public Badges HTML was also inspected and confirmed not to expose the private drop counts needed here.

Valve's [trading-card FAQ](https://steamcommunity.com/tradingcards/faq/) and [Steamworks Community Items documentation](https://partner.steamgames.com/doc/marketing/tradingcards) describe playtime-based card drops and eligibility. They do not guarantee third-party automation, fixed timing, or improved rates from multiple simultaneous games. No such guarantees are made here.

## Farming and conflict handling

SteamKit2 exposes no higher-level card-farming method. `ClientGamesPlayed` is the established Steam protocol message for reporting played games through a separate client; it does not run or modify a local game. Only AppIDs verified in the authenticated owned-library/Badges intersection are reported. This communicates playtime to Steam while waiting for cards; playtime cannot be separated technically from playtime-based drops, and can affect refund eligibility.

The engine owns state transitions and a selected-game queue. The default active batch is one game. Only confirmed decreases for actively reported games add to session progress; rising counts, inactive games and missing rows cannot fabricate drops. A completed game's AppID is removed before the next batch is reported. Polling pauses on failure rather than continuing with stale eligibility.

SteamKit2's [PlayingSessionStateCallback documentation](https://github.com/SteamRE/SteamKit/blob/3.4.0/SteamKit2/SteamKit2/Steam/Handlers/SteamUser/Callbacks.cs) explicitly warns that sending GamesPlayed while blocked can cause `LoggedInElsewhere`. Consequently the transport prevents **all** GamesPlayed messages while blocked, including empty ones. The server's blocked state takes precedence; local queue state pauses and never tries to seize the playing session. On a normal stop, pause or logout, an empty GamesPlayed list clears presence before logoff. Loss of the separate transport also ends its reported session.

From 0.1.4, unexpected transport loss and network-only HTTP/timeouts trigger one bounded recovery worker. SteamKit2's `DisconnectedCallback.UserInitiated` distinguishes a deliberate transport close from an unexpected disconnect. Logged-off results indicating transport problems are handled as network loss; other server-ended sessions require manual action. Failed connection attempts do not emit a new recovery event recursively.

The worker waits 5, 15, 30, 60 and 120 seconds before its five attempts. Each automatic attempt performs one saved-session transport connection, avoiding nested retry multiplication. It never opens a QR challenge. After reconnection it verifies the entire library and queued games before reporting AppIDs. Previously active games can contribute a confirmed remaining-count reduction across the short interruption, while missing rows never count as received cards. Queue order, batching policy, active elapsed time and existing progress are preserved. A completed active batch advances only when automatic queueing is enabled.

Pause, Stop, reset, sign-out, game conflicts and disposal cancel recovery and advance an intent version. A late result from an older version cannot resume a stopped queue. Duplicate disconnect events share one worker; further network loss during verification consumes the same finite retry budget. Cancellation also interrupts the post-reconnect library request. A manually paused or idle connection can reconnect, but it stays paused or idle. Authentication rejections, HTTP status rejections/rate limits and changed Badges data terminate recovery without retries. Exceeding the retry budget leaves a visible fault and requires manual reconnection. Monitor error handling cannot overwrite a successful recovery or a newer manual stop.

Startup still connects without starting farming. Playing-session conflicts and parsing failures always require explicit recovery by the user. See the [SteamKit2 disconnect callback source](https://github.com/SteamRE/SteamKit/blob/3.4.0/SteamKit2/SteamKit2/Steam/SteamClient/Callbacks.cs) for the origin of the disconnect classification.

## Scope

One Windows user, one Steam account, one local process. No achievements, stats modification, trading, inventory actions, Market operations, bypasses, local process inspection or game launch code. The only native Windows call is a DWM title-bar appearance setting. Artwork uses Steam-hosted image URLs. WPF and WinForms are used only for the native window and tray menu.
