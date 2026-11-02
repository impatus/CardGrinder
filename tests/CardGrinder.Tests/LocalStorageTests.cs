using System.Text;
using CardGrinder.Core;
using Xunit;

namespace CardGrinder.Tests;

public sealed class LocalStorageTests
{
    [Fact]
    public void SavedRefreshTokenIsEncryptedAndLogoutRemovesIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "CardGrinderTests-" + Guid.NewGuid());
        try
        {
            var store = new LocalStorage(path);
            Assert.False(store.HasSavedSession);
            var session = new SavedSession("test-account", "sensitive-refresh-token");
            store.SaveSession(session);
            byte[] encrypted = File.ReadAllBytes(Path.Combine(path, "session.bin"));
            Assert.DoesNotContain(session.RefreshToken, Encoding.UTF8.GetString(encrypted));
            Assert.DoesNotContain(session.AccountName, Encoding.UTF8.GetString(encrypted));
            var restartedStore = new LocalStorage(path);
            Assert.True(restartedStore.HasSavedSession);
            Assert.Equal(session, restartedStore.LoadSession());
            restartedStore.DeleteSession(); Assert.Null(store.LoadSession()); Assert.False(store.HasSavedSession);
        }
        finally { if (Directory.Exists(path)) { foreach (string file in Directory.GetFiles(path)) File.Delete(file); Directory.Delete(path); } }
    }
    [Fact]
    public void UntrustedExceptionsDoNotExposeTokensInUi()
    {
        Assert.DoesNotContain("secret", SafeErrors.Describe(new HttpRequestException("https://steamcommunity.com/?access_token=secret")));
        Assert.DoesNotContain("secret", SafeErrors.Describe(new Exception("secret")));
    }
}
