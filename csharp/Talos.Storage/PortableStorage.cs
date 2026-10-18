using System.Collections.Concurrent;

namespace Talos.Storage;

public sealed record PortableStorageResult(string DataRoot, bool Migrated, string? Warning = null);

public sealed class PortableStorageException : IOException
{
    public string Code { get; }
    public PortableStorageException(string message, string code, Exception? inner = null) : base(message, inner) => Code = code;
}

/// <summary>Prepares EXE-adjacent data without network, assembly loading, or an AppData fallback.</summary>
public static class PortableStorage
{
    public const long MaximumFileBytes = 256L * 1024 * 1024;
    public const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumFiles = 10000;
    public const int MaximumDirectories = 2000;
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> StableFiles = new(StringComparer.OrdinalIgnoreCase)
        { "state.json", "vision-calibration.json" };
    private static readonly HashSet<string> StableDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "releases", "logs" };

    public static PortableStorageResult Prepare(string appDirectory, string? legacyDataDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        string app = Path.GetFullPath(appDirectory);
        string target = Path.Combine(app, "data");
        string? source = string.IsNullOrWhiteSpace(legacyDataDirectory) ? null : Path.GetFullPath(legacyDataDirectory);
        if (source != null && (Contains(source, target) || Contains(target, source)))
            throw Error("旧数据目录与便携数据目录重叠，已停止迁移；不会覆盖原数据。", "overlap");
        object synchronization = Gates.GetOrAdd(app, _ => new object());
        if (!Monitor.TryEnter(synchronization))
            throw Error("便携数据目录正由另一个程序准备，请稍后重试。", "busy");
        try
        {
            string? stage = null;
            bool removedEmptyTarget = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoLinks(app);
                if (source != null) EnsureNoLinks(source);
                Directory.CreateDirectory(app);
                EnsureNoLinks(app);
                using var destinationLock = AcquireLock(Path.Combine(app, ".storage.lock"), "便携数据目录正由另一个程序准备，请关闭其他实例后重试。");
                EnsureNoLinks(target);
                if (File.Exists(target)) throw Error("程序目录内的 data 是文件，无法建立便携数据目录。", "destination");
                if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                {
                    RejectTreeLinks(target, cancellationToken);
                    CheckWritable(target);
                    return new PortableStorageResult(target, false);
                }
                // Check directory permissions before inspecting or copying any legacy files.
                CheckWritable(app);
                if (source == null || !Directory.Exists(source))
                {
                    if (source != null) EnsureNoLinks(source);
                    Directory.CreateDirectory(target);
                    CheckWritable(target);
                    return new PortableStorageResult(target, false);
                }
                EnsureNoLinks(source);
                using var sourceLock = AcquireLock(Path.Combine(source, ".update.lock"), "旧数据正被其他程序使用，请退出旧程序后重试迁移。");
                var plan = Plan(source, cancellationToken);
                if (plan.Files.Count == 0)
                {
                    Directory.CreateDirectory(target);
                    CheckWritable(target);
                    return new PortableStorageResult(target, false, "旧目录没有可迁移的稳定数据；原目录已保留。");
                }
                stage = Path.Combine(app, ".data-migration-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                EnsureNoLinks(stage);
                foreach (var directory in plan.Directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.Combine(stage, directory));
                }
                foreach (var file in plan.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string original = Path.Combine(source, file.Relative);
                    string destination = Path.Combine(stage, file.Relative);
                    EnsureNoLinks(original);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    EnsureNoLinks(destination);
                    using var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length != file.Length) throw Error("旧数据在迁移中发生变化，请退出旧程序后重试。", "changed");
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    byte[] bytes = new byte[65536];
                    long copied = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int count = input.Read(bytes);
                        if (count == 0) break;
                        copied += count;
                        if (copied > file.Length) throw Error("旧数据在迁移中增长，已停止迁移。", "changed");
                        output.Write(bytes, 0, count);
                    }
                    if (copied != file.Length || input.Length != file.Length)
                        throw Error("旧数据在迁移中发生变化，已停止迁移。", "changed");
                    output.Flush(true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                EnsureNoLinks(target);
                // Never merge. If another writer put any data here, its files stay untouched.
                if (Directory.Exists(target))
                {
                    if (Directory.EnumerateFileSystemEntries(target).Any())
                        throw Error("便携数据目录在迁移中出现了数据，已停止迁移且未覆盖。", "destination");
                    Directory.Delete(target, false);
                    removedEmptyTarget = true;
                }
                Directory.Move(stage, target); // Same-volume atomic publication of the complete copied tree.
                stage = null;
                return new PortableStorageResult(target, true, plan.Skipped
                    ? "旧数据已复制；锁、临时文件和非迁移范围的文件未复制，原目录全部保留。" : null);
            }
            catch (OperationCanceledException) { throw; }
            catch (PortableStorageException) { throw; }
            catch (UnauthorizedAccessException ex)
            { throw Error("无法写入程序目录内的 data。请将整个程序文件夹移到可写位置后重试；不会改用 AppData。", "permission", ex); }
            catch (IOException ex)
            { throw Error("无法完整准备程序目录内的 data；原数据已保留，请检查权限、磁盘空间及其他程序占用后重试。", "io", ex); }
            finally
            {
                if (stage != null) DeleteStage(stage);
                if (removedEmptyTarget && !Directory.Exists(target) && !File.Exists(target))
                {
                    try { Directory.CreateDirectory(target); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        finally { Monitor.Exit(synchronization); }
    }

    private sealed record PlannedFile(string Relative, long Length);
    private sealed record CopyPlan(List<PlannedFile> Files, List<string> Directories, bool Skipped);

    private static CopyPlan Plan(string source, CancellationToken token)
    {
        var files = new List<PlannedFile>();
        var directories = new List<string>();
        bool skipped = false;
        long total = 0;
        var pending = new Stack<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            token.ThrowIfCancellationRequested();
            EnsureNoLinks(entry);
            string name = Path.GetFileName(entry);
            bool directory = Directory.Exists(entry);
            if (IsTemporary(name)) { skipped = true; continue; }
            if (directory && StableDirectories.Contains(name)) { directories.Add(name); pending.Push(entry); }
            else if (!directory && StableFiles.Contains(name)) AddFile(entry);
            else skipped = true;
        }
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                EnsureNoLinks(entry);
                string name = Path.GetFileName(entry);
                if (IsTemporary(name)) { skipped = true; continue; }
                if (Directory.Exists(entry))
                {
                    if (directories.Count >= MaximumDirectories) throw Error("旧数据目录数量超过迁移限制。", "size");
                    directories.Add(Path.GetRelativePath(source, entry)); pending.Push(entry);
                }
                else AddFile(entry);
            }
        }
        return new CopyPlan(files, directories, skipped);

        void AddFile(string filename)
        {
            RejectKeyName(filename);
            long size = new FileInfo(filename).Length;
            if (size > MaximumFileBytes || total + size > MaximumTotalBytes || files.Count >= MaximumFiles)
                throw Error("旧数据超过迁移大小限制，请保留原件并手动检查。", "size");
            files.Add(new PlannedFile(Path.GetRelativePath(source, filename), size));
            total += size;
        }
    }

    private static bool IsTemporary(string name) => name.Equals(".update.lock", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".storage.lock", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(".staging-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(".repair-backup-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(".data-migration-", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);

    private static void RejectKeyName(string filename)
    {
        string name = Path.GetFileName(filename);
        string extension = Path.GetExtension(name);
        if (new[] { ".pem", ".key", ".pfx", ".p12" }.Contains(extension, StringComparer.OrdinalIgnoreCase) ||
            name.Contains("private-key", StringComparison.OrdinalIgnoreCase) || name.Contains("private_key", StringComparison.OrdinalIgnoreCase))
            throw Error("迁移范围内出现密钥类文件，已停止且未读取该文件；请检查旧目录。", "key-file");
    }

    private static FileStream AcquireLock(string filename, string message)
    {
        EnsureNoLinks(filename);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (OperatingSystem.IsMacOS()) throw Error("此平台不支持便携迁移锁。", "platform");
            stream.Lock(0, 1);
            return stream;
        }
        catch (IOException ex) { stream?.Dispose(); throw Error(message, "busy", ex); }
        catch { stream?.Dispose(); throw; }
    }

    private static void CheckWritable(string directory)
    {
        string probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.WriteByte(0); file.Flush(true);
        }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    private static void RejectTreeLinks(string root, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(root);
        int count = 0;
        while (pending.Count > 0)
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                token.ThrowIfCancellationRequested();
                if (++count > MaximumFiles + MaximumDirectories) throw Error("现有便携数据项目过多，已停止自动准备。", "size");
                EnsureNoLinks(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
    }

    private static bool Contains(string parent, string child)
    {
        parent = Path.TrimEndingDirectorySeparator(parent);
        child = Path.TrimEndingDirectorySeparator(child);
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            child.StartsWith(parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw Error("数据目录包含符号链接或重解析点，已停止迁移；原数据未删除。", "path");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void DeleteStage(string directory)
    {
        try { EnsureNoLinks(directory); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static PortableStorageException Error(string message, string code, Exception? inner = null) => new(message, code, inner);
}
