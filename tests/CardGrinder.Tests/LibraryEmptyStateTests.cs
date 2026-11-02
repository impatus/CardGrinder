using CardGrinder.ViewModels;
using Xunit;

namespace CardGrinder.Tests;

public sealed class LibraryEmptyStateTests
{
    [Fact]
    public void FailedInitialRefreshNeverClaimsTheLibraryIsEmpty()
    {
        var state = LibraryEmptyState.Describe(true, false, true, false, false);
        Assert.Equal("Card status unavailable", state.Title);
        Assert.Contains("could not be verified", state.Description);
    }
    [Fact] public void ConnectedButUnloadedLibraryRequestsARefresh() => Assert.Equal("Your library has not been loaded", LibraryEmptyState.Describe(true, false, false, false, false).Title);
    [Fact] public void ConfirmedEmptyLibraryCanReportNoDrops() => Assert.Equal("No remaining card drops", LibraryEmptyState.Describe(true, false, false, true, false).Title);
    [Fact] public void LoadingDoesNotReportAnEmptyLibrary() => Assert.Equal("Loading card status", LibraryEmptyState.Describe(true, true, false, false, false).Title);
    [Fact] public void FiltersDoNotClaimNoDropsForTheEntireLibrary() => Assert.Equal("No matching games", LibraryEmptyState.Describe(true, false, false, true, true).Title);
    [Fact] public void SavedSessionReconnectShowsConnectingState() => Assert.Equal("Connecting to Steam", LibraryEmptyState.Describe(false, true, false, false, false).Title);
}
