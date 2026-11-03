using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace CardGrinder.Core;

public sealed record UpdateArtifact(string Url, string Sha256, long Size);
public sealed record UpdateRelease(int SchemaVersion, string AppId, string Version, DateTimeOffset PublishedAt,
    string Notes, UpdateArtifact Installer, UpdateArtifact Portable);
public sealed record SignedUpdateManifest(string Payload, string Signature);
public sealed record UpdateDownload(string Path, UpdateArtifact Artifact);

public sealed class AppUpdateService : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int ManifestLimit = 128 * 1024;
    private const long PackageLimit = 1024L * 1024 * 1024;
    private readonly UpdateChannel _channel;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private UpdateRelease? _verifiedRelease;
    public Version CurrentVersion { get; }
    public bool IsEnabled => _channel.Enabled && !string.IsNullOrWhiteSpace(_channel.ManifestUrl)
        && !string.IsNullOrWhiteSpace(_channel.PublicKeyPem);

    public AppUpdateService(UpdateChannel channel, Version currentVersion, HttpClient? client = null)
    {
        _channel = channel;
        CurrentVersion = new(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
        _ownsClient = client is null;
        _http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        // A dormant channel must not make even a speculative network request.
        if (!IsEnabled) return null;
        var uri = RequireHttps(_channel.ManifestUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new() { NoCache = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > ManifestLimit) throw new InvalidDataException("Update information is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + read > ManifestLimit) throw new InvalidDataException("Update information is too large.");
            output.Write(buffer, 0, read);
        }
        var release = VerifyManifest(output.ToArray());
        return ParseVersion(release.Version) > CurrentVersion ? release : null;
    }

    public UpdateRelease VerifyManifest(byte[] json)
    {
        if (!IsEnabled) throw new InvalidOperationException("Updates are not published yet.");
        if (json.Length > ManifestLimit) throw new InvalidDataException("Update information is too large.");
        var envelope = JsonSerializer.Deserialize<SignedUpdateManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Missing update information.");
        var payload = Convert.FromBase64String(envelope.Payload);
        using var key = RSA.Create();
        key.ImportFromPem(_channel.PublicKeyPem);
        if (key.KeySize < 3072 || !key.VerifyData(payload, Convert.FromBase64String(envelope.Signature),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("The update signature could not be verified.");
        var release = JsonSerializer.Deserialize<UpdateRelease>(payload, JsonOptions)
            ?? throw new InvalidDataException("Missing release information.");
        if (release.SchemaVersion != 1 || release.AppId != "CardGrinder" || release.PublishedAt == default
            || release.Notes is null || release.Notes.Length > 16000)
            throw new InvalidDataException("Unsupported update information.");
        _ = ParseVersion(release.Version);
        ValidateArtifact(release.Installer, ".exe");
        ValidateArtifact(release.Portable, ".zip");
        _verifiedRelease = release;
        return release;
    }

    private void ValidateArtifact(UpdateArtifact? artifact, string extension)
    {
        if (artifact is null || artifact.Size <= 0 || artifact.Size > PackageLimit || artifact.Sha256 is null
            || artifact.Sha256.Length != 64 || !artifact.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid update package information.");
        var uri = RequireHttps(artifact.Url);
        var channel = RequireHttps(_channel.ManifestUrl);
        if (uri.Authority != channel.Authority || !uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update package is outside the trusted channel.");
    }

    public async Task<UpdateDownload> DownloadAsync(UpdateRelease release, bool installed, string downloadRoot,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!IsEnabled) throw new InvalidOperationException("Updates are not published yet.");
        if (!ReferenceEquals(release, _verifiedRelease)) throw new InvalidOperationException("Check and verify this update before downloading it.");
        if (ParseVersion(release.Version) <= CurrentVersion) throw new InvalidOperationException("This update is not newer than CardGrinder.");
        var artifact = installed ? release.Installer : release.Portable;
        ValidateArtifact(artifact, installed ? ".exe" : ".zip");
        var directory = Path.Combine(downloadRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, installed ? "CardGrinder-Setup.exe" : "CardGrinder-Portable.zip");
        var partial = path + ".partial";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using var response = await _http.GetAsync(artifact.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != artifact.Size)
                throw new InvalidDataException("The update size does not match its signed information.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                double lastPercent = -1;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += read;
                    if (total > artifact.Size) throw new InvalidDataException("The update exceeds its signed size.");
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    var percent = Math.Floor(total * 100.0 / artifact.Size);
                    if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                }
                if (total != artifact.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(artifact.Sha256)))
                    throw new CryptographicException("The downloaded update failed verification.");
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partial, path);
            return new(path, artifact);
        }
        catch
        {
            try { File.Delete(partial); Directory.Delete(directory); } catch (IOException) { }
            throw;
        }
    }

    public static async Task VerifyDownloadAsync(UpdateDownload download, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(download.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (file.Length != download.Artifact.Size || !CryptographicOperations.FixedTimeEquals(
            await SHA256.HashDataAsync(file, cancellationToken), Convert.FromHexString(download.Artifact.Sha256)))
            throw new CryptographicException("The update file changed. Download it again.");
    }

    public static Version ParseVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Split('.').Length != 3
            || value.Any(c => c != '.' && (c < '0' || c > '9')) || !Version.TryParse(value, out var version))
            throw new InvalidDataException("A stable three-part release version is required.");
        return version;
    }

    private static Uri RequireHttps(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Updates require an HTTPS address without credentials.");
        return uri;
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
