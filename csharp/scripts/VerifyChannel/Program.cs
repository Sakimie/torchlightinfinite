using System.Security.Cryptography;
using System.Text.Json;
using Talos.Updates;

if (args.Length is not (3 or 5)) throw new ArgumentException("Expected public feed URL, pinned public key file, expected version; optional baseline signed feed and package");
string temp = Path.Combine(Path.GetTempPath(), "talos-csharp-live-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    using var service = new UpdateService(temp, File.ReadAllText(args[1]), args[0]);
    byte[]? baselineFeed = args.Length == 5 ? File.ReadAllBytes(args[3]) : null;
    byte[]? baselinePackage = args.Length == 5 ? File.ReadAllBytes(args[4]) : null;
    string? baselineVersion = null;
    if (baselineFeed is not null && baselinePackage is not null)
    {
        UpdateStatus baseline = await service.InstallBootstrapAsync(baselineFeed, baselinePackage);
        if (!baseline.ActiveValid) throw new InvalidDataException("Baseline installation failed");
        baselineVersion = baseline.ActiveVersion;
    }
    ReleaseManifest manifest = await service.CheckAsync();
    if (manifest.Version != args[2]) throw new InvalidDataException("Unexpected live channel version");
    UpdateStatus installed = await service.InstallAsync(manifest);
    if (!installed.ActiveValid || installed.ActiveVersion != args[2]) throw new InvalidDataException("Live installation was not activated");
    string active = await service.GetActiveDirectoryAsync();
    if (!File.Exists(Path.Combine(active, "Talos.App.dll")) || !File.Exists(Path.Combine(active, "Talos.Core.dll"))) throw new InvalidDataException("Installed C# application files missing");
    UpdateStatus local = await service.StatusAsync();
    if (!local.ActiveValid) throw new InvalidDataException("Local revalidation failed");
    string? rollbackVersion = null;
    bool? originalHostPreservesUpdate = null;
    if (baselineFeed is not null && baselinePackage is not null)
    {
        using var offlineClient = new HttpClient(new DenyNetwork());
        using var offline = new UpdateService(temp, File.ReadAllText(args[1]), args[0], offlineClient);
        UpdateStatus reboot = await offline.InstallBootstrapAsync(baselineFeed, baselinePackage);
        if (!reboot.ActiveValid || reboot.ActiveVersion != args[2]) throw new InvalidDataException("Original host bootstrap downgraded the update");
        originalHostPreservesUpdate = true;
        UpdateStatus rollback = await offline.RollbackAsync(baselineVersion);
        if (!rollback.ActiveValid || rollback.ActiveVersion != baselineVersion) throw new InvalidDataException("Offline rollback to baseline failed");
        rollbackVersion = rollback.ActiveVersion;
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        passed = true, version = installed.ActiveVersion, actualHttpsTransport = true,
        signatureAlgorithm = "ES256", signedPayloadBytes = manifest.Package.Bytes,
        signedPayloadSha256 = manifest.Package.Sha256,
        windowsExeExecuted = false, windowsAssembliesLoaded = false,
        privateKeyRead = false, activeFilesVerified = true,
        upgradedFrom = baselineVersion, originalHostPreservesUpdate, offlineRollbackVersion = rollbackVersion
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally { Directory.Delete(temp, recursive: true); }

sealed class DenyNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Offline verification attempted networking");
}
