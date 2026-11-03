using System.Text.Json;

namespace CardGrinder.Core;

public sealed record UpdateChannel(bool Enabled, string ManifestUrl, string PublicKeyPem)
{
    public static UpdateChannel LoadBundled()
    {
        using var stream = typeof(UpdateChannel).Assembly.GetManifestResourceStream("CardGrinder.Core.UpdateChannel.json");
        return stream is null ? new(false, "", "")
            : JsonSerializer.Deserialize<UpdateChannel>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
              ?? new(false, "", "");
    }
}
