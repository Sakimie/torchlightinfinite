using System.Security.Cryptography;
using System.Text;
using Talos.Storage;

namespace Talos.Tests;

/// <summary>Real filesystem tests with inert payloads and temporary QA publisher keys.</summary>
internal static class StorageChecks
{
    public static void Register(Checks suite)
    {
        suite.Add("storage.data path follows the application directory independently of CWD", () =>
        {
            using var layout = new Layout(); var previous = Environment.CurrentDirectory;
            var elsewhere = Path.Combine(layout.Root, "unrelated-working-directory"); Directory.CreateDirectory(elsewhere);
            try
            {
                Environment.CurrentDirectory = elsewhere;
                var first = PortableStorage.Prepare(layout.App, null);
                Check.Equal(Path.Combine(layout.App, "data"), first.DataRoot); Check.False(first.Migrated);
                Environment.CurrentDirectory = layout.Root;
                var second = PortableStorage.Prepare(layout.App, null);
                Check.Equal(first.DataRoot, second.DataRoot); Check.False(second.Migrated);
                Check.False(Directory.Exists(Path.Combine(elsewhere, "data")), "Storage fell back to CWD");
            }
            finally { Environment.CurrentDirectory = previous; }
        });
        suite.AddAsync("storage.full signed legacy state releases calibration and logs copy without changing source", async () =>
        {
            using var layout = new Layout(); await layout.SeedLegacyAsync();
            Directory.CreateDirectory(Path.Combine(layout.Legacy, "logs")); File.WriteAllText(Path.Combine(layout.Legacy, "logs", "history.txt"), "legacy diagnostic");
            File.WriteAllText(Path.Combine(layout.Legacy, "ignored-user-file.txt"), "outside migration whitelist");
            var original = Snapshot(layout.Legacy); var result = PortableStorage.Prepare(layout.App, layout.Legacy);
            Check.True(result.Migrated); Check.Equal(layout.Data, result.DataRoot);
            EqualFile(layout.Legacy, layout.Data, "state.json"); EqualFile(layout.Legacy, layout.Data, "vision-calibration.json"); EqualFile(layout.Legacy, layout.Data, "logs/history.txt");
            foreach (var name in original.Keys.Where(name => name.StartsWith("releases/", StringComparison.Ordinal))) EqualFile(layout.Legacy, layout.Data, name);
            Check.False(File.Exists(Path.Combine(layout.Data, "ignored-user-file.txt")));
            AssertSnapshot(original, layout.Legacy); layout.Updates.Transport.DenyAll = true;
            using var destination = layout.Updates.Service(layout.Data); Check.True((await destination.StatusAsync()).ActiveValid);
            Check.Equal("0.4.1", (await destination.StatusAsync()).ActiveVersion); await destination.GetActiveDirectoryAsync();
            Check.Equal(0, layout.Updates.Transport.Requests.Count);
        });
        suite.AddAsync("storage.moving the complete application keeps signed versions usable offline and rollback independent", async () =>
        {
            using var layout = new Layout(); await layout.SeedLegacyAsync(); var original = Snapshot(layout.Legacy);
            PortableStorage.Prepare(layout.App, layout.Legacy);
            var moved = Path.Combine(layout.Root, "moved-application"); Directory.Move(layout.App, moved);
            var result = PortableStorage.Prepare(moved, layout.Legacy); Check.False(result.Migrated);
            Check.Equal(Path.Combine(moved, "data"), result.DataRoot); Check.False(Directory.Exists(layout.App));
            layout.Updates.Transport.DenyAll = true; using var service = layout.Updates.Service(result.DataRoot);
            Check.True((await service.StatusAsync()).ActiveValid); Check.Equal("0.4.1", (await service.StatusAsync()).ActiveVersion);
            Check.True((await service.GetActiveDirectoryAsync()).StartsWith(result.DataRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            var rollback = await service.RollbackAsync("0.4.0"); Check.True(rollback.ActiveValid); Check.Equal("0.4.0", rollback.ActiveVersion);
            await service.GetActiveDirectoryAsync(); AssertSnapshot(original, layout.Legacy); Check.Equal(0, layout.Updates.Transport.Requests.Count);
        });
        suite.AddAsync("storage.any existing portable user data prevents merging or overwriting legacy", async () =>
        {
            using var layout = new Layout(); await layout.SeedLegacyAsync(); Directory.CreateDirectory(layout.Data);
            var file = Path.Combine(layout.Data, "vision-calibration.json"); File.WriteAllText(file, "{\"owner\":\"portable-user\"}");
            var before = Snapshot(layout.Data); var source = Snapshot(layout.Legacy);
            var result = PortableStorage.Prepare(layout.App, layout.Legacy); Check.False(result.Migrated);
            AssertSnapshot(before, layout.Data); AssertSnapshot(source, layout.Legacy);
            Check.False(File.Exists(Path.Combine(layout.Data, "state.json")));
        });
        suite.AddAsync("storage.initialized portable signed state is preserved instead of legacy merge", async () =>
        {
            using var layout = new Layout(); await layout.SeedLegacyAsync();
            using (var existing = layout.Updates.Service(layout.Data))
            {
                var release = layout.Updates.Release("0.4.2"); await existing.InstallBootstrapAsync(release.Feed, release.Package);
            }
            File.WriteAllText(Path.Combine(layout.Data, "vision-calibration.json"), "{\"owner\":\"new\"}"); var before = Snapshot(layout.Data);
            var result = PortableStorage.Prepare(layout.App, layout.Legacy); Check.False(result.Migrated); AssertSnapshot(before, layout.Data);
            using var service = layout.Updates.Service(layout.Data); Check.Equal("0.4.2", (await service.StatusAsync()).ActiveVersion); Check.True((await service.StatusAsync()).ActiveValid);
        });
        suite.Add("storage.missing legacy and an empty data directory initialize locally", () =>
        {
            using var layout = new Layout(); Directory.CreateDirectory(layout.Data);
            var result = PortableStorage.Prepare(layout.App, Path.Combine(layout.Root, "no-old-installation"));
            Check.False(result.Migrated); Check.Equal(layout.Data, result.DataRoot); Check.True(Directory.Exists(result.DataRoot));
        });
        suite.AddAsync("storage.cancelled migration preserves source and leaves no partially committed data", async () =>
        {
            using var layout = new Layout(); await layout.SeedLegacyAsync(); var original = Snapshot(layout.Legacy);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            Check.Throws<OperationCanceledException>(() => PortableStorage.Prepare(layout.App, layout.Legacy, cancel.Token));
            Check.False(Directory.Exists(layout.Data)); AssertNoStaging(layout.App); AssertSnapshot(original, layout.Legacy);
            Check.True(PortableStorage.Prepare(layout.App, layout.Legacy).Migrated, "Cancelled migration cannot be retried");
        });
        suite.AddAsync("storage.cancellation during a real staged copy removes partial output", async () =>
        {
            using var layout = new Layout(); await layout.SeedLegacyAsync(); Directory.CreateDirectory(Path.Combine(layout.Legacy, "logs"));
            var large = Path.Combine(layout.Legacy, "logs", "cancel-probe.bin");
            using (var sparse = new FileStream(large, FileMode.CreateNew, FileAccess.Write, FileShare.None)) sparse.SetLength(128L * 1024 * 1024);
            var original = Snapshot(layout.Legacy); using var cancel = new CancellationTokenSource(); var copiedFileObserved = 0;
            using var stopObserver = new CancellationTokenSource(); using var observerStarted = new ManualResetEventSlim();
            // Poll from an already-running dedicated observer. Recursive filesystem
            // watchers can miss a new file created before their new-subdirectory watch.
            var observer = new Thread(() =>
            {
                observerStarted.Set();
                while (!stopObserver.IsCancellationRequested)
                {
                    foreach (var stage in Directory.EnumerateDirectories(layout.App, ".data-migration-*"))
                        if (File.Exists(Path.Combine(stage, "logs", "cancel-probe.bin")))
                        { Interlocked.Exchange(ref copiedFileObserved, 1); cancel.Cancel(); return; }
                    Thread.Yield();
                }
            }) { IsBackground = true };
            observer.Start(); observerStarted.Wait();
            var copy = Task.Factory.StartNew(
                () => PortableStorage.Prepare(layout.App, layout.Legacy, cancel.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try { await Check.ThrowsAsync<OperationCanceledException>(() => copy.WaitAsync(TimeSpan.FromSeconds(10))); }
            finally
            {
                cancel.Cancel(); stopObserver.Cancel(); Check.True(observer.Join(TimeSpan.FromSeconds(1)), "Cancellation observer did not stop");
                try { await copy; } catch (Exception) { /* The assertion above retains the actual outcome. */ }
            }
            Check.Equal(1, Volatile.Read(ref copiedFileObserved), "Copy interruption did not reach a staged output file");
            Check.False(Directory.Exists(layout.Data)); AssertNoStaging(layout.App); AssertSnapshot(original, layout.Legacy);
        });
        suite.AddAsync("storage.transient locks stages backups and incomplete files are not migrated", async () =>
        {
            using var layout = new Layout(); await layout.SeedLegacyAsync();
            var transient = new[] { ".staging-interrupted/file.bin", ".repair-backup-old/file.bin", ".data-migration-old/file.bin", ".state-incomplete.tmp", "logs/incomplete.tmp", "logs/incomplete.partial" };
            foreach (var name in transient)
            { var path = Path.Combine(layout.Legacy, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "incomplete QA bytes"); }
            var original = Snapshot(layout.Legacy); var result = PortableStorage.Prepare(layout.App, layout.Legacy); Check.True(result.Migrated);
            foreach (var name in transient) Check.False(File.Exists(Path.Combine(layout.Data, name)), "Transient copied: " + name);
            Check.False(File.Exists(Path.Combine(layout.Data, ".update.lock"))); AssertSnapshot(original, layout.Legacy);
            using var destination = layout.Updates.Service(layout.Data); Check.True((await destination.StatusAsync()).ActiveValid);
        });
        suite.Add("storage.oversized or key-like copied files fail during planning without reading contents", () =>
        {
            using var layout = new Layout(); Directory.CreateDirectory(Path.Combine(layout.Legacy, "logs"));
            var tooLarge = Path.Combine(layout.Legacy, "logs", "large.dat");
            using (var sparse = new FileStream(tooLarge, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                sparse.SetLength(PortableStorage.MaximumFileBytes + 1);
                Reject(() => PortableStorage.Prepare(layout.App, layout.Legacy), "size");
                Check.Equal(PortableStorage.MaximumFileBytes + 1, sparse.Length);
            }
            File.Delete(tooLarge); var keyLike = Path.Combine(layout.Legacy, "logs", "qa-placeholder.pem"); File.WriteAllText(keyLike, "QA placeholder, no key material");
            using (var blocked = new FileStream(keyLike, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Reject(() => PortableStorage.Prepare(layout.App, layout.Legacy), "key-file");
            Check.False(Directory.Exists(layout.Data)); AssertNoStaging(layout.App);
        });
        suite.AddAsync("storage.source copy error cleans stage and does not commit partial data", async () =>
        {
            using var layout = new Layout(); await layout.SeedLegacyAsync(); var original = Snapshot(layout.Legacy);
            using (var held = new FileStream(Path.Combine(layout.Legacy, "vision-calibration.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Reject(() => PortableStorage.Prepare(layout.App, layout.Legacy));
            Check.False(Directory.Exists(layout.Data)); AssertNoStaging(layout.App); AssertSnapshot(original, layout.Legacy);
            Check.True(PortableStorage.Prepare(layout.App, layout.Legacy).Migrated);
        });
        suite.AddAsync("storage.portable and legacy lock contention cannot copy or change active data", async () =>
        {
            foreach (var portableLock in new[] { true, false })
            {
                using var layout = new Layout(); await layout.SeedLegacyAsync(); var original = Snapshot(layout.Legacy);
                var path = portableLock ? Path.Combine(layout.App, ".storage.lock") : Path.Combine(layout.Legacy, ".update.lock");
                using (var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                {
                    var task = Task.Run(() => PortableStorage.Prepare(layout.App, layout.Legacy, cancel.Token));
                    var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
                    Check.True(ReferenceEquals(task, completed), "Contended storage lock ignored cancellation");
                    try { await task; throw new InvalidOperationException("Contended lock allowed migration"); }
                    catch (PortableStorageException error) { Check.Equal("busy", error.Code); }
                    catch (OperationCanceledException) { }
                }
                Check.False(Directory.Exists(layout.Data)); AssertNoStaging(layout.App); AssertSnapshot(original, layout.Legacy);
                Check.True(PortableStorage.Prepare(layout.App, layout.Legacy).Migrated);
            }
        });
        suite.Add("storage.nested source and destination are rejected without recursive copying", () =>
        {
            using var layout = new Layout();
            File.WriteAllText(Path.Combine(layout.App, "vision-calibration.json"), "{}");
            Reject(() => PortableStorage.Prepare(layout.App, layout.App)); Check.False(Directory.Exists(layout.Data));
            Directory.CreateDirectory(layout.Legacy); var nestedApp = Path.Combine(layout.Legacy, "application"); Directory.CreateDirectory(nestedApp);
            File.WriteAllText(Path.Combine(layout.Legacy, "vision-calibration.json"), "{}");
            Reject(() => PortableStorage.Prepare(nestedApp, layout.Legacy)); Check.False(Directory.Exists(Path.Combine(nestedApp, "data")));
        });
        suite.Add("storage.symbolic source root copied file and release directory are refused", () =>
        {
            foreach (var mode in new[] { "root", "ancestor", "file", "directory" })
            {
                using var layout = new Layout(); var outside = Path.Combine(layout.Root, "outside"); Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "vision-calibration.json"), "untouched"); var original = Snapshot(outside); string source;
                if (mode == "root")
                { source = Path.Combine(layout.Root, "linked-legacy"); Directory.CreateSymbolicLink(source, outside); }
                else if (mode == "ancestor")
                {
                    Directory.CreateDirectory(Path.Combine(outside, "legacy"));
                    var alias = Path.Combine(layout.Root, "ancestor-alias"); Directory.CreateSymbolicLink(alias, outside); source = Path.Combine(alias, "legacy");
                }
                else
                {
                    source = layout.Legacy; Directory.CreateDirectory(source);
                    if (mode == "file") File.CreateSymbolicLink(Path.Combine(source, "vision-calibration.json"), Path.Combine(outside, "vision-calibration.json"));
                    else Directory.CreateSymbolicLink(Path.Combine(source, "releases"), outside);
                }
                Reject(() => PortableStorage.Prepare(layout.App, source)); Check.False(Directory.Exists(layout.Data)); AssertSnapshot(original, outside); AssertNoStaging(layout.App);
            }
        });
        suite.Add("storage.symbolic application directory and data target are refused", () =>
        {
            foreach (var appLink in new[] { true, false })
            {
                using var layout = new Layout(); var outside = Path.Combine(layout.Root, "outside"); Directory.CreateDirectory(outside);
                var app = layout.App;
                if (appLink) { app = Path.Combine(layout.Root, "linked-app"); Directory.CreateSymbolicLink(app, outside); }
                else Directory.CreateSymbolicLink(layout.Data, outside);
                Reject(() => PortableStorage.Prepare(app, null)); Check.Equal(0, Directory.EnumerateFileSystemEntries(outside).Count());
            }
        });
        suite.Add("storage.unwritable application directory fails explicitly without external fallback on Linux", () =>
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Permission fixture is designed for Linux console validation");
            using var layout = new Layout(); var mode = File.GetUnixFileMode(layout.App);
            try
            {
                File.SetUnixFileMode(layout.App, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                Reject(() => PortableStorage.Prepare(layout.App, null)); Check.False(Directory.Exists(layout.Data));
            }
            finally { File.SetUnixFileMode(layout.App, mode); }
        });
    }

    private static void Reject(Action action, string? code = null)
    {
        try { action(); } catch (PortableStorageException error) { if (code is not null) Check.Equal(code, error.Code); return; }
        throw new InvalidOperationException("Unsafe/failed migration was accepted");
    }
    private static SortedDictionary<string, string> Snapshot(string root) => new(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))), StringComparer.Ordinal);
    private static void AssertSnapshot(SortedDictionary<string, string> expected, string root) => Check.True(expected.SequenceEqual(Snapshot(root)), "Source or existing portable data changed");
    private static void EqualFile(string first, string second, string relative) => Check.True(File.ReadAllBytes(Path.Combine(first, relative)).SequenceEqual(File.ReadAllBytes(Path.Combine(second, relative))), "Copied bytes differ: " + relative);
    private static void AssertNoStaging(string app) => Check.Equal(0, Directory.EnumerateDirectories(app).Count(), "Incomplete data/staging directory survived failure");

    private sealed class Layout : IDisposable
    {
        public UpdateFixture Updates { get; } = new();
        public string Root => Updates.Root;
        public string App => Path.Combine(Root, "application");
        public string Legacy => Path.Combine(Root, "legacy-preview");
        public string Data => Path.Combine(App, "data");
        public Layout() => Directory.CreateDirectory(App);
        public async Task SeedLegacyAsync()
        {
            Directory.CreateDirectory(Legacy); using var service = Updates.Service(Legacy);
            foreach (var version in new[] { "0.4.0", "0.4.1" })
            { var release = Updates.Release(version); await service.InstallBootstrapAsync(release.Feed, release.Package); }
            File.WriteAllText(Path.Combine(Legacy, "vision-calibration.json"), "{\"layoutConfirmed\":true,\"marker\":\"legacy-only\",\"referenceWidth\":1920,\"referenceHeight\":1080}", Encoding.UTF8);
        }
        public void Dispose() => Updates.Dispose();
    }
}
