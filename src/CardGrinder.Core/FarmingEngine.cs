using System.Diagnostics;

namespace CardGrinder.Core;

/// <summary>Owns the queue and bounded recovery of an interrupted Steam connection.</summary>
public sealed class FarmingEngine : IAsyncDisposable
{
    private readonly ISteamSession session;
    private readonly ICardLibrary library;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly List<uint> queue = [];
    private readonly Dictionary<uint, GameProgress> games = [];
    private readonly HashSet<uint> interruptedActive = [];
    private readonly Stopwatch elapsed = new();
    private readonly Task monitor;
    private readonly TimeSpan[] reconnectDelays;
    private readonly object refreshSync = new(), recoverySync = new();
    private CancellationTokenSource? currentRefresh, recoveryCancellation;
    private Task recoveryTask = Task.CompletedTask;
    private FarmState state;
    private string message = "Connect your Steam account to get started.";
    private bool automaticQueue = true;
    private volatile bool farmingRequested;
    private int simultaneous = 1, intentVersion;
    private DateTimeOffset? updatedAt;
    public event Action<FarmSnapshot>? Changed;

    public FarmingEngine(ISteamSession session, ICardLibrary library, IReadOnlyList<TimeSpan>? reconnectDelays = null)
    {
        this.session = session; this.library = library; lifetimeToken = lifetime.Token;
        this.reconnectDelays = reconnectDelays?.ToArray() ?? [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)];
        if (this.reconnectDelays.Length == 0 || this.reconnectDelays.Any(delay => delay < TimeSpan.Zero))
            throw new ArgumentException("At least one nonnegative reconnect delay is required.", nameof(reconnectDelays));
        session.Interrupted += OnInterrupted;
        monitor = MonitorAsync();
    }

    public Task RefreshAsync(CancellationToken ct = default) => ExecuteAsync(() => RefreshCoreAsync(ct), ct);

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        RequireConnection();
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        lock (refreshSync) currentRefresh = refresh;
        IReadOnlyList<CardGame> incoming;
        try { incoming = await library.GetGamesAsync(refresh.Token); refresh.Token.ThrowIfCancellationRequested(); }
        finally { lock (refreshSync) currentRefresh = null; }
        foreach (var id in queue)
            if (!incoming.Any(game => game.AppId == id))
                throw new InvalidDataException("Steam did not return card status for a queued game. Farming has been paused.");
        foreach (var game in incoming)
        {
            if (games.TryGetValue(game.AppId, out var old))
            {
                int received = old.Received + (old.Active || interruptedActive.Contains(game.AppId) ? Math.Max(0, old.Game.Remaining - game.Remaining) : 0);
                games[game.AppId] = old with { Game = game, Received = received, Baseline = Math.Max(old.Baseline, received + game.Remaining) };
            }
            else games[game.AppId] = new(game, game.Remaining, 0, false);
        }
        var incomingIds = incoming.Select(game => game.AppId).ToHashSet();
        foreach (var id in games.Keys.Where(id => !incomingIds.Contains(id) && !queue.Contains(id)).ToArray())
            if (games[id].Received == 0) games.Remove(id);
        updatedAt = DateTimeOffset.Now;
        if (state == FarmState.Farming)
        {
            AdvanceQueue(games.Values.Where(game => game.Active).Select(game => game.Game.AppId).ToArray());
        }
        else if (state == FarmState.Faulted)
        {
            state = queue.Count > 0 ? FarmState.Paused : FarmState.Idle;
            message = "Card status verified. Resume the queue when you are ready.";
        }
        else if (state == FarmState.Idle)
            message = incoming.Any(game => game.Remaining > 0) ? "Library updated. Select games to start farming." : "Library updated. No remaining card drops were found.";
        if (state != FarmState.Reconnecting) interruptedActive.Clear();
    }

    public Task StartAsync(IEnumerable<uint> appIds, bool runTogether, bool autoQueue, bool allowPlaytime, CancellationToken ct = default)
    {
        CancelAutomaticRecovery();
        return ExecuteAsync(() =>
        {
            RequireConnection();
            if (state == FarmState.Farming) throw new InvalidOperationException("Stop the current session before starting a new selection.");
            if (updatedAt is null || DateTimeOffset.Now - updatedAt > TimeSpan.FromMinutes(10))
                throw new InvalidOperationException("Refresh the library before starting farming.");
            var chosen = appIds.Distinct().ToArray();
            if (chosen.Length == 0) throw new InvalidOperationException("Select at least one game with card drops remaining.");
            if (chosen.Any(id => !games.TryGetValue(id, out var game) || game.Game.Remaining <= 0))
                throw new InvalidOperationException("Only games with confirmed remaining card drops can be farmed.");
            if (!allowPlaytime && chosen.Any(id => games[id].Game.HoursPlayed < 2))
                throw new InvalidOperationException("A selected game has under 2 hours of playtime. Enable waiting for eligible playtime, or choose another game.");
            if (runTogether && chosen.Length > 4) throw new InvalidOperationException("Choose no more than four games for simultaneous farming.");
            queue.Clear(); queue.AddRange(chosen); interruptedActive.Clear();
            simultaneous = runTogether ? chosen.Length : 1; automaticQueue = autoQueue;
            foreach (var id in games.Keys.ToArray()) games[id] = games[id] with { Baseline = games[id].Game.Remaining, Received = 0, Active = false };
            elapsed.Restart(); state = FarmState.Farming; farmingRequested = true;
            ApplyQueue(); return Task.CompletedTask;
        }, ct);
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        CancelAutomaticRecovery(); CancelRefresh(); session.SetPlayedGames([]);
        return ExecuteAsync(() =>
        {
            interruptedActive.Clear();
            if (state is FarmState.Farming or FarmState.Reconnecting) PauseInternal("Paused. Resume when you are ready.");
            return Task.CompletedTask;
        }, ct);
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        CancelAutomaticRecovery();
        await RefreshAsync(ct);
        await ExecuteAsync(() =>
        {
            RequireConnection();
            if (state != FarmState.Paused) throw new InvalidOperationException("There is no paused queue to resume.");
            interruptedActive.Clear(); queue.RemoveAll(id => games[id].Game.Remaining == 0);
            state = FarmState.Farming; farmingRequested = true; elapsed.Start();
            ApplyQueue(); return Task.CompletedTask;
        }, ct);
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        CancelAutomaticRecovery(); CancelRefresh(); session.SetPlayedGames([]);
        return ExecuteAsync(() =>
        {
            session.SetPlayedGames([]); queue.Clear(); interruptedActive.Clear(); ClearActive(); elapsed.Stop();
            state = FarmState.Idle; message = "Stopped. Choose games to start a new session.";
            return Task.CompletedTask;
        }, ct);
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        CancelAutomaticRecovery(); CancelRefresh();
        return ExecuteAsync(() =>
        {
            session.SetPlayedGames([]); queue.Clear(); games.Clear(); interruptedActive.Clear(); elapsed.Reset(); updatedAt = null;
            state = FarmState.Idle; message = "Connect your Steam account to get started.";
            return Task.CompletedTask;
        }, ct);
    }

    private void RequireConnection()
    {
        if (!session.IsLoggedOn) throw new IOException("Steam is disconnected. Waiting for a connection.");
        if (session.PlayingBlocked)
            throw new InvalidOperationException("Steam reported an interrupted or conflicting session. Wait for recovery or resume manually.");
    }

    private void AdvanceQueue(IReadOnlyCollection<uint> previousActive)
    {
        RequireConnection();
        bool completedBatch = previousActive.Count > 0 && previousActive.All(id => games[id].Game.Remaining == 0);
        queue.RemoveAll(id => games[id].Game.Remaining == 0);
        if (!automaticQueue && completedBatch && queue.Count > 0)
        {
            farmingRequested = false;
            PauseInternal("Selected games completed. Start the next selection when ready.");
        }
        else ApplyQueue();
    }

    private void ApplyQueue()
    {
        RequireConnection();
        var active = queue.Where(id => games[id].Game.Remaining > 0).Take(simultaneous).ToArray();
        session.SetPlayedGames(active);
        foreach (var id in games.Keys.ToArray()) games[id] = games[id] with { Active = active.Contains(id) };
        if (active.Length == 0)
        {
            state = FarmState.Completed; farmingRequested = false; elapsed.Stop(); message = "All selected card drops are complete.";
        }
        else message = active.Length == 1 ? $"Farming {games[active[0]].Game.Name}" : $"Farming {active.Length} games simultaneously";
    }

    private void ClearActive()
    {
        foreach (var id in games.Keys.ToArray()) games[id] = games[id] with { Active = false };
    }
    private void RememberActive() => interruptedActive.UnionWith(games.Values.Where(game => game.Active).Select(game => game.Game.AppId));
    private void PauseInternal(string reason)
    {
        session.SetPlayedGames([]); ClearActive(); elapsed.Stop(); state = FarmState.Paused; message = reason;
    }
    private void CancelRefresh() { lock (refreshSync) currentRefresh?.Cancel(); }

    private void CancelAutomaticRecovery()
    {
        farmingRequested = false; Interlocked.Increment(ref intentVersion);
        lock (recoverySync) recoveryCancellation?.Cancel();
    }

    private void OnInterrupted(SteamInterruption reason)
    {
        if (lifetime.IsCancellationRequested) return;
        if (reason != SteamInterruption.Network) CancelAutomaticRecovery();
        int version = Volatile.Read(ref intentVersion);
        CancelRefresh();
        _ = HandleInterruptionAsync(reason, version);
    }

    private async Task HandleInterruptionAsync(SteamInterruption reason, int version)
    {
        try
        {
            await gate.WaitAsync(lifetimeToken).ConfigureAwait(false);
            try
            {
                if (version != Volatile.Read(ref intentVersion)) return;
                if (reason == SteamInterruption.Network)
                {
                    if (farmingRequested) RememberActive();
                    if (state == FarmState.Farming) PauseInternal("Network interrupted. Reconnecting to Steam…");
                    BeginRecovery();
                }
                else
                {
                    interruptedActive.Clear();
                    if (state is FarmState.Farming or FarmState.Reconnecting)
                        PauseInternal(reason == SteamInterruption.PlayingConflict
                            ? "Paused because another Steam client is playing. Resume manually when it has finished."
                            : "Steam ended the session. Reconnect and resume manually.");
                }
                Publish();
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    // Called under the state gate. A single worker handles both CM loss and HTTP/network timeouts.
    private void BeginRecovery()
    {
        if (lifetime.IsCancellationRequested) return;
        lock (recoverySync)
        {
            if (recoveryCancellation is { IsCancellationRequested: false }) return;
            var previous = recoveryTask;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            recoveryCancellation = cancellation;
            int version = Volatile.Read(ref intentVersion);
            state = FarmState.Reconnecting; elapsed.Stop(); ClearActive();
            message = "Network interrupted. Reconnecting to Steam…";
            recoveryTask = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                await RecoverAsync(cancellation, version).ConfigureAwait(false);
            });
        }
    }

    private async Task RecoverAsync(CancellationTokenSource cancellation, int version)
    {
        var ct = cancellation.Token;
        Exception? failure = null;
        try
        {
            for (int attempt = 0; attempt < reconnectDelays.Length; attempt++)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (version != Volatile.Read(ref intentVersion)) return;
                    state = FarmState.Reconnecting;
                    message = $"Reconnecting to Steam (attempt {attempt + 1}/{reconnectDelays.Length}) in {reconnectDelays[attempt].TotalSeconds:0} seconds…";
                    Publish();
                }
                finally { gate.Release(); }
                try
                {
                    await Task.Delay(reconnectDelays[attempt], ct).ConfigureAwait(false);
                    await session.ReconnectAsync(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        if (version != Volatile.Read(ref intentVersion)) return;
                        RequireConnection();
                        library.ResetAfterSignIn();
                        var previousActive = interruptedActive.ToArray();
                        await RefreshCoreAsync(ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested(); RequireConnection();
                        if (farmingRequested)
                        {
                            state = FarmState.Farming; elapsed.Start(); AdvanceQueue(previousActive);
                        }
                        else
                        {
                            state = queue.Count > 0 ? FarmState.Paused : FarmState.Idle;
                            message = queue.Count > 0 ? "Reconnected to Steam. Your queue remains paused." : "Reconnected to Steam. Select games to start farming.";
                        }
                        interruptedActive.Clear(); Publish(); return;
                    }
                    finally { gate.Release(); }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    failure = ex;
                    if (!NetworkFailures.IsTransient(ex) && !(ex is OperationCanceledException && !session.IsLoggedOn)) break;
                }
            }
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (version != Volatile.Read(ref intentVersion)) return;
                farmingRequested = false; interruptedActive.Clear(); session.SetPlayedGames([]); ClearActive(); elapsed.Stop();
                state = FarmState.Faulted;
                message = $"Automatic reconnection stopped. {SafeErrors.Describe(failure ?? new IOException())} Reconnect manually to continue.";
                Publish();
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            lock (recoverySync)
            {
                if (ReferenceEquals(recoveryCancellation, cancellation)) recoveryCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task ExecuteAsync(Func<Task> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await action(); }
        catch (Exception ex)
        {
            bool transient = NetworkFailures.IsTransient(ex) && !ct.IsCancellationRequested && !lifetime.IsCancellationRequested;
            if (farmingRequested && (transient || !session.IsLoggedOn)) RememberActive();
            if (state == FarmState.Farming) PauseInternal("Farming paused while the connection or card status is checked.");
            if (transient) BeginRecovery();
            else if (ex is not OperationCanceledException) farmingRequested = false;
            throw;
        }
        finally { try { Publish(); } finally { gate.Release(); } }
    }

    private void Publish() => Changed?.Invoke(new(state, message, games.Values.ToArray(), games.Values.Sum(game => game.Received), elapsed.Elapsed, updatedAt));

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(3));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                await gate.WaitAsync(lifetime.Token);
                bool active;
                int monitoredVersion;
                try { active = state == FarmState.Farming; monitoredVersion = Volatile.Read(ref intentVersion); } finally { gate.Release(); }
                if (!active) continue;
                try { await RefreshAsync(lifetime.Token); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (NetworkFailures.IsTransient(ex)) { /* ExecuteAsync already scheduled recovery. */ }
                catch (Exception ex)
                {
                    await gate.WaitAsync(lifetime.Token);
                    try
                    {
                        if (state != FarmState.Reconnecting && monitoredVersion == Volatile.Read(ref intentVersion))
                        {
                            state = FarmState.Faulted; message = $"Automatic checks stopped: {SafeErrors.Describe(ex)}"; Publish();
                        }
                    }
                    finally { gate.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        session.Interrupted -= OnInterrupted;
        CancelAutomaticRecovery(); CancelRefresh(); lifetime.Cancel();
        Task recovery;
        lock (recoverySync) recovery = recoveryTask;
        await monitor; await recovery;
        await StopAsync(); lifetime.Dispose();
    }
}

public static class SafeErrors
{
    public static string Describe(Exception ex) => ex switch
    {
        InvalidDataException => ex.Message,
        InvalidOperationException => ex.Message,
        OperationCanceledException => "The operation timed out or was cancelled. Try connecting again.",
        IOException or HttpRequestException or TimeoutException => "Steam could not be reached. Check your connection and try again.",
        System.Security.Cryptography.CryptographicException => "The saved session cannot be decrypted by this Windows account. Sign in again.",
        _ => "Steam could not complete the operation. Reconnect and try again."
    };
}
