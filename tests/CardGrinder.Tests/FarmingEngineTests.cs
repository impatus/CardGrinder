using CardGrinder.Core;
using Xunit;

namespace CardGrinder.Tests;

public sealed class FarmingEngineTests
{
    private sealed class Session : ISteamSession
    {
        public bool IsLoggedOn { get; set; } = true;
        public bool PlayingBlocked { get; set; }
        public event Action<SteamInterruption>? Interrupted;
        public Task ReconnectAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); IsLoggedOn = true; return Task.CompletedTask; }
        public uint[] Playing { get; private set; } = [];
        public void SetPlayedGames(IReadOnlyCollection<uint> ids)
        {
            if (ids.Count > 0 && (!IsLoggedOn || PlayingBlocked)) throw new InvalidOperationException("Blocked");
            Playing = ids.ToArray();
        }
        public void Interrupt(bool disconnect)
        {
            if (disconnect) IsLoggedOn = false; else PlayingBlocked = true;
            Playing = []; Interrupted?.Invoke(disconnect ? SteamInterruption.SessionEnded : SteamInterruption.PlayingConflict);
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
    [Fact]
    public async Task DefaultQueueReportsOneGameAndAdvancesOnConfirmedCompletion()
    {
        var session = new Session(); var library = new Library();
        await using var engine = new FarmingEngine(session, library);
        FarmSnapshot? snapshot = null; engine.Changed += s => snapshot = s;
        await engine.RefreshAsync(); await engine.StartAsync([10, 20], false, true, true);
        Assert.Equal(new uint[] { 10 }, session.Playing);
        library.Games = [new(10, "First", 0, 5), new(20, "Second", 3, 10)];
        await engine.RefreshAsync();
        Assert.Equal(new uint[] { 20 }, session.Playing); Assert.Equal(2, snapshot!.Received); Assert.True(snapshot.Games.Single(g => g.Game.AppId == 10).Completed);
        library.Games = [new(10, "First", 0, 5), new(20, "Second", 0, 10)];
        await engine.RefreshAsync(); Assert.Empty(session.Playing); Assert.Equal(FarmState.Completed, snapshot.State); Assert.Equal(5, snapshot.Received);
    }
    [Fact]
    public async Task MissingGameDoesNotCountAsADropAndPausesFarming()
    {
        var session = new Session(); var library = new Library(); await using var engine = new FarmingEngine(session, library);
        FarmSnapshot? snapshot = null; engine.Changed += s => snapshot = s;
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        library.Games = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.RefreshAsync());
        Assert.Empty(session.Playing); Assert.Equal(0, snapshot!.Received); Assert.Equal(FarmState.Paused, snapshot.State);
    }
    [Fact]
    public async Task IncreasedDropsNeverCreateFalseProgress()
    {
        var session = new Session(); var library = new Library(); await using var engine = new FarmingEngine(session, library);
        FarmSnapshot? snapshot = null; engine.Changed += s => snapshot = s;
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        library.Games = [new(10, "First", 5, 5), new(20, "Second", 3, 10)];
        await engine.RefreshAsync(); Assert.Equal(0, snapshot!.Received);
        await engine.RefreshAsync(); Assert.Equal(0, snapshot.Received);
    }
    [Fact]
    public async Task SimultaneousModeOnlyReportsSelectedEligibleGames()
    {
        var session = new Session(); var library = new Library(); await using var engine = new FarmingEngine(session, library);
        await engine.RefreshAsync(); await engine.StartAsync([10, 20], true, true, true);
        Assert.Equal(new uint[] { 10, 20 }, session.Playing);
        await engine.StopAsync(); Assert.Empty(session.Playing);
    }
    [Fact]
    public async Task DisabledQueuePausesInsteadOfStartingNextGame()
    {
        var session = new Session(); var library = new Library(); await using var engine = new FarmingEngine(session, library);
        FarmSnapshot? snapshot = null; engine.Changed += s => snapshot = s;
        await engine.RefreshAsync(); await engine.StartAsync([10, 20], false, false, true);
        library.Games = [new(10, "First", 0, 5), new(20, "Second", 3, 10)];
        await engine.RefreshAsync(); Assert.Empty(session.Playing); Assert.Equal(FarmState.Paused, snapshot!.State);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForcedSignOutAndPlayConflictsPauseWithoutAutomaticResume(bool disconnect)
    {
        var session = new Session(); var library = new Library(); await using var engine = new FarmingEngine(session, library);
        var paused = new TaskCompletionSource<FarmSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Changed += s => { if (s.State == FarmState.Paused) paused.TrySetResult(s); };
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        session.Interrupt(disconnect);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Empty(session.Playing);
        session.PlayingBlocked = false; session.IsLoggedOn = true;
        await engine.RefreshAsync(); Assert.Empty(session.Playing);
        await engine.ResumeAsync(); Assert.Equal(new uint[] { 10 }, session.Playing);
    }
    [Fact]
    public async Task RequestFailureStopsPresenceAndDoesNotAdvanceQueue()
    {
        var session = new Session(); var library = new Library(); await using var engine = new FarmingEngine(session, library);
        await engine.RefreshAsync(); await engine.StartAsync([10, 20], false, true, true);
        library.Error = new HttpRequestException("Unavailable");
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.RefreshAsync()); Assert.Empty(session.Playing);
    }
    [Fact]
    public async Task StopCancelsAnInFlightRequestAndStopsPresenceImmediately()
    {
        var session = new Session(); var library = new Library(); await using var engine = new FarmingEngine(session, library);
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        library.Handler = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return []; };
        var refresh = engine.RefreshAsync(); await started.Task;
        var stop = engine.StopAsync(); Assert.Empty(session.Playing);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await stop.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Empty(session.Playing);
    }
    [Fact]
    public async Task UnknownAppAndCompletedGamesCannotBeStarted()
    {
        var session = new Session(); var library = new Library { Games = [new(10, "Complete", 0, 5)] }; await using var engine = new FarmingEngine(session, library);
        await engine.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync([10], false, true, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync([999], false, true, true)); Assert.Empty(session.Playing);
    }
    [Fact]
    public async Task PlaytimePreferenceRejectsUnderTwoHoursWithoutStarting()
    {
        var session = new Session(); var library = new Library { Games = [new(10, "New game", 2, 0.5)] }; await using var engine = new FarmingEngine(session, library);
        await engine.RefreshAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync([10], false, true, false)); Assert.Empty(session.Playing);
    }
    [Fact]
    public async Task DisposalStopsAllReportedGames()
    {
        var session = new Session(); var engine = new FarmingEngine(session, new Library());
        await engine.RefreshAsync(); await engine.StartAsync([10], false, true, true);
        await engine.DisposeAsync(); Assert.Empty(session.Playing);
    }
}
