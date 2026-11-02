using CardGrinder.Core;
using Xunit;

namespace CardGrinder.Tests;

public sealed class ReconnectionTests
{
    private sealed class Session : ISteamSession
    {
        public bool IsLoggedOn { get; set; } = true;
        public bool PlayingBlocked { get; set; }
        public event Action<SteamInterruption>? Interrupted;
        public uint[] Playing { get; private set; } = [];
        public int ReconnectCalls { get; private set; }
        public List<uint[]> Reports { get; } = [];
        public Func<int, CancellationToken, Task>? ReconnectHandler { get; set; }
        public async Task ReconnectAsync(CancellationToken ct)
        {
            ReconnectCalls++;
            if (ReconnectHandler is not null) await ReconnectHandler(ReconnectCalls, ct);
            ct.ThrowIfCancellationRequested(); IsLoggedOn = true;
        }
        public void SetPlayedGames(IReadOnlyCollection<uint> ids)
        {
            if (ids.Count > 0 && (!IsLoggedOn || PlayingBlocked)) throw new InvalidOperationException("Unsafe game report.");
            Playing = ids.ToArray(); Reports.Add(Playing);
        }
        public void Interrupt(SteamInterruption reason)
        {
            if (reason == SteamInterruption.PlayingConflict) PlayingBlocked = true; else IsLoggedOn = false;
            Playing = []; Interrupted?.Invoke(reason);
        }
    }
    private sealed class Library : ICardLibrary
    {
        public IReadOnlyList<CardGame> Games { get; set; } = [new(10, "First", 2, 5), new(20, "Second", 3, 10)];
        public Exception? Error { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<CardGame>>>? Handler { get; set; }
        public void ResetAfterSignIn() { }
        public Task<IReadOnlyList<CardGame>> GetGamesAsync(CancellationToken ct) => Handler?.Invoke(ct) ?? (Error is null ? Task.FromResult(Games) : Task.FromException<IReadOnlyList<CardGame>>(Error));
    }
    private static readonly TimeSpan[] ImmediateRetries = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
    private static Task<FarmSnapshot> WaitFor(FarmingEngine engine, Func<FarmSnapshot, bool> predicate)
    {
        var result = new TaskCompletionSource<FarmSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(FarmSnapshot snapshot)
        {
            if (predicate(snapshot)) { engine.Changed -= Changed; result.TrySetResult(snapshot); }
        }
        engine.Changed += Changed;
        return result.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task NetworkBreakRestoresTheCurrentQueueAndPreservesSessionProgress()
    {
        var session = new Session(); var library = new Library();
        await using var engine = new FarmingEngine(session, library, ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10, 20], false, true, true);
        library.Games = [new(10, "First", 1, 5), new(20, "Second", 3, 10)];
        await engine.RefreshAsync();
        var restored = WaitFor(engine, snapshot => snapshot.State == FarmState.Farming && session.ReconnectCalls > 0);
        session.Interrupt(SteamInterruption.Network);
        var snapshot = await restored;
        Assert.Equal(1, session.ReconnectCalls); Assert.Equal(new uint[] { 10 }, session.Playing);
        Assert.Equal(1, snapshot.Received); Assert.Equal(2, snapshot.Games.Single(game => game.Game.AppId == 10).Baseline);
    }

    [Fact]
    public async Task ConfirmedFinalDropDuringBreakAdvancesTheOriginalQueue()
    {
        var session = new Session(); var library = new Library();
        await using var engine = new FarmingEngine(session, library, ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10, 20], false, true, true);
        library.Games = [new(10, "First", 0, 5), new(20, "Second", 3, 10)];
        var restored = WaitFor(engine, snapshot => snapshot.State == FarmState.Farming && session.ReconnectCalls > 0);
        session.Interrupt(SteamInterruption.Network);
        var snapshot = await restored;
        Assert.Equal(new uint[] { 20 }, session.Playing); Assert.Equal(2, snapshot.Received);
    }

