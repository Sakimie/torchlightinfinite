using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// This checker runs only the original platform-neutral Talos.Updates library.
// It never executes the Windows EXE, loads WPF, or accesses a signing private key.
if (args.Length != 6) throw new ArgumentException("Expected old EXE, old feed, old package, new feed, public key, report path");
string oldExe = Path.GetFullPath(args[0]), oldFeed = Path.GetFullPath(args[1]), oldPackage = Path.GetFullPath(args[2]),
    newFeed = Path.GetFullPath(args[3]), publicKey = Path.GetFullPath(args[4]), reportPath = Path.GetFullPath(args[5]);
using var newEnvelope = JsonDocument.Parse(File.ReadAllBytes(newFeed));
using var newManifest = JsonDocument.Parse(Convert.FromBase64String(newEnvelope.RootElement.GetProperty("payload").GetString()!));
string attemptedVersion = newManifest.RootElement.GetProperty("version").GetString()!;
string launcherMinimum = newManifest.RootElement.GetProperty("launcherMin").GetString()!;
if (attemptedVersion != "0.4.2" || launcherMinimum != "1.1.0")
    throw new InvalidDataException("Expected exact portable 0.4.2 manifest with launcherMin 1.1.0");
string testRoot = Path.Combine(Path.GetTempPath(), "talos-legacy-gate-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var context = new AssemblyLoadContext("OriginalPublishedUpdater", isCollectible: true);
try
{
    byte[] oldLibrary = ExtractEntry(oldExe, "Talos.Updates.dll");
    using var dll = new MemoryStream(oldLibrary, false);
    Assembly assembly = context.LoadFromStream(dll);
    Type updater = assembly.GetType("Talos.Updates.UpdateService", true)!;
    var handler = new FeedOnlyHandler(File.ReadAllBytes(newFeed));
    using var http = new HttpClient(handler);
    object service = Activator.CreateInstance(updater, [testRoot, File.ReadAllText(publicKey), "https://qa.example.invalid/latest.json", http])!;
    try
    {
        object before = await Invoke(service, "InstallBootstrapAsync", [File.ReadAllBytes(oldFeed), File.ReadAllBytes(oldPackage), CancellationToken.None]);
        if (Property<string>(before, "ActiveVersion") != "0.4.1" || !Property<bool>(before, "ActiveValid"))
            throw new InvalidDataException("Actual old signed bootstrap did not initialize 0.4.1");
        if (handler.Requests.Count != 0) throw new InvalidDataException("Offline old bootstrap unexpectedly used HTTP");
        string activeDirectory = (string)await Invoke(service, "GetActiveDirectoryAsync", [CancellationToken.None]);
        byte[] originalState = File.ReadAllBytes(Path.Combine(testRoot, "state.json"));
        var originalFiles = Snapshot(activeDirectory);
        string? rejection = null;
        try { await Invoke(service, "CheckAsync", [CancellationToken.None]); }
        catch (Exception error)
        {
            error = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            if (error.GetType().FullName != "Talos.Updates.UpdateException") throw;
            rejection = Property<string>(error, "Code");
        }
        if (rejection != "launcher") throw new InvalidDataException("Actual legacy updater failed to reject new launcher minimum");
        if (handler.Requests.Count != 1 || handler.Requests[0] != "https://qa.example.invalid/latest.json")
            throw new InvalidDataException("Legacy check requested unexpected content or downloaded a package");
        handler.DenyAll = true;
        object after = await Invoke(service, "StatusAsync", [CancellationToken.None]);
        if (Property<string>(after, "ActiveVersion") != "0.4.1" || !Property<bool>(after, "ActiveValid"))
            throw new InvalidDataException("Legacy active changed after launcher-gated check");
        string afterDirectory = (string)await Invoke(service, "GetActiveDirectoryAsync", [CancellationToken.None]);
        if (afterDirectory != activeDirectory || !File.ReadAllBytes(Path.Combine(testRoot, "state.json")).SequenceEqual(originalState) ||
            !Snapshot(afterDirectory).OrderBy(x => x.Key).SequenceEqual(originalFiles.OrderBy(x => x.Key)))
            throw new InvalidDataException("Legacy state or active files changed");
        if (Directory.Exists(Path.Combine(testRoot, "releases", "0.4.2"))) throw new InvalidDataException("Gated release was written");
        if (context.Assemblies.Any(item => item.GetName().Name is "Talos.App" or "Talos.Core"))
            throw new InvalidDataException("Check loaded a game assembly");
        var report = new
        {
            passed = true, windowsExeExecuted = false, gameAssembliesLoaded = false,
            scope = "Linux execution of the exact pure .NET updater extracted from the originally published 0.4.0 EXE; fake HTTP only; no Windows UI execution.",
            originalHost = new { filename = Path.GetFileName(oldExe), bytes = new FileInfo(oldExe).Length, sha256 = Hash(File.ReadAllBytes(oldExe)) },
            originalUpdater = new { assembly = assembly.GetName().Name, bytes = oldLibrary.Length, sha256 = Hash(oldLibrary) },
            inputs = new { oldFeedSha256 = Hash(File.ReadAllBytes(oldFeed)), oldPackageSha256 = Hash(File.ReadAllBytes(oldPackage)),
                newFeedSha256 = Hash(File.ReadAllBytes(newFeed)), publicKeySha256 = Hash(File.ReadAllBytes(publicKey)) },
            initialActiveVersion = "0.4.1", attemptedVersion, newLauncherMinimum = launcherMinimum, checkRejectedWithCode = rejection,
            httpRequests = handler.Requests, packageDownloaded = false, activeAfterCheck = "0.4.1", activeValidAfterCheck = true,
            stateBytesUnchanged = true, activeFileHashesUnchanged = true, activeFileCount = originalFiles.Count,
            postCheckLocalMethodsUsedNoHttp = true
        };
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(reportPath, json); Console.WriteLine(json);
    }
    finally { ((IDisposable)service).Dispose(); }
}
finally
{
    context.Unload();
    Directory.Delete(testRoot, true);
}

static async Task<object> Invoke(object target, string name, object?[] arguments)
{
    object? result = target.GetType().GetMethod(name)!.Invoke(target, arguments);
    if (result is not Task task) throw new InvalidOperationException("Expected task");
    await task;
    return task.GetType().GetProperty("Result")!.GetValue(task)!;
}
static T Property<T>(object target, string name) => (T)target.GetType().GetProperty(name)!.GetValue(target)!;
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static Dictionary<string, string> Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
    .ToDictionary(path => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'), path => Hash(File.ReadAllBytes(path)), StringComparer.Ordinal);
static byte[] ExtractEntry(string filename, string wanted)
{
    Assembly model = Assembly.LoadFrom("/workspace/.dotnet/sdk/10.0.402/Microsoft.NET.HostModel.dll");
    var detect = model.GetType("Microsoft.NET.HostModel.Bundle.Bundler", true)!.GetMethod("IsBundle", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
    object?[] detection = [filename, 0L];
    if (detect.Invoke(null, detection) is not true) throw new InvalidDataException("Expected genuine single-file bundle");
    using var file = File.OpenRead(filename);
    using var binary = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
    file.Position = (long)detection[1]!;
    uint major = binary.ReadUInt32(); binary.ReadUInt32(); int count = binary.ReadInt32();
    if (major != 6 || count is < 5 or > 5000) throw new InvalidDataException("Unsupported bundle version");
    binary.ReadString(); for (int i = 0; i < 5; i++) binary.ReadUInt64();
    for (int i = 0; i < count; i++)
    {
        long offset = binary.ReadInt64(), size = binary.ReadInt64(), compressed = binary.ReadInt64();
        byte type = binary.ReadByte(); string name = binary.ReadString();
        if (name != wanted) continue;
        if (type != 1 || size is < 1 or > 2 * 1024 * 1024 || compressed < 0 || offset < 0 ||
            offset + (compressed > 0 ? compressed : size) > file.Length) throw new InvalidDataException("Invalid updater bundle entry");
        file.Position = offset;
        byte[] bytes = binary.ReadBytes(checked((int)(compressed > 0 ? compressed : size)));
        if (compressed == 0) return bytes;
        using var packed = new MemoryStream(bytes);
        using var inflate = new DeflateStream(packed, CompressionMode.Decompress);
        using var output = new MemoryStream(); inflate.CopyTo(output);
        if (output.Length != size) throw new InvalidDataException("Updater entry length mismatch");
        return output.ToArray();
    }
    throw new InvalidDataException("Original updater not in bundle");
}
internal sealed class FeedOnlyHandler(byte[] feed) : HttpMessageHandler
{
    public bool DenyAll;
    public List<string> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DenyAll) throw new InvalidOperationException("A local updater method used HTTP");
        string uri = request.RequestUri!.AbsoluteUri; Requests.Add(uri);
        if (uri != "https://qa.example.invalid/latest.json") throw new InvalidOperationException("Package download forbidden in gate check");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(feed) });
    }
}
