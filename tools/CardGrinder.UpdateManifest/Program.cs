using System.Security.Cryptography;
using System.Text.Json;
using CardGrinder.Core;

if (args.Length == 3 && (args[0] == "verify" || args[0] == "check"))
{
    var channel = JsonSerializer.Deserialize<UpdateChannel>(File.ReadAllText(args[1]), AppUpdateService.JsonOptions)
        ?? throw new InvalidDataException("Missing channel configuration.");
    using var service = new AppUpdateService(channel,
        args[0] == "check" ? AppUpdateService.ParseVersion(args[2]) : new Version(0, 0, 0));
    var result = args[0] == "verify" ? service.VerifyManifest(File.ReadAllBytes(args[2]))
        : await service.CheckAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result, AppUpdateService.JsonOptions));
    return 0;
}
if (args.Length == 3 && args[0] == "keygen")
{
    if (File.Exists(args[1]) || File.Exists(args[2])) throw new IOException("Refusing to overwrite an existing signing key.");
    using var key = RSA.Create(3072);
    File.WriteAllText(args[1], key.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(args[2], key.ExportSubjectPublicKeyInfoPem());
    Console.WriteLine("Signing key created. Keep the private PEM outside the repository and release folders.");
    return 0;
}
if (args.Length != 7 || args[0] != "sign")
{
    Console.WriteLine("keygen <private.pem> <public.pem>");
    Console.WriteLine("sign <version> <release-directory> <https-base-url> <notes.txt> <private.pem> <latest.json>");
    Console.WriteLine("verify <channel.json> <latest.json>");
    Console.WriteLine("check <channel.json> <current-version>");
    return 1;
}
var version = AppUpdateService.ParseVersion(args[1]).ToString(3);
var directory = Path.GetFullPath(args[2]);
var baseUrl = new Uri(args[3].TrimEnd('/') + "/");
if (baseUrl.Scheme != "https" || baseUrl.UserInfo.Length != 0 || baseUrl.Query.Length != 0 || baseUrl.Fragment.Length != 0)
    throw new ArgumentException("An HTTPS release directory URL is required.");
UpdateArtifact Artifact(string name)
{
    var file = new FileInfo(Path.Combine(directory, name));
    using var stream = file.OpenRead();
    return new(new Uri(baseUrl, name).AbsoluteUri, Convert.ToHexString(SHA256.HashData(stream)), file.Length);
}
var release = new UpdateRelease(1, "CardGrinder", version, DateTimeOffset.UtcNow, File.ReadAllText(args[4]).Trim(),
    Artifact($"CardGrinder-{version}-Setup-x64.exe"), Artifact($"CardGrinder-{version}-Portable-x64.zip"));
using var signingKey = RSA.Create();
signingKey.ImportFromPem(File.ReadAllText(args[5]));
var payload = JsonSerializer.SerializeToUtf8Bytes(release, AppUpdateService.JsonOptions);
var envelope = new SignedUpdateManifest(Convert.ToBase64String(payload),
    Convert.ToBase64String(signingKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
var json = JsonSerializer.SerializeToUtf8Bytes(envelope, AppUpdateService.JsonOptions);
using var verifier = new AppUpdateService(new(true, new Uri(baseUrl, "latest.json").AbsoluteUri,
    signingKey.ExportSubjectPublicKeyInfoPem()), new(0, 0, 0));
verifier.VerifyManifest(json);
File.WriteAllBytes(args[6], json);
Console.WriteLine($"Signed CardGrinder {version}. No files were uploaded.");
return 0;

