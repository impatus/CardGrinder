namespace CardGrinder.Core;

public sealed record CardGame(uint AppId, string Name, int Remaining, double HoursPlayed)
{
    public string CoverUrl => $"https://cdn.akamai.steamstatic.com/steam/apps/{AppId}/header.jpg";
}

public enum FarmState { Idle, Farming, Paused, Completed, Faulted, Reconnecting }
public enum SteamInterruption { Network, PlayingConflict, SessionEnded }
public sealed record GameProgress(CardGame Game, int Baseline, int Received, bool Active)
{
    public double Percent => Baseline == 0 ? 0 : Math.Clamp(Received * 100d / Baseline, 0, 100);
    public bool Completed => Game.Remaining == 0;
}
public sealed record FarmSnapshot(FarmState State, string Message, IReadOnlyList<GameProgress> Games,
    int Received, TimeSpan Duration, DateTimeOffset? UpdatedAt);
public sealed record SteamProfile(ulong SteamId, string Name, string? AvatarUrl);

public interface ISteamSession
{
    bool IsLoggedOn { get; }
    bool PlayingBlocked { get; }
    event Action<SteamInterruption>? Interrupted;
    Task ReconnectAsync(CancellationToken cancellationToken);
    void SetPlayedGames(IReadOnlyCollection<uint> appIds);
}

public interface ICardLibrary
{
    Task<IReadOnlyList<CardGame>> GetGamesAsync(CancellationToken cancellationToken);
    void ResetAfterSignIn();
}

public sealed record AppSettings
{
    public bool StartMinimized { get; init; }
    public bool MinimizeToTray { get; init; } = true;
    public bool AutomaticQueue { get; init; } = true;
    public bool WaitForEligiblePlaytime { get; init; } = true;
}
