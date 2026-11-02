using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace CardGrinder.Core;

public sealed class SteamSession : ISteamSession, IAsyncDisposable
{
    private readonly SteamClient client = new();
    private readonly CallbackManager callbacks;
    private readonly SteamUser user;
    private readonly SteamFriends friends;
    private readonly LocalStorage storage;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly object sync = new();
    private readonly Task callbackLoop;
    private TaskCompletionSource<bool>? connected;
    private TaskCompletionSource<SteamUser.LoggedOnCallback>? loggedOn;
    private TaskCompletionSource? disconnected;
    private string? refreshToken;
    private string? accessToken;
    private DateTimeOffset tokenRefreshed;
    private bool online;
    private bool blocked;
    private SteamProfile? profile;
    public bool IsLoggedOn { get { lock (sync) return online; } }
    public bool PlayingBlocked { get { lock (sync) return blocked; } }
    public SteamProfile? Profile { get { lock (sync) return profile; } }
    public event Action<SteamInterruption>? Interrupted;
    public event Action<SteamProfile?>? ProfileChanged;
    public event Action<string>? QrChallengeChanged;
    public event Action<string>? ConnectionChanged;

    public SteamSession(LocalStorage storage)
    {
        this.storage = storage;
        user = client.GetHandler<SteamUser>()!;
        friends = client.GetHandler<SteamFriends>()!;
        callbacks = new CallbackManager(client);
        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected?.TrySetResult(true));
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(c =>
        {
            LostConnection("Disconnected from Steam.", c.UserInitiated ? null : SteamInterruption.Network);
            disconnected?.TrySetResult();
        });
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(c => loggedOn?.TrySetResult(c));
        callbacks.Subscribe<SteamUser.LoggedOffCallback>(c => LostConnection($"Steam signed out ({c.Result}).",
            IsTransportResult(c.Result) ? SteamInterruption.Network : SteamInterruption.SessionEnded));
        callbacks.Subscribe<SteamUser.PlayingSessionStateCallback>(c =>
        {
            lock (sync) blocked = c.PlayingBlocked;
            if (c.PlayingBlocked)
            {
                Interrupted?.Invoke(SteamInterruption.PlayingConflict);
                ConnectionChanged?.Invoke($"Another Steam session is playing App {c.PlayingAppID}. Farming is paused.");
            }
        });
        callbacks.Subscribe<SteamUser.AccountInfoCallback>(c =>
        {
            SteamProfile? current;
            lock (sync) { if (profile is not null) profile = profile with { Name = c.PersonaName }; current = profile; }
            if (current is not null) ProfileChanged?.Invoke(current);
        });
        callbacks.Subscribe<SteamFriends.PersonaStateCallback>(c =>
        {
            SteamProfile? current;
            lock (sync)
            {
                if (profile is null || c.FriendID.ConvertToUInt64() != profile.SteamId) return;
                var avatar = c.AvatarHash is { Length: 20 } && c.AvatarHash.Any(b => b != 0)
                    ? $"https://avatars.steamstatic.com/{Convert.ToHexString(c.AvatarHash).ToLowerInvariant()}_medium.jpg" : profile.AvatarUrl;
                profile = profile with { Name = string.IsNullOrWhiteSpace(c.Name) ? profile.Name : c.Name, AvatarUrl = avatar };
                current = profile;
            }
            ProfileChanged?.Invoke(current);
        });
        callbackLoop = Task.Run(() =>
        {
            try { while (!lifetime.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(250)); }
            catch { LostConnection("The Steam connection stopped unexpectedly. Restart CardGrinder.", SteamInterruption.SessionEnded); }
        });
    }

    public Task ConnectAsync(bool freshQr, CancellationToken ct, bool savedSessionOnly = false)
        => ConnectCoreAsync(freshQr, ct, savedSessionOnly, 3);

    public Task ReconnectAsync(CancellationToken cancellationToken)
        => ConnectCoreAsync(false, cancellationToken, true, 1);

    private async Task ConnectCoreAsync(bool freshQr, CancellationToken ct, bool savedSessionOnly, int transportAttempts)
    {
        await connectionGate.WaitAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        try
        {
            if (IsLoggedOn) return;
            SavedSession? saved = null;
            if (!freshQr)
            {
                try { saved = storage.LoadSession(); }
                catch (System.Security.Cryptography.CryptographicException) { storage.DeleteSession(); }
            }
            // Background startup must never open a new QR challenge if the saved session is unavailable.
            if (savedSessionOnly && saved is null)
                throw new InvalidOperationException("The saved Steam session could not be restored. Choose Connect Steam to sign in again.");
            // A server logoff can leave the underlying CM transport open. Close it before reconnecting.
            if (client.IsConnected)
            {
                disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Disconnect();
                await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
            ConnectionChanged?.Invoke("Connecting to Steam…");
            // Bounded transport retries. Authentication failures are never retried automatically.
            for (int attempt = 0; ; attempt++)
            {
                connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Connect();
                try { await connected.Task.WaitAsync(TimeSpan.FromSeconds(20), token); break; }
                catch (Exception ex) when ((ex is TimeoutException or IOException) && attempt + 1 < transportAttempts)
                {
                    client.Disconnect();
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), token);
                }
            }
            loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
            string accountName;
            if (saved is null)
            {
                ConnectionChanged?.Invoke("Scan the QR code with the Steam mobile app, then approve Steam Guard.");
                var auth = await client.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                {
                    IsPersistentSession = true,
                    DeviceFriendlyName = "CardGrinder on Windows"
                }).WaitAsync(TimeSpan.FromSeconds(30), token);
                auth.ChallengeURLChanged = () => QrChallengeChanged?.Invoke(auth.ChallengeURL);
                QrChallengeChanged?.Invoke(auth.ChallengeURL);
                var result = await auth.PollingWaitForResultAsync(token);
                token.ThrowIfCancellationRequested();
                refreshToken = result.RefreshToken; accessToken = result.AccessToken;
                accountName = result.AccountName;
            }
            else { accountName = saved.AccountName; refreshToken = saved.RefreshToken; }
            token.ThrowIfCancellationRequested();
            user.LogOn(new SteamUser.LogOnDetails { Username = accountName, AccessToken = refreshToken, ShouldRememberPassword = true, LoginID = 0x43475244 });
            var logon = await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            if (logon.Result != EResult.OK || logon.ClientSteamID is null)
            {
                if (IsTransportResult(logon.Result)) throw new IOException("Steam is temporarily unavailable during sign-in.");
                if (logon.Result is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired) storage.DeleteSession();
                throw new InvalidOperationException($"Steam sign-in failed ({logon.Result}). Use New QR sign-in to authenticate again.");
            }
            if (saved is not null)
            {
                var generated = await client.Authentication.GenerateAccessTokenForAppAsync(logon.ClientSteamID, refreshToken!).WaitAsync(TimeSpan.FromSeconds(30), token);
                accessToken = generated.AccessToken;
            }
            token.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (!client.IsConnected) throw new InvalidOperationException("Steam disconnected during sign-in. Try again.");
                tokenRefreshed = DateTimeOffset.UtcNow;
                profile = new(logon.ClientSteamID.ConvertToUInt64(), accountName, null);
                online = true;
            }
            storage.SaveSession(new(accountName, refreshToken!));
            ProfileChanged?.Invoke(Profile);
            friends.RequestFriendInfo(logon.ClientSteamID);
            ConnectionChanged?.Invoke("Connected to Steam");
        }
        catch
        {
            client.Disconnect();
            lock (sync) { online = false; accessToken = null; refreshToken = null; }
            throw;
        }
        finally { connected = null; loggedOn = null; disconnected = null; connectionGate.Release(); }
    }

    public async Task<string> GetWebCookieAsync(CancellationToken ct)
    {
        if (!IsLoggedOn || Profile is null || string.IsNullOrEmpty(refreshToken)) throw new InvalidOperationException("Sign in to Steam first.");
        if (DateTimeOffset.UtcNow - tokenRefreshed > TimeSpan.FromHours(8))
        {
            var generated = await client.Authentication.GenerateAccessTokenForAppAsync(new SteamID(Profile.SteamId), refreshToken)
                .WaitAsync(TimeSpan.FromSeconds(30), ct);
            ct.ThrowIfCancellationRequested();
            if (!IsLoggedOn) throw new InvalidOperationException("Steam disconnected while renewing the session.");
            accessToken = generated.AccessToken; tokenRefreshed = DateTimeOffset.UtcNow;
        }
        return Uri.EscapeDataString($"{Profile.SteamId}||{accessToken}");
    }

    public async Task<IReadOnlyDictionary<uint, (string Name, double Hours)>> GetOwnedGamesAsync(CancellationToken ct)
    {
        if (!IsLoggedOn || Profile is null) throw new InvalidOperationException("Sign in to Steam first.");
        var service = client.GetHandler<SteamUnifiedMessages>()!.CreateService<Player>();
        SteamUnifiedMessages.ServiceMethodResponse<CPlayer_GetOwnedGames_Response> result;
        try
        {
            result = await service.GetOwnedGames(new CPlayer_GetOwnedGames_Request
            {
                steamid = Profile.SteamId,
                include_appinfo = true,
                include_played_free_games = true,
                skip_unvetted_apps = false
            }).ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (AsyncJobFailedException ex) when (!IsLoggedOn) { throw new IOException("Steam disconnected while reading the library.", ex); }
        if (IsTransportResult(result.Result)) throw new IOException("Steam is temporarily unavailable while reading the library.");
        if (result.Result != EResult.OK) throw new InvalidOperationException($"Steam could not verify the owned library ({result.Result}).");
        return result.Body.games.GroupBy(g => checked((uint)g.appid)).ToDictionary(g => g.Key, g => (g.First().name ?? $"App {g.Key}", g.First().playtime_forever / 60d));
    }

    public void SetPlayedGames(IReadOnlyCollection<uint> appIds)
    {
        lock (sync)
        {
            if (appIds.Count > 0 && (!online || blocked)) throw new InvalidOperationException("Steam is disconnected or another client is playing. Farming cannot start.");
            // Sending even an empty GamesPlayed message while blocked can trigger LoggedInElsewhere.
            if (!online || blocked || !client.IsConnected) return;
            var message = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
            foreach (var appId in appIds) message.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed { game_id = new GameID(appId).ToUInt64() });
            client.Send(message);
        }
    }

    private static bool IsTransportResult(EResult result) => result is EResult.NoConnection or EResult.Timeout or EResult.ConnectFailed or EResult.ServiceUnavailable or EResult.TryAnotherCM;

    private void LostConnection(string reason, SteamInterruption? interruption)
    {
        bool wasOnline;
        lock (sync) { wasOnline = online; online = false; accessToken = null; blocked = false; }
        connected?.TrySetException(new IOException("Steam transport disconnected."));
        loggedOn?.TrySetException(new IOException("Steam transport disconnected."));
        // Failed connection attempts must not recursively launch another recovery loop.
        if (wasOnline && interruption is { } kind) Interrupted?.Invoke(kind);
        ConnectionChanged?.Invoke(reason);
    }

    public async Task DisconnectAsync(bool forget, CancellationToken ct = default)
    {
        Interrupted?.Invoke(SteamInterruption.SessionEnded);
        await connectionGate.WaitAsync(ct);
        try
        {
            SetPlayedGames([]);
            user.LogOff();
            client.Disconnect();
            lock (sync) { online = false; profile = null; refreshToken = null; accessToken = null; blocked = false; }
            if (forget) storage.DeleteSession();
            ProfileChanged?.Invoke(null);
        }
        finally { connectionGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(false);
        lifetime.Cancel();
        await callbackLoop;
        lifetime.Dispose();
    }
}
