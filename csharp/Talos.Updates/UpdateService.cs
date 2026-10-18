using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Talos.Updates;

/// <summary>Signed, local-first updates. Only CheckAsync and InstallAsync use HTTP.</summary>
public sealed class UpdateService : IDisposable
{
    public const string LauncherVersion = "1.1.0";
    public const int MaximumFeedBytes = 1024 * 1024;
    public const long MaximumPackageBytes = 64L * 1024 * 1024;
    public const long MaximumFileBytes = 32L * 1024 * 1024;
    public const long MaximumExpandedBytes = 128L * 1024 * 1024;
    public const int MaximumFiles = 256;
    private const string StateSchema = "talos-csharp-state-v1";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex VersionPattern = new(@"^(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})\.(0|[1-9][0-9]{0,3})$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private readonly string root;
    private readonly string publicKey;
    private readonly string feedUrl;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly SemaphoreSlim gate;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public UpdateService(string dataRoot, string publicKeyPem, string feedUrl, HttpClient? client = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        root = Path.GetFullPath(dataRoot);
        publicKey = publicKeyPem;
        this.feedUrl = feedUrl;
        using (var key = OpenKey()) { }
        if (!string.IsNullOrEmpty(feedUrl)) ValidateUrl(feedUrl);
        this.client = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false
        }) { Timeout = TimeSpan.FromMinutes(3) };
        ownsClient = client == null;
        gate = Gates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
    }

    public void Dispose() { if (ownsClient) client.Dispose(); }

    public async Task<UpdateStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
        return await SnapshotAsync(ReadState(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReleaseManifest> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(feedUrl)) throw Error("未配置更新地址。", "network");
        var bytes = await DownloadAsync(feedUrl, MaximumFeedBytes, null, null, cancellationToken).ConfigureAwait(false);
        return Authenticate(bytes);
    }

    public async Task<UpdateStatus> InstallAsync(ReleaseManifest manifest, IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var authenticated = Authenticate(Utf8.GetBytes(manifest.EnvelopeJson));
        if (authenticated.Identity != manifest.Identity) throw Error("更新认证对象不匹配。", "signature");
        using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
        var state = ReadState();
        EnsureIdentity(state, authenticated);
        var package = await DownloadAsync(authenticated.Package.Url, authenticated.Package.Bytes,
            authenticated.Package.Bytes, progress, cancellationToken).ConfigureAwait(false);
        return await InstallPackageAsync(state, authenticated, package, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateStatus> InstallBootstrapAsync(byte[] signedFeed, byte[] package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signedFeed);
        ArgumentNullException.ThrowIfNull(package);
        var manifest = Authenticate(signedFeed);
        using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
        var state = ReadState();
        // Embedded bootstrap must never silently downgrade an already initialized installation.
        if (state.Active != null && CompareVersions(manifest.Version, state.Active) < 0)
            return await SnapshotAsync(state, cancellationToken).ConfigureAwait(false);
        EnsureIdentity(state, manifest);
        return await InstallPackageAsync(state, manifest, package, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateStatus> RollbackAsync(string? version = null, CancellationToken cancellationToken = default)
    {
        using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
        var state = ReadState();
        var healthy = await HealthyAsync(state, cancellationToken).ConfigureAwait(false);
        if (version != null) ValidateVersion(version);
        var selected = version ?? state.History.Concat(healthy.Keys.OrderByDescending(x => Version.Parse(x)))
            .FirstOrDefault(v => v != state.Active && healthy.ContainsKey(v));
        if (selected == null || !healthy.ContainsKey(selected))
            throw Error("没有可回退的完整本地版本。", "rollback");
        if (selected != state.Active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Activate(state, selected);
            WriteState(state);
        }
        return await SnapshotAsync(state, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<string> GetActiveDirectoryAsync(CancellationToken cancellationToken = default)
    {
        using var held = await LockAsync(cancellationToken).ConfigureAwait(false);
        var state = ReadState();
        if (state.Active == null || !state.Installed.TryGetValue(state.Active, out var envelope))
            throw Error("没有已验证的活动版本。请导入更新或回退。", "active");
        var manifest = Authenticate(Utf8.GetBytes(envelope));
        if (manifest.Version != state.Active) throw Error("活动版本签名不匹配。", "active");
        var path = ReleasePath(manifest.Version);
        await VerifyDirectoryAsync(path, manifest, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private ECDsa OpenKey()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(publicKey) || publicKey.Contains("PRIVATE", StringComparison.Ordinal) ||
                !publicKey.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
                throw new CryptographicException("Expected public key");
            var key = ECDsa.Create();
            try
            {
                key.ImportFromPem(publicKey);
                if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
                    throw new CryptographicException("Expected P-256");
                return key;
            }
            catch { key.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        { throw Error("更新公钥无效。", "key", ex); }
    }

    private ReleaseManifest Authenticate(byte[] signedFeed)
    {
        if (signedFeed.Length > MaximumFeedBytes) throw Error("更新清单超过大小限制。", "manifest");
        try
        {
            var envelopeJson = Utf8.GetString(signedFeed);
            using var envelope = ParseJson(envelopeJson);
            var e = envelope.RootElement;
            Keys(e, "schema", "algorithm", "payload", "signature");
            if (String(e, "schema") != "talos-signed-csharp-feed-v1" || String(e, "algorithm") != "ES256")
                throw Error("不支持的更新签名格式。", "signature");
            var payload = Base64(String(e, "payload"));
            var signature = Base64(String(e, "signature"));
            if (payload.Length > MaximumFeedBytes || signature.Length != 64)
                throw Error("更新签名大小无效。", "signature");
            using (var key = OpenKey())
                if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    throw Error("更新签名验证失败。", "signature");
            using var document = ParseJson(Utf8.GetString(payload));
            var m = document.RootElement;
            Keys(m, "schema", "app", "version", "launcherMin", "publishedAt", "notes", "entryAssembly", "package", "files");
            if (String(m, "schema") != "talos-csharp-release-v1" || String(m, "app") != "TalosHelper")
                throw Error("更新不属于此应用。", "manifest");
            var version = String(m, "version"); ValidateVersion(version);
            var minimum = String(m, "launcherMin"); ValidateVersion(minimum);
            if (CompareVersions(minimum, LauncherVersion) > 0) throw Error("此更新需要新版启动器，请下载完整便携包后再使用。", "launcher");
            var publishedAt = String(m, "publishedAt");
            if (publishedAt.Length > 64 || !DateTimeOffset.TryParse(publishedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time) || time.Offset != TimeSpan.Zero ||
                !(publishedAt.EndsWith('Z') || publishedAt.EndsWith("+00:00", StringComparison.Ordinal)))
                throw Error("更新时间格式无效。", "manifest");
            var notes = m.GetProperty("notes");
            if (notes.ValueKind != JsonValueKind.Array || notes.GetArrayLength() > 30) throw Error("更新说明无效。", "manifest");
            var text = notes.EnumerateArray().Select(n => n.ValueKind == JsonValueKind.String && n.GetString()!.Length <= 4000
                ? n.GetString()! : throw Error("更新说明无效。", "manifest")).ToArray();
            var entry = String(m, "entryAssembly");
            if (entry != "Talos.App.dll") throw Error("更新入口无效。", "manifest");
            var p = m.GetProperty("package"); Keys(p, "url", "bytes", "sha256");
            var url = String(p, "url"); ValidateUrl(url);
            var package = new UpdatePackage(url, Size(p, "bytes", MaximumPackageBytes), Hash(p, "sha256"));
            if (package.Bytes == 0) throw Error("更新包为空。", "manifest");
            var array = m.GetProperty("files");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 2 or > MaximumFiles)
                throw Error("更新文件数量无效。", "manifest");
            var files = new List<ReleaseFile>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var f in array.EnumerateArray())
            {
                Keys(f, "path", "bytes", "sha256");
                var name = String(f, "path"); ValidateRelativePath(name);
                // A release cannot replace the stable host/runtime or shared verification assembly.
                var leaf = name.Split('/')[^1];
                if (leaf.Equals("Talos.Updates.dll", StringComparison.OrdinalIgnoreCase) ||
                    leaf.StartsWith("Talos.Host", StringComparison.OrdinalIgnoreCase) ||
                    leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    name.Split('/').Any(x => x.Equals("runtime", StringComparison.OrdinalIgnoreCase) || x.Equals("_internal", StringComparison.OrdinalIgnoreCase)))
                    throw Error("更新包含禁止替换的运行组件。", "manifest");
                if (!names.Add(name)) throw Error("更新包含重复文件路径。", "path");
                var bytes = Size(f, "bytes", MaximumFileBytes);
                expanded = checked(expanded + bytes);
                if (expanded > MaximumExpandedBytes) throw Error("更新展开大小超过限制。", "size");
                files.Add(new ReleaseFile(name, bytes, Hash(f, "sha256")));
            }
            if (!names.Contains("Talos.App.dll") || !names.Contains("Talos.Core.dll") ||
                !files.Any(f => f.Path == "Talos.App.dll") || !files.Any(f => f.Path == "Talos.Core.dll"))
                throw Error("更新缺少应用组件。", "manifest");
            foreach (var name in names)
                for (var index = name.IndexOf('/'); index >= 0; index = name.IndexOf('/', index + 1))
                    if (names.Contains(name[..index])) throw Error("更新文件与目录路径冲突。", "path");
            return new ReleaseManifest(version, minimum, publishedAt, text, entry, package, files,
                envelopeJson, Convert.ToHexStringLower(SHA256.HashData(payload)));
        }
        catch (UpdateException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or
            KeyNotFoundException or DecoderFallbackException or OverflowException or CryptographicException)
        { throw Error("更新清单或签名格式无效。", "manifest", ex); }
    }

    private async Task<byte[]> DownloadAsync(string url, long maximum, long? exact,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            for (var redirects = 0; redirects <= 5; redirects++)
            {
                var uri = ValidateUrl(url);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("TalosHelper-CSharp/1.0");
                request.Headers.AcceptEncoding.ParseAdd("identity");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } finalUri) ValidateUrl(finalUri.AbsoluteUri);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects == 5 || response.Headers.Location == null) throw Error("更新地址重定向过多。", "network");
                    url = new Uri(uri, response.Headers.Location).AbsoluteUri;
                    ValidateUrl(url);
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK) throw Error($"更新下载失败（HTTP {(int)response.StatusCode}）。", "network");
                if (response.Content.Headers.ContentEncoding.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw Error("更新服务器返回了不支持的内容编码。", "network");
                var declared = response.Content.Headers.ContentLength;
                if (declared > maximum || (exact.HasValue && declared.HasValue && declared != exact))
                    throw Error("更新下载大小不匹配。", "size");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[64 * 1024];
                while (true)
                {
                    var count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    if (buffer.Length + count > maximum) throw Error("更新下载超过大小限制。", "size");
                    buffer.Write(chunk, 0, count);
                    progress?.Report(new UpdateProgress("download", buffer.Length, exact ?? declared ?? 0, "正在下载更新"));
                }
                if ((exact.HasValue && buffer.Length != exact) || (declared.HasValue && buffer.Length != declared))
                    throw Error("更新下载不完整。", "size");
                return buffer.ToArray();
            }
            throw Error("更新重定向无效。", "network");
        }
        catch (OperationCanceledException) { throw; }
        catch (UpdateException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        { throw Error("无法通过 HTTPS 下载更新。", "network", ex); }
    }

    private async Task<UpdateStatus> InstallPackageAsync(State state, ReleaseManifest manifest, byte[] package,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new UpdateProgress("verify", 0, package.LongLength, "正在验证更新包"));
        if (package.LongLength != manifest.Package.Bytes || package.LongLength > MaximumPackageBytes ||
            !HashEquals(package, manifest.Package.Sha256)) throw Error("更新包大小或 SHA256 不匹配。", "hash");
        var stage = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
        string? backup = null;
        var target = ReleasePath(manifest.Version);
        var published = false;
        var committed = false;
        try
        {
            Directory.CreateDirectory(stage);
            EnsureNoLinks(stage);
            await ExtractAsync(package, stage, manifest, progress, cancellationToken).ConfigureAwait(false);
            await VerifyDirectoryAsync(stage, manifest, cancellationToken).ConfigureAwait(false);
            progress?.Report(new UpdateProgress("activate", 0, 1, "正在切换已验证版本"));
            cancellationToken.ThrowIfCancellationRequested();
            // No cancellation after this point: a commit must complete or restore its prior state.
            if (Directory.Exists(target))
            {
                try { await VerifyDirectoryAsync(target, manifest, CancellationToken.None).ConfigureAwait(false); }
                catch (UpdateException)
                {
                    // A crash can leave a partial orphan. Never adopt unknown files or symlinks.
                    VerifyRepairableSubset(target, manifest, state.Installed.ContainsKey(manifest.Version));
                    backup = Path.Combine(root, ".repair-backup-" + Guid.NewGuid().ToString("N"));
                    Directory.Move(target, backup);
                    Directory.Move(stage, target);
                    published = true;
                }
            }
            else
            {
                EnsureNoLinks(Path.GetDirectoryName(target)!);
                Directory.Move(stage, target);
                published = true;
            }
            state.Installed[manifest.Version] = manifest.EnvelopeJson;
            Activate(state, manifest.Version);
            WriteState(state);
            committed = true;
            return await SnapshotAsync(state, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (UpdateException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        { throw Error("无法安全安装更新；原活动版本已保留。", "install", ex); }
        finally
        {
            if (!committed)
            {
                if (published) TryDeleteDirectory(target);
                if (backup != null && Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target);
            }
            TryDeleteDirectory(stage);
            if (committed && backup != null) TryDeleteDirectory(backup);
        }
    }

    private async Task ExtractAsync(byte[] package, string stage, ReleaseManifest manifest,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            using var memory = new MemoryStream(package, false);
            using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
            if (zip.Entries.Count != manifest.Files.Count) throw Error("更新包文件数量不匹配。", "zip");
            var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long done = 0;
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRelativePath(entry.FullName);
                var attributes = unchecked((uint)entry.ExternalAttributes);
                var unixType = (attributes >> 16) & 0xF000;
                if (unixType is not (0 or 0x8000) || (attributes & (0x10 | 0x400)) != 0 ||
                    !seen.Add(entry.FullName) || !expected.TryGetValue(entry.FullName, out var file))
                    throw Error("更新包含未声明、重复或特殊文件。", "zip");
                if (entry.Length != file.Bytes || entry.Length > MaximumFileBytes ||
                    (entry.Length > 1024 * 1024 && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > 200)))
                    throw Error("更新文件大小或压缩比例无效。", "zip");
                var destination = Path.Combine(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                EnsureNoLinks(destination);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var chunk = new byte[64 * 1024];
                long copied = 0;
                while (true)
                {
                    var count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    copied += count;
                    if (copied > file.Bytes) throw Error("更新文件展开超过声明大小。", "zip");
                    hash.AppendData(chunk, 0, count);
                    await output.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                if (copied != file.Bytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                    throw Error("更新文件 SHA256 不匹配。", "hash");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
                done += copied;
                progress?.Report(new UpdateProgress("extract", done, manifest.Files.Sum(f => f.Bytes), "正在展开已验证文件"));
            }
        }
        catch (InvalidDataException ex) { throw Error("更新 ZIP 损坏或格式不受支持。", "zip", ex); }
    }

    private async Task VerifyDirectoryAsync(string directory, ReleaseManifest manifest, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) throw Error("本地版本目录不存在。", "files");
        var actual = EnumerateFiles(directory);
        if (actual.Count != manifest.Files.Count) throw Error("本地版本包含缺失或额外文件。", "files");
        VerifyDeclaredDirectories(directory, manifest);
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(file.Path, out var path)) throw Error("本地版本文件缺失。", "files");
            EnsureNoLinks(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != file.Bytes) throw Error("本地版本文件大小已变化。", "files");
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (Convert.ToHexStringLower(hash) != file.Sha256) throw Error("本地版本文件校验失败。", "files");
        }
    }

    private Dictionary<string, string> EnumerateFiles(string directory)
    {
        EnsureNoLinks(directory);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(); pending.Push(directory);
        var directories = 0;
        while (pending.Count > 0)
        {
            if (++directories > MaximumFiles * 16) throw Error("本地版本目录过多。", "files");
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                EnsureNoLinks(path);
                var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                ValidateRelativePath(relative);
                if (!aliases.Add(relative)) throw Error("本地版本存在路径别名。", "path");
                if (Directory.Exists(path)) pending.Push(path);
                else
                {
                    if (result.Count >= MaximumFiles) throw Error("本地版本文件过多。", "files");
                    result.Add(relative, path);
                }
            }
        }
        return result;
    }

    private void VerifyRepairableSubset(string target, ReleaseManifest manifest, bool registered)
    {
        var files = EnumerateFiles(target);
        VerifyDeclaredDirectories(target, manifest);
        var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        if (files.Keys.Any(name => !expected.ContainsKey(name))) throw Error("已有版本包含未知文件，不能自动替换。", "files");
        // Registered damaged files may be repaired. Unregistered orphans must be a genuine signed subset.
        if (!registered)
            foreach (var (name, path) in files)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != expected[name].Bytes ||
                    Convert.ToHexStringLower(SHA256.HashData(stream)) != expected[name].Sha256)
                    throw Error("未注册版本包含无法认证的文件。", "files");
            }
    }

    private static void VerifyDeclaredDirectories(string directory, ReleaseManifest manifest)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
            for (var index = file.Path.IndexOf('/'); index >= 0; index = file.Path.IndexOf('/', index + 1))
                allowed.Add(file.Path[..index]);
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.Count > 0)
            foreach (var path in Directory.EnumerateDirectories(pending.Pop()))
            {
                EnsureNoLinks(path);
                var relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                if (!allowed.Contains(relative)) throw Error("本地版本包含未声明的目录。", "files");
                pending.Push(path);
            }
    }

    private async Task<Dictionary<string, ReleaseManifest>> HealthyAsync(State state, CancellationToken cancellationToken)
    {
        var healthy = new Dictionary<string, ReleaseManifest>(StringComparer.Ordinal);
        foreach (var (version, envelope) in state.Installed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = Authenticate(Utf8.GetBytes(envelope));
                if (manifest.Version != version) throw Error("版本记录不匹配。", "state");
                await VerifyDirectoryAsync(ReleasePath(version), manifest, cancellationToken).ConfigureAwait(false);
                healthy.Add(version, manifest);
            }
            catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
            { state.Warnings.Add($"本地版本 {version} 不完整或校验失败。"); }
        }
        return healthy;
    }

    private async Task<UpdateStatus> SnapshotAsync(State state, CancellationToken cancellationToken)
    {
        var healthy = await HealthyAsync(state, cancellationToken).ConfigureAwait(false);
        return new UpdateStatus(state.Active, state.Active != null && healthy.ContainsKey(state.Active),
            healthy.Keys.OrderByDescending(v => Version.Parse(v)).ToArray())
        {
            History = state.History.Where(healthy.ContainsKey).ToArray(),
            Warnings = state.Warnings.Distinct().ToArray()
        };
    }

    private sealed class State
    {
        public string? Active;
        public List<string> History = new();
        public Dictionary<string, string> Installed = new(StringComparer.Ordinal);
        public List<string> Warnings = new();
    }

    private State ReadState()
    {
        var path = Path.Combine(root, "state.json");
        EnsureNoLinks(path);
        if (!File.Exists(path)) return new State();
        try
        {
            if (new FileInfo(path).Length > 8 * 1024 * 1024) throw Error("版本状态文件过大。", "state");
            using var document = ParseJson(File.ReadAllText(path, Utf8));
            var e = document.RootElement; Keys(e, "schema", "active", "history", "installed");
            if (String(e, "schema") != StateSchema) throw Error("版本状态格式不受支持。", "state");
            var state = new State();
            var active = e.GetProperty("active");
            if (active.ValueKind != JsonValueKind.Null) { state.Active = active.GetString(); ValidateVersion(state.Active!); }
            var history = e.GetProperty("history");
            if (history.ValueKind != JsonValueKind.Array || history.GetArrayLength() > 128) throw Error("版本历史无效。", "state");
            foreach (var v in history.EnumerateArray()) { var text = v.GetString()!; ValidateVersion(text); if (!state.History.Contains(text)) state.History.Add(text); }
            var installed = e.GetProperty("installed");
            if (installed.ValueKind != JsonValueKind.Object || installed.EnumerateObject().Count() > 128) throw Error("版本记录无效。", "state");
            foreach (var record in installed.EnumerateObject())
            {
                ValidateVersion(record.Name); Keys(record.Value, "feed");
                var feed = record.Value.GetProperty("feed");
                if (feed.ValueKind != JsonValueKind.Object) throw Error("版本签名记录无效。", "state");
                state.Installed.Add(record.Name, feed.GetRawText());
            }
            return state;
        }
        catch (UpdateException) { throw; }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ArgumentException or DecoderFallbackException)
        { throw Error("无法读取版本状态；请保留该文件并使用本地恢复。", "state", ex); }
    }

    private void WriteState(State state)
    {
        var temporary = Path.Combine(root, ".state-" + Guid.NewGuid().ToString("N") + ".tmp");
        var path = Path.Combine(root, "state.json");
        EnsureNoLinks(path);
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject(); writer.WriteString("schema", StateSchema);
            if (state.Active == null) writer.WriteNull("active"); else writer.WriteString("active", state.Active);
            writer.WriteStartArray("history"); foreach (var version in state.History.Take(128)) writer.WriteStringValue(version); writer.WriteEndArray();
            writer.WriteStartObject("installed");
            foreach (var (version, envelope) in state.Installed)
            {
                writer.WriteStartObject(version); writer.WritePropertyName("feed"); writer.WriteRawValue(envelope); writer.WriteEndObject();
            }
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        var bytes = memory.ToArray();
        if (bytes.Length > 8 * 1024 * 1024) throw Error("版本状态超过大小限制，尚未切换活动版本。", "size");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        catch
        {
            // A platform may report an error after rename; never undo an already committed state.
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void EnsureIdentity(State state, ReleaseManifest manifest)
    {
        if (state.Installed.Count >= 128 && !state.Installed.ContainsKey(manifest.Version))
            throw Error("本地版本数量已达上限，请先备份并清理旧安装。", "size");
        if (state.Installed.TryGetValue(manifest.Version, out var envelope))
        {
            var existing = Authenticate(Utf8.GetBytes(envelope));
            if (existing.Identity != manifest.Identity || existing.Version != manifest.Version)
                throw Error("同版本号已有不同的签名内容。", "immutable");
        }
    }

    private static void Activate(State state, string version)
    {
        var prior = state.Active;
        state.History.RemoveAll(x => x == version || x == prior);
        if (prior != null && prior != version) state.History.Insert(0, prior);
        state.Active = version;
    }

    private async Task<IDisposable> LockAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        FileStream? file = null;
        try
        {
            EnsureNoLinks(root); Directory.CreateDirectory(root); EnsureNoLinks(root);
            var releases = Path.Combine(root, "releases"); EnsureNoLinks(releases); Directory.CreateDirectory(releases);
            var path = Path.Combine(root, ".update.lock"); EnsureNoLinks(path);
            file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (OperatingSystem.IsMacOS()) throw Error("此平台不支持更新锁。", "platform");
            file.Lock(0, 1);
            return new HeldLock(file, gate);
        }
        catch (Exception ex)
        {
            file?.Dispose(); gate.Release();
            if (ex is IOException) throw Error("另一个程序正在更新，或更新目录不可用。", "busy", ex);
            throw;
        }
    }

    private sealed class HeldLock(FileStream file, SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() { try { file.Dispose(); } finally { gate.Release(); } }
    }

    private string ReleasePath(string version)
    {
        ValidateVersion(version);
        var path = Path.Combine(root, "releases", version); EnsureNoLinks(path); return path;
    }

    private static void EnsureNoLinks(string path)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw Error("更新路径包含符号链接或重解析点。", "path");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path != path.Normalize(NormalizationForm.FormC) ||
            path.Contains('\\') || path.Any(c => c < 32 || c == 127 || "<>:\"|?*".Contains(c)))
            throw Error("更新包含不安全的 Windows 文件路径。", "path");
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' '))
                throw Error("更新包含不安全的文件路径。", "path");
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
                Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$", RegexOptions.CultureInvariant))
                throw Error("更新路径使用了 Windows 保留名称。", "path");
        }
    }

    private static Uri ValidateUrl(string value)
    {
        if (value.Length > 4000 || value.Any(c => c <= 32 || c == 127 || c == '\\') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || uri.Port != 443)
            throw Error("更新地址必须是无凭据的 HTTPS 地址。", "network");
        return uri;
    }

    private static void ValidateVersion(string version)
    {
        if (version == null || !VersionPattern.IsMatch(version)) throw Error("更新版本号无效。", "version");
    }
    private static int CompareVersions(string left, string right) { ValidateVersion(left); ValidateVersion(right); return Version.Parse(left).CompareTo(Version.Parse(right)); }
    private static bool HashEquals(byte[] bytes, string expected) => Convert.ToHexStringLower(SHA256.HashData(bytes)) == expected;
    private static string Hash(JsonElement element, string name) { var hash = String(element, name); if (!HashPattern.IsMatch(hash)) throw Error("更新 SHA256 格式无效。", "manifest"); return hash; }
    private static long Size(JsonElement element, string name, long maximum) { if (!element.GetProperty(name).TryGetInt64(out var value) || value < 0 || value > maximum) throw Error("更新大小超过限制。", "size"); return value; }
    private static string String(JsonElement element, string name) { var value = element.GetProperty(name); return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Error("更新字段类型无效。", "manifest"); }
    private static byte[] Base64(string text) { var bytes = Convert.FromBase64String(text); if (Convert.ToBase64String(bytes) != text) throw Error("更新 Base64 格式无效。", "signature"); return bytes; }
    private static JsonDocument ParseJson(string text)
    {
        var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        try { Unique(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw Error("更新 JSON 包含重复字段。", "manifest"); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
    private static void Keys(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != names.Length ||
            names.Any(name => !element.TryGetProperty(name, out _))) throw Error("更新 JSON 字段不符合格式。", "manifest");
    }
    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) { EnsureNoLinks(path); Directory.Delete(path, true); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UpdateException) { }
    }
    private static UpdateException Error(string message, string code, Exception? inner = null) => new(message, code, inner);
}