    [Fact]
    public async Task DisabledAutomaticQueueDoesNotStartAnotherGameAfterRecovery()
    {
        var session = new Session(); var library = new Library();
        await using var engine = new FarmingEngine(session, library, ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10, 20], false, false, true);
        library.Games = [new(10, "First", 0, 5), new(20, "Second", 3, 10)];
        var restored = WaitFor(engine, snapshot => snapshot.State == FarmState.Paused && session.ReconnectCalls > 0);
        session.Interrupt(SteamInterruption.Network);
        await restored; Assert.Empty(session.Playing);
    }

    [Fact]
    public async Task SimultaneousModeRestoresTheSameSelectedBatch()
    {
        var session = new Session(); var library = new Library();
        await using var engine = new FarmingEngine(session, library, ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10, 20], true, true, true);
        var restored = WaitFor(engine, snapshot => snapshot.State == FarmState.Farming && session.ReconnectCalls > 0);
        session.Interrupt(SteamInterruption.Network);
        await restored; Assert.Equal(new uint[] { 10, 20 }, session.Playing);
    }

    [Fact]
    public async Task AManuallyPausedQueueReconnectsWithoutStartingFarming()
    {
        var session = new Session(); var library = new Library();
        await using var engine = new FarmingEngine(session, library, ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10, 20], false, true, true); await engine.PauseAsync();
        var restored = WaitFor(engine, snapshot => snapshot.State == FarmState.Paused && session.ReconnectCalls > 0);
        session.Interrupt(SteamInterruption.Network);
        await restored; Assert.True(session.IsLoggedOn); Assert.Empty(session.Playing);
        Assert.Single(session.Reports, ids => ids.Length > 0);
    }

    [Fact]
    public async Task AnIdleConnectionIsRestoredWithoutReportingAnyGame()
    {
        var session = new Session(); var library = new Library();
        await using var engine = new FarmingEngine(session, library, ImmediateRetries);
        await engine.RefreshAsync();
        var restored = WaitFor(engine, snapshot => snapshot.State == FarmState.Idle && session.ReconnectCalls > 0);
        session.Interrupt(SteamInterruption.Network);
        await restored; Assert.True(session.IsLoggedOn); Assert.DoesNotContain(session.Reports, ids => ids.Length > 0);
    }

    [Fact]
    public async Task StopDuringBackoffCancelsAllPendingAttempts()
    {
        var session = new Session(); var library = new Library();
        var engine = new FarmingEngine(session, library, [TimeSpan.FromHours(1)]);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        var reconnecting = WaitFor(engine, snapshot => snapshot.State == FarmState.Reconnecting);
        session.Interrupt(SteamInterruption.Network); await reconnecting;
        await engine.StopAsync(); await engine.DisposeAsync();
        Assert.Equal(0, session.ReconnectCalls); Assert.Empty(session.Playing);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopOrPauseDuringAConnectionAttemptCannotBeOverriddenByLateSuccess(bool stop)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session { ReconnectHandler = async (_, _) => { started.TrySetResult(); await release.Task; } };
        var engine = new FarmingEngine(session, new Library(), ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        session.Interrupt(SteamInterruption.Network); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (stop) await engine.StopAsync(); else await engine.PauseAsync();
        release.SetResult(); await engine.DisposeAsync();
        Assert.Empty(session.Playing); Assert.Single(session.Reports, ids => ids.Length > 0);
    }

    [Fact]
    public async Task ExitCancelsAnActiveReconnect()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session { ReconnectHandler = async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var engine = new FarmingEngine(session, new Library(), ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        session.Interrupt(SteamInterruption.Network); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); Assert.Empty(session.Playing);
    }

    [Theory]
    [InlineData(SteamInterruption.PlayingConflict)]
    [InlineData(SteamInterruption.SessionEnded)]
    public async Task PlayingConflictAndSignOutCancelRecoveryAndNeverResumeAutomatically(SteamInterruption reason)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session { ReconnectHandler = async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        await using var engine = new FarmingEngine(session, new Library(), ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        session.Interrupt(SteamInterruption.Network); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var paused = WaitFor(engine, snapshot => snapshot.State == FarmState.Paused);
        session.Interrupt(reason); await paused;
        Assert.Empty(session.Playing); Assert.Single(session.Reports, ids => ids.Length > 0);
    }

    [Fact]
    public async Task DuplicateDisconnectCallbacksUseOneRecoveryWorker()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Session { ReconnectHandler = async (_, ct) => { started.TrySetResult(); await release.Task.WaitAsync(ct); } };
        await using var engine = new FarmingEngine(session, new Library(), ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        var restored = WaitFor(engine, snapshot => snapshot.State == FarmState.Farming && session.ReconnectCalls > 0);
        session.Interrupt(SteamInterruption.Network); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        session.Interrupt(SteamInterruption.Network); session.Interrupt(SteamInterruption.Network);
        release.SetResult(); await restored; Assert.Equal(1, session.ReconnectCalls);
    }

    [Fact]
    public async Task RepeatedNetworkFailuresStopAtTheConfiguredAttemptLimit()
    {
        var session = new Session { ReconnectHandler = (_, _) => Task.FromException(new IOException("Network unavailable")) };
        await using var engine = new FarmingEngine(session, new Library(), ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        var failed = WaitFor(engine, snapshot => snapshot.State == FarmState.Faulted);
        session.Interrupt(SteamInterruption.Network); await failed;
        Assert.Equal(3, session.ReconnectCalls); Assert.Empty(session.Playing);
    }

    [Fact]
    public async Task AuthenticationRejectionStopsAfterOneAttempt()
    {
        var session = new Session { ReconnectHandler = (_, _) => Task.FromException(new InvalidOperationException("Saved session expired")) };
        await using var engine = new FarmingEngine(session, new Library(), ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        var failed = WaitFor(engine, snapshot => snapshot.State == FarmState.Faulted);
        session.Interrupt(SteamInterruption.Network); await failed;
        Assert.Equal(1, session.ReconnectCalls); Assert.Empty(session.Playing);
    }

    [Fact]
    public async Task UnknownCardStatusAfterReconnectionDoesNotRestartOrRetryFarming()
    {
        var session = new Session(); var library = new Library();
        await using var engine = new FarmingEngine(session, library, ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        library.Error = new InvalidDataException("Steam changed Badges markup.");
        var failed = WaitFor(engine, snapshot => snapshot.State == FarmState.Faulted);
        session.Interrupt(SteamInterruption.Network); await failed;
        Assert.Equal(1, session.ReconnectCalls); Assert.Empty(session.Playing);
    }

    [Fact]
    public async Task HttpNetworkFailureRecoversEvenWhenTheCmConnectionHasNotDropped()
    {
        var library = new Library();
        var session = new Session { ReconnectHandler = (_, _) => { library.Error = null; return Task.CompletedTask; } };
        await using var engine = new FarmingEngine(session, library, ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        library.Error = new HttpRequestException("Network unreachable");
        var restored = WaitFor(engine, snapshot => snapshot.State == FarmState.Farming && session.ReconnectCalls > 0);
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.RefreshAsync());
        await restored; Assert.Equal(new uint[] { 10 }, session.Playing);
    }

    [Fact]
    public async Task ASecondNetworkBreakDuringVerificationRetriesWithinTheSameBudget()
    {
        var session = new Session(); var library = new Library();
        await using var engine = new FarmingEngine(session, library, ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        int reads = 0;
        library.Handler = ct =>
        {
            if (++reads == 1) { session.Interrupt(SteamInterruption.Network); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(library.Games);
        };
        var restored = WaitFor(engine, snapshot => snapshot.State == FarmState.Farming && session.ReconnectCalls > 0);
        session.Interrupt(SteamInterruption.Network); await restored;
        Assert.Equal(2, session.ReconnectCalls); Assert.Equal(new uint[] { 10 }, session.Playing);
    }

    [Fact]
    public async Task StopCancelsThePostReconnectLibraryVerification()
    {
        var session = new Session(); var library = new Library();
        var engine = new FarmingEngine(session, library, ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Handler = async ct => { reading.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return []; };
        session.Interrupt(SteamInterruption.Network); await reading.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await engine.DisposeAsync(); Assert.Empty(session.Playing);
        Assert.Single(session.Reports, ids => ids.Length > 0);
    }

    [Fact]
    public async Task MissingQueuedGameAfterRecoveryNeverMeansCompleted()
    {
        var session = new Session(); var library = new Library();
        await using var engine = new FarmingEngine(session, library, ImmediateRetries);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        library.Games = [];
        var failed = WaitFor(engine, snapshot => snapshot.State == FarmState.Faulted);
        session.Interrupt(SteamInterruption.Network);
        var snapshot = await failed;
        Assert.Equal(0, snapshot.Received); Assert.Empty(session.Playing); Assert.Equal(1, session.ReconnectCalls);
    }
}
