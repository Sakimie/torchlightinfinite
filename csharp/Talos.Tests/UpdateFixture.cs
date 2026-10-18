using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Talos.Updates;

namespace Talos.Tests;

/// <summary>Ephemeral test publisher and fake transport. Never reads a real publisher key.</summary>
internal sealed class UpdateFixture : IDisposable
{
    internal const string FeedUrl = "https://updates.example.invalid/csharp/latest.json";
    internal const string Commit = "0123456789abcdef0123456789abcdef01234567";
    internal static string PackageUrl(string version) => $"https://updates.example.invalid/{Commit}/talos-csharp-{version}.zip";
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "talos-csharp-qa-" + Guid.NewGuid().ToString("N"));
    public FakeTransport Transport { get; } = new();
    public string PublicKey => _key.ExportSubjectPublicKeyInfoPem();
    public UpdateFixture() => Directory.CreateDirectory(Root);
    public UpdateService Service(string? root = null) => new(root ?? Root, PublicKey, FeedUrl, new HttpClient(Transport, false));
    public TestRelease Release(string version, IReadOnlyDictionary<string, byte[]>? files = null,
        Action<ZipArchive>? editZip = null, Action<Dictionary<string, object?>>? editManifest = null)
    {
        files ??= new Dictionary<string, byte[]>
        {
            ["Talos.App.dll"] = Encoding.UTF8.GetBytes("QA inert App " + version),
            ["Talos.Core.dll"] = Encoding.UTF8.GetBytes("QA inert Core " + version),
            ["assets/templates.json"] = Encoding.UTF8.GetBytes("{\"qa\":true}")
        };
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Key, CompressionLevel.Optimal);
                using var output = entry.Open();
                output.Write(file.Value);
            }
        }
        if (editZip is not null)
        {
            using var zip = new ZipArchive(memory, ZipArchiveMode.Update, true);
            editZip(zip);
        }
        var package = memory.ToArray();
        var payload = new Dictionary<string, object?>
        {
            ["schema"] = "talos-csharp-release-v1", ["app"] = "TalosHelper", ["version"] = version,
            ["launcherMin"] = "1.0.0", ["publishedAt"] = "2026-10-17T00:00:00Z", ["notes"] = new[] { "Temporary QA fixture" },
            ["entryAssembly"] = "Talos.App.dll",
            ["package"] = new { url = PackageUrl(version), bytes = package.LongLength, sha256 = Sha(package) },
            ["files"] = files.Select(file => new { path = file.Key, bytes = file.Value.LongLength, sha256 = Sha(file.Value) }).ToArray()
        };
        editManifest?.Invoke(payload);
        var feed = Sign(JsonSerializer.SerializeToUtf8Bytes(payload));
        return new(version, feed, package, files);
    }
    public byte[] Sign(byte[] payload) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "talos-signed-csharp-feed-v1", algorithm = "ES256", payload = Convert.ToBase64String(payload),
        signature = Convert.ToBase64String(_key.SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
    });
    public void Serve(TestRelease release)
    {
        Transport.Responses[FeedUrl] = () => FakeTransport.Ok(release.Feed);
        Transport.Responses[PackageUrl(release.Version)] = () => FakeTransport.Ok(release.Package);
    }
    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}

internal sealed record TestRelease(string Version, byte[] Feed, byte[] Package, IReadOnlyDictionary<string, byte[]> Files);

internal sealed class FakeTransport : HttpMessageHandler
{
    public Dictionary<string, Func<HttpResponseMessage>> Responses { get; } = new(StringComparer.Ordinal);
    public List<string> Requests { get; } = [];
    public bool DenyAll { get; set; }
    public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri?.AbsoluteUri ?? "(missing)";
        Requests.Add(uri);
        if (DenyAll) throw new InvalidOperationException("Network used during an offline operation: " + uri);
        if (!Responses.TryGetValue(uri, out var response)) throw new InvalidOperationException("Unexpected network request: " + uri);
        var value = response();
        value.RequestMessage ??= request;
        return Task.FromResult(value);
    }
}
