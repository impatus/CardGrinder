using System.Security.Cryptography;
using System.Text.Json;

namespace CardGrinder.Core;

public sealed record SavedSession(string AccountName, string RefreshToken);

public sealed class LocalStorage
{
    private readonly string directory;
    public LocalStorage(string? directory = null) => this.directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CardGrinder");
    private string SessionPath => Path.Combine(directory, "session.bin");
    public bool HasSavedSession => File.Exists(SessionPath);
    public void SaveSession(SavedSession session)
    {
        Directory.CreateDirectory(directory);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(session);
        try
        {
            var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(SessionPath + ".tmp", encrypted);
            File.Move(SessionPath + ".tmp", SessionPath, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public SavedSession? LoadSession()
    {
        if (!File.Exists(SessionPath)) return null;
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(SessionPath), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<SavedSession>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void DeleteSession()
    {
        File.Delete(SessionPath); File.Delete(SessionPath + ".tmp");
    }
    public AppSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(directory, "settings.json"))) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }
    public void SaveSettings(AppSettings settings)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
