using CardGrinder.Core;
using Xunit;

namespace CardGrinder.Tests;

public sealed class SavedSessionStartupTests
{
    [Fact]
    public async Task StartupWithoutSavedSessionDoesNotConnectOrCreateQrChallenge()
    {
        var store = new LocalStorage(Path.Combine(Path.GetTempPath(), "CardGrinder-Startup-" + Guid.NewGuid()));
        await using var session = new SteamSession(store);
        bool challenged = false; var states = new List<string>();
        session.QrChallengeChanged += _ => challenged = true;
        session.ConnectionChanged += states.Add;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(false, default, savedSessionOnly: true));
        Assert.False(session.IsLoggedOn); Assert.False(challenged);
        Assert.DoesNotContain("Connecting to Steam…", states);
        Assert.False(store.HasSavedSession);
    }

    [Fact]
    public async Task UnreadableSavedSessionDoesNotFallBackToQrInTheBackground()
    {
        string path = Path.Combine(Path.GetTempPath(), "CardGrinder-Startup-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        try
        {
            File.WriteAllBytes(Path.Combine(path, "session.bin"), [1, 2, 3, 4]);
            var store = new LocalStorage(path);
            await using var session = new SteamSession(store);
            bool challenged = false;
            session.QrChallengeChanged += _ => challenged = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(false, default, savedSessionOnly: true));
            Assert.False(session.IsLoggedOn); Assert.False(challenged); Assert.False(store.HasSavedSession);
        }
        finally { foreach (string file in Directory.GetFiles(path)) File.Delete(file); Directory.Delete(path); }
    }
}
