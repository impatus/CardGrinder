using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CardGrinder.Core;
using Xunit;

namespace CardGrinder.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly RSA key = RSA.Create(3072);
    private readonly byte[] package = Encoding.UTF8.GetBytes("verified test package");
    private readonly string root = Path.Combine(Path.GetTempPath(), "CardGrinder-Update-" + Guid.NewGuid());
    private UpdateChannel Channel => new(true, "https://impatus.no/uploads/cardgrinder/latest.json", key.ExportSubjectPublicKeyInfoPem());
    private UpdateRelease Release => new(1, "CardGrinder", "0.4.0", DateTimeOffset.UtcNow, "New release",
        new("https://impatus.no/uploads/cardgrinder/setup.exe", Convert.ToHexString(SHA256.HashData(package)), package.Length),
        new("https://impatus.no/uploads/cardgrinder/portable.zip", Convert.ToHexString(SHA256.HashData(package)), package.Length));
    private byte[] Sign(UpdateRelease release, RSA? signingKey = null)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(release, AppUpdateService.JsonOptions);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateManifest(Convert.ToBase64String(payload),
            Convert.ToBase64String((signingKey ?? key).SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))), AppUpdateService.JsonOptions);
    }
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => new(new Handler(response));
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    [Fact]
    public async Task DisabledChannelMakesNoRequest()
    {
        using var client = Client((_, _) => throw new Exception("Must not request"));
        using var service = new AppUpdateService(Channel with { Enabled = false }, new(0, 3, 0), client);
        Assert.Null(await service.CheckAsync(default));
    }
    [Theory]
    [InlineData("0.2.0", false)]
    [InlineData("0.3.0", false)]
    [InlineData("0.4.0", true)]
    public async Task OnlyNewerStableReleasesAreOffered(string version, bool offered)
    {
        using var client = Client((_, _) => Task.FromResult(Response(Sign(Release with { Version = version }))));
        using var service = new AppUpdateService(Channel, new(0, 3, 0), client);
        Assert.Equal(offered, await service.CheckAsync(default) is not null);
    }
    [Fact]
    public void RejectsWrongKeyAndChangedPayload()
    {
        using var service = new AppUpdateService(Channel, new(0, 3, 0));
        using var other = RSA.Create(3072);
        Assert.Throws<CryptographicException>(() => service.VerifyManifest(Sign(Release, other)));
        var envelope = JsonSerializer.Deserialize<SignedUpdateManifest>(Sign(Release), AppUpdateService.JsonOptions)!;
        byte[] payload = Convert.FromBase64String(envelope.Payload); payload[0] ^= 1;
        Assert.Throws<CryptographicException>(() => service.VerifyManifest(JsonSerializer.SerializeToUtf8Bytes(envelope with { Payload = Convert.ToBase64String(payload) })));
    }
    [Theory]
    [InlineData("http://impatus.no/setup.exe")]
    [InlineData("https://example.com/setup.exe")]
    [InlineData("https://secret@impatus.no/setup.exe")]
    [InlineData("https://impatus.no/setup.cmd")]
    public void RejectsUntrustedArtifactUrls(string url)
    {
        using var service = new AppUpdateService(Channel, new(0, 3, 0));
        Assert.Throws<InvalidDataException>(() => service.VerifyManifest(Sign(Release with { Installer = Release.Installer with { Url = url } })));
    }
    [Fact]
    public void RejectsWrongAppAndOversizeMetadata()
    {
        using var service = new AppUpdateService(Channel, new(0, 3, 0));
        Assert.Throws<InvalidDataException>(() => service.VerifyManifest(Sign(Release with { AppId = "Playboi" })));
        Assert.Throws<InvalidDataException>(() => service.VerifyManifest(new byte[128 * 1024 + 1]));
        Assert.Throws<InvalidDataException>(() => service.VerifyManifest(Sign(Release with { Installer = Release.Installer with { Size = 2L * 1024 * 1024 * 1024 } })));
    }
    [Theory]
    [InlineData(true, ".exe")]
    [InlineData(false, ".zip")]
    public async Task DownloadsCorrectEditionAndRechecksBeforeOpening(bool installed, string extension)
    {
        Uri? requested = null;
        using var client = Client((request, _) => { requested = request.RequestUri; return Task.FromResult(Response(package)); });
        using var service = new AppUpdateService(Channel, new(0, 3, 0), client);
        var release = service.VerifyManifest(Sign(Release));
        var download = await service.DownloadAsync(release, installed, root, null, default);
        Assert.EndsWith(extension, requested!.AbsolutePath);
        await AppUpdateService.VerifyDownloadAsync(download, default);
        await File.WriteAllTextAsync(download.Path, "modified");
        await Assert.ThrowsAsync<CryptographicException>(() => AppUpdateService.VerifyDownloadAsync(download, default));
    }
    [Fact]
    public async Task RejectsUnverifiedReleaseAndRemovesFailedDownload()
    {
        using var client = Client((_, _) => Task.FromResult(Response(new byte[package.Length])));
        using var service = new AppUpdateService(Channel, new(0, 3, 0), client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAsync(Release, true, root, null, default));
        var release = service.VerifyManifest(Sign(Release));
        await Assert.ThrowsAsync<CryptographicException>(() => service.DownloadAsync(release, true, root, null, default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }
    [Fact]
    public async Task CancelsDownloadAndRejectsRedirects()
    {
        using var client = Client(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response(package); });
        using var service = new AppUpdateService(Channel, new(0, 3, 0), client);
        var release = service.VerifyManifest(Sign(Release));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(release, true, root, null, cancelled.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        using var redirects = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)));
        using var redirectService = new AppUpdateService(Channel, new(0, 3, 0), redirects);
        await Assert.ThrowsAsync<HttpRequestException>(() => redirectService.CheckAsync(default));
    }
    [Theory]
    [InlineData("v0.4.0")]
    [InlineData("0.4")]
    [InlineData("0.4.0-beta")]
    public void RejectsAmbiguousVersions(string version) => Assert.Throws<InvalidDataException>(() => AppUpdateService.ParseVersion(version));
    public void Dispose() { key.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
}
