namespace CardGrinder.Core;

public sealed class SteamCardLibrary : ICardLibrary, IDisposable
{
    private readonly SteamSession session;
    private readonly SteamCommunityClient community;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset lastRequest;
    private int failures;
    public SteamCardLibrary(SteamSession session)
    {
        this.session = session;
        community = new(session.GetWebCookieAsync, WaitForRequestAsync, time => lastRequest = time);
    }
    public void ResetAfterSignIn() => failures = 0;

    public async Task<IReadOnlyList<CardGame>> GetGamesAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (failures >= 3) throw new InvalidOperationException("Automatic requests stopped after three Steam failures. Reconnect before refreshing again.");
            await WaitForRequestAsync(cancellationToken);
            var owned = await session.GetOwnedGamesAsync(cancellationToken);
            ulong steamId = session.Profile?.SteamId ?? throw new InvalidOperationException("Steam disconnected.");
            var counts = new Dictionary<uint, int>();
            bool hasGameBadges = false;
            int pages = 1;
            for (int page = 1; page <= pages; page++)
            {
                var parsed = BadgeParser.Parse(await community.GetBadgesAsync(steamId, page, cancellationToken), allowMissingDropData: true);
                hasGameBadges |= parsed.HasGameBadges;
                if (page == 1) pages = parsed.Pages;
                else if (parsed.Pages != pages) throw new InvalidDataException($"Steam changed Badges pagination during refresh (expected {pages} pages, received {parsed.Pages} on page {page}). Refresh again later.");
                foreach (var pair in parsed.Drops)
                {
                    if (!counts.TryAdd(pair.Key, pair.Value)) throw new InvalidDataException("Steam returned duplicate games across Badges pages. Refresh again later.");
                }
            }
            // Individual pages can contain only crafted badges. Validate private-data visibility across the full library.
            BadgeParser.ValidateDropVisibility(hasGameBadges, counts.Count);
            if (!session.IsLoggedOn || session.Profile?.SteamId != steamId) throw new InvalidOperationException("Steam disconnected during library refresh.");
            failures = 0;
            return counts.Where(p => owned.ContainsKey(p.Key))
                .Select(p => new CardGame(p.Key, owned[p.Key].Name, p.Value, owned[p.Key].Hours)).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { failures++; throw; }
        finally { gate.Release(); }
    }

    private async Task WaitForRequestAsync(CancellationToken ct)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(5, failures == 0 ? 5 : Math.Pow(2, failures) * 10));
        var wait = lastRequest + interval - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        lastRequest = DateTimeOffset.UtcNow;
    }
    public void Dispose() { community.Dispose(); gate.Dispose(); }
}
