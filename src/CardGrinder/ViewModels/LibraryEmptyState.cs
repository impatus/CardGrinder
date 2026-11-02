namespace CardGrinder.ViewModels;

internal sealed record LibraryEmptyState(string Title, string Description)
{
    public static LibraryEmptyState Describe(bool connected, bool loading, bool failed, bool loaded, bool hasGames)
    {
        if (!connected && loading) return new("Connecting to Steam", "Waiting for a secure Steam connection…");
        if (!connected) return new("Your next card is waiting", "Connect Steam to find owned games with trading card drops.");
        if (loading) return new("Loading card status", "Steam is connected. Checking your owned games and Badges pages…");
        if (failed) return new("Card status unavailable", "Steam is connected, but card status could not be verified. Check the error above and refresh again.");
        if (!loaded) return new("Your library has not been loaded", "Steam is connected. Refresh the library to check your remaining card drops.");
        if (hasGames) return new("No matching games", "No games match your current filters. Try another search or show completed games.");
        return new("No remaining card drops", "Your library was checked successfully. Steam reported no remaining drops in your owned games.");
    }
}
