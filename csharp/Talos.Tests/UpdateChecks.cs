using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Talos.Updates;

namespace Talos.Tests;

internal static class UpdateChecks
{
    public static void Register(Checks suite)
    {
        suite.AddAsync("updates.offline bootstrap verifies and hashes every payload file", async () =>
        {
            using var fixture = new UpdateFixture();
            fixture.Transport.DenyAll = true;
            var release = fixture.Release("0.4.0");
            var service = fixture.Service();
            var status = await service.InstallBootstrapAsync(release.Feed, release.Package);
            Check.Equal("0.4.0", status.ActiveVersion); Check.True(status.ActiveValid);
            var active = await service.GetActiveDirectoryAsync();
            foreach (var file in release.Files)
                Check.True(File.ReadAllBytes(Path.Combine(active, file.Key)).SequenceEqual(file.Value), file.Key);
            Check.Equal(0, fixture.Transport.Requests.Count);
        });
        suite.AddAsync("updates.check and install authenticate exact payload bytes", async () =>
        {
            using var fixture = new UpdateFixture();
            var release = fixture.Release("0.4.1"); fixture.Serve(release);
            var service = fixture.Service();
            var manifest = await service.CheckAsync();
            Check.Equal("0.4.1", manifest.Version);
            var status = await service.InstallAsync(manifest);
            Check.True(status.ActiveValid); Check.Equal("0.4.1", status.ActiveVersion);
            Check.True(fixture.Transport.Requests.Contains(UpdateFixture.FeedUrl));
            Check.True(fixture.Transport.Requests.Contains(UpdateFixture.PackageUrl("0.4.1")));
        });
        suite.AddAsync("updates.reject signature tampering and another publisher key", async () =>
        {
            using var fixture = new UpdateFixture(); using var stranger = new UpdateFixture();
            var release = fixture.Release("0.4.0"); var service = fixture.Service();
            var envelope = JsonNode.Parse(release.Feed)!.AsObject();
            var payload = Convert.FromBase64String(envelope["payload"]!.GetValue<string>());
            payload[^2] ^= 1; envelope["payload"] = Convert.ToBase64String(payload);
            await Check.ThrowsAsync<UpdateException>(() => service.InstallBootstrapAsync(Encoding.UTF8.GetBytes(envelope.ToJsonString()), release.Package));
            var strangerRelease = stranger.Release("0.4.1");
            await Check.ThrowsAsync<UpdateException>(() => service.InstallBootstrapAsync(strangerRelease.Feed, strangerRelease.Package));
            Check.False((await service.StatusAsync()).ActiveValid);
        });
        suite.AddAsync("updates.install reauthenticates a manifest from another service", async () =>
        {
            using var fixture = new UpdateFixture(); using var stranger = new UpdateFixture();
            var release = stranger.Release("0.4.0"); stranger.Serve(release);
            var forgedForService = await stranger.Service().CheckAsync();
            await Check.ThrowsAsync<UpdateException>(() => fixture.Service().InstallAsync(forgedForService));
            Check.Equal(0, fixture.Transport.Requests.Count);
        });
        suite.AddAsync("updates.corrupt ZIP bytes cannot replace existing active", async () =>
        {
            using var fixture = new UpdateFixture(); var service = fixture.Service();
            var first = fixture.Release("0.4.0"); await service.InstallBootstrapAsync(first.Feed, first.Package);
            var next = fixture.Release("0.4.1"); var damaged = next.Package.ToArray(); damaged[0] ^= 1;
            await Check.ThrowsAsync<UpdateException>(() => service.InstallBootstrapAsync(next.Feed, damaged));
            Check.Equal("0.4.0", (await service.StatusAsync()).ActiveVersion);
            Check.True((await service.StatusAsync()).ActiveValid);
        });
        suite.AddAsync("updates.Windows path aliases and traversal are rejected cross platform", async () =>
        {
            foreach (var path in new[] { "../escape", "/absolute", "C:/drive", "C:drive", "a\\b", "ads:stream", "tail. ", "dir./file", "CON", "con.txt", "CON .txt", "PRN   .log", "AUX .bin", "NUL .json", "COM1 .txt", "LPT9 .ext", "CONIN$ .txt", "CONOUT$  .log", "CONIN$", "CONOUT$.log", "LPT9.ext", "COM¹.txt" })
            {
                using var fixture = new UpdateFixture();
                var release = fixture.Release("0.4.0", FilesWith(path));
                await Check.ThrowsAsync<UpdateException>(() => fixture.Service().InstallBootstrapAsync(release.Feed, release.Package), "Accepted unsafe path: " + path);
                Check.False((await fixture.Service().StatusAsync()).ActiveValid, "Accepted unsafe path: " + path);
            }
        });
        suite.AddAsync("updates.reject case-fold duplicates and file-directory collisions", async () =>
        {
            foreach (var pair in new[] { ("assets/Name", "ASSETS/name"), ("assets", "assets/name"), ("Assets", "assets/name") })
            {
                using var fixture = new UpdateFixture(); var files = FilesWith(pair.Item1); files[pair.Item2] = [8];
                var release = fixture.Release("0.4.0", files);
                await Check.ThrowsAsync<UpdateException>(() => fixture.Service().InstallBootstrapAsync(release.Feed, release.Package));
            }
        });
        suite.AddAsync("updates.manifest cannot replace shared host or runtime components", async () =>
        {
            foreach (var path in new[] { "Talos.Updates.dll", "nested/TALOS.UPDATES.DLL", "Talos.Host.dll", "nested/talos.host.runtimeconfig.json", "tool.EXE", "Runtime/file", "nested/_INTERNAL/file" })
            {
                using var fixture = new UpdateFixture(); var release = fixture.Release("0.4.0", FilesWith(path));
                await Check.ThrowsAsync<UpdateException>(() => fixture.Service().InstallBootstrapAsync(release.Feed, release.Package), "Accepted protected component: " + path);
            }
        });
        suite.AddAsync("updates.feed package file expanded-size and file-count limits reject signed excess", async () =>
        {
            using var fixture = new UpdateFixture(); var service = fixture.Service();
            await Check.ThrowsAsync<UpdateException>(() => service.InstallBootstrapAsync(new byte[UpdateService.MaximumFeedBytes + 1], []));
            var packageTooLarge = fixture.Release("0.4.0", editManifest: value => value["package"] = new { url = UpdateFixture.PackageUrl("0.4.0"), bytes = UpdateService.MaximumPackageBytes + 1, sha256 = new string('a', 64) });
            await Check.ThrowsAsync<UpdateException>(() => service.InstallBootstrapAsync(packageTooLarge.Feed, packageTooLarge.Package));
            foreach (var sizes in new[] { new[] { UpdateService.MaximumFileBytes + 1, 1L }, Enumerable.Repeat(UpdateService.MaximumFileBytes, 5).ToArray() })
            {
                var release = fixture.Release("0.4.0", editManifest: value => value["files"] = sizes.Select((size, index) => new { path = index == 0 ? "Talos.App.dll" : index == 1 ? "Talos.Core.dll" : $"assets/{index}", bytes = size, sha256 = new string('b', 64) }).ToArray());
                await Check.ThrowsAsync<UpdateException>(() => service.InstallBootstrapAsync(release.Feed, release.Package));
            }
            var files = Enumerable.Range(0, UpdateService.MaximumFiles + 1).ToDictionary(index => index == 0 ? "Talos.App.dll" : index == 1 ? "Talos.Core.dll" : $"assets/{index}", _ => new byte[] { 1 });
            var tooMany = fixture.Release("0.4.0", files);
            await Check.ThrowsAsync<UpdateException>(() => service.InstallBootstrapAsync(tooMany.Feed, tooMany.Package));
        });
        suite.AddAsync("updates.reject duplicate and unlisted ZIP members", async () =>
        {
            foreach (var name in new[] { "Talos.App.dll", "unlisted.txt" })
            {
                using var fixture = new UpdateFixture();
                var release = fixture.Release("0.4.0", editZip: zip => { using var output = zip.CreateEntry(name).Open(); output.WriteByte(1); });
                await Check.ThrowsAsync<UpdateException>(() => fixture.Service().InstallBootstrapAsync(release.Feed, release.Package));
            }
        });
        suite.AddAsync("updates.reject ZIP symlink mode even when its bytes are signed", async () =>
        {
            using var fixture = new UpdateFixture(); var files = FilesWith("assets/link");
            var release = fixture.Release("0.4.0", files, zip =>
            {
                var entry = zip.Entries.Single(item => item.FullName == "assets/link");
                entry.ExternalAttributes = unchecked((int)(0xa1ffu << 16));
            });
            await Check.ThrowsAsync<UpdateException>(() => fixture.Service().InstallBootstrapAsync(release.Feed, release.Package));
        });
        suite.AddAsync("updates.unsafe package URL and incompatible launcher fail before download", async () =>
        {
            foreach (var url in new[] { "http://updates.example.invalid/file.zip", "https://user:pass@updates.example.invalid/file.zip", "file:///tmp/test.zip" })
            {
                using var fixture = new UpdateFixture();
                var release = fixture.Release("0.4.0", editManifest: manifest => manifest["package"] = new { url, bytes = 1, sha256 = new string('a', 64) });
                await Check.ThrowsAsync<UpdateException>(() => fixture.Service().InstallBootstrapAsync(release.Feed, release.Package));
                Check.Equal(0, fixture.Transport.Requests.Count);
            }
            using var incompatible = new UpdateFixture();
            var incompatibleRelease = incompatible.Release("0.4.0", editManifest: manifest => manifest["launcherMin"] = "99.0.0");
            await Check.ThrowsAsync<UpdateException>(() => incompatible.Service().InstallBootstrapAsync(incompatibleRelease.Feed, incompatibleRelease.Package));
        });
        suite.AddAsync("updates.reject final HTTP redirect target", async () =>
        {
            using var fixture = new UpdateFixture(); var release = fixture.Release("0.4.0");
            fixture.Transport.Responses[UpdateFixture.FeedUrl] = () =>
            {
                var response = FakeTransport.Ok(release.Feed);
                response.RequestMessage = new(HttpMethod.Get, "http://updates.example.invalid/redirected");
                return response;
            };
            await Check.ThrowsAsync<UpdateException>(() => fixture.Service().CheckAsync());
        });
        suite.AddAsync("updates.cancelled install leaves old signed active intact", async () =>
        {
            using var fixture = new UpdateFixture(); var service = fixture.Service();
            var first = fixture.Release("0.4.0"); await service.InstallBootstrapAsync(first.Feed, first.Package);
            var second = fixture.Release("0.4.1"); fixture.Serve(second); var manifest = await service.CheckAsync();
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => service.InstallAsync(manifest, cancellationToken: cancel.Token));
            Check.Equal("0.4.0", (await service.StatusAsync()).ActiveVersion); Check.True((await service.StatusAsync()).ActiveValid);
        });
        suite.AddAsync("updates.status active directory and rollback use no network", async () =>
        {
            using var fixture = new UpdateFixture(); var service = fixture.Service();
            var first = fixture.Release("0.4.0"); await service.InstallBootstrapAsync(first.Feed, first.Package);
            var second = fixture.Release("0.4.1"); fixture.Serve(second); await service.InstallAsync(await service.CheckAsync());
            var requests = fixture.Transport.Requests.Count; fixture.Transport.DenyAll = true;
            Check.True((await service.StatusAsync()).ActiveValid); await service.GetActiveDirectoryAsync();
            var rolled = await service.RollbackAsync("0.4.0"); Check.Equal("0.4.0", rolled.ActiveVersion); Check.True(rolled.ActiveValid);
            await service.GetActiveDirectoryAsync(); Check.Equal(requests, fixture.Transport.Requests.Count);
        });
        suite.AddAsync("updates.bad active fails launch but can roll back to healthy signed version", async () =>
        {
            using var fixture = new UpdateFixture(); var service = fixture.Service();
            var first = fixture.Release("0.4.0"); await service.InstallBootstrapAsync(first.Feed, first.Package);
            var second = fixture.Release("0.4.1"); fixture.Serve(second); await service.InstallAsync(await service.CheckAsync());
            File.AppendAllText(Path.Combine(await service.GetActiveDirectoryAsync(), "Talos.App.dll"), "corruption");
            fixture.Transport.DenyAll = true;
            Check.False((await service.StatusAsync()).ActiveValid);
            await Check.ThrowsAsync<UpdateException>(() => service.GetActiveDirectoryAsync());
            var status = await service.RollbackAsync("0.4.0"); Check.True(status.ActiveValid); Check.Equal("0.4.0", status.ActiveVersion);
        });
        suite.AddAsync("updates.bad historical version cannot be selected for rollback", async () =>
        {
            using var fixture = new UpdateFixture(); var service = fixture.Service();
            var first = fixture.Release("0.4.0"); await service.InstallBootstrapAsync(first.Feed, first.Package);
            var firstDirectory = await service.GetActiveDirectoryAsync();
            var second = fixture.Release("0.4.1"); fixture.Serve(second); await service.InstallAsync(await service.CheckAsync());
            File.AppendAllText(Path.Combine(firstDirectory, "Talos.Core.dll"), "corruption");
            await Check.ThrowsAsync<UpdateException>(() => service.RollbackAsync("0.4.0"));
            Check.Equal("0.4.1", (await service.StatusAsync()).ActiveVersion); Check.True((await service.StatusAsync()).ActiveValid);
        });
        suite.AddAsync("updates.signed same-version conflict cannot overwrite immutable files", async () =>
        {
            using var fixture = new UpdateFixture(); var service = fixture.Service();
            var first = fixture.Release("0.4.0"); await service.InstallBootstrapAsync(first.Feed, first.Package);
            var changed = FilesWith("new-file"); changed["Talos.App.dll"] = Encoding.UTF8.GetBytes("changed same version");
            var conflicting = fixture.Release("0.4.0", changed); fixture.Serve(conflicting);
            await Check.ThrowsAsync<UpdateException>(async () => await service.InstallAsync(await service.CheckAsync()));
            Check.True((await service.StatusAsync()).ActiveValid);
            Check.True(File.ReadAllBytes(Path.Combine(await service.GetActiveDirectoryAsync(), "Talos.App.dll")).SequenceEqual(first.Files["Talos.App.dll"]));
        });
        suite.AddAsync("updates.signed compressed bomb is rejected before activation", async () =>
        {
            using var fixture = new UpdateFixture(); var files = FilesWith("assets/highly-compressed.bin");
            files["assets/highly-compressed.bin"] = new byte[2 * 1024 * 1024];
            var release = fixture.Release("0.4.0", files);
            await Check.ThrowsAsync<UpdateException>(() => fixture.Service().InstallBootstrapAsync(release.Feed, release.Package));
            Check.False((await fixture.Service().StatusAsync()).ActiveValid);
        });
        suite.AddAsync("updates.cancellation immediately before commit preserves exact active state", async () =>
        {
            using var fixture = new UpdateFixture(); var service = fixture.Service();
            var first = fixture.Release("0.4.0"); await service.InstallBootstrapAsync(first.Feed, first.Package);
            var stateFile = Path.Combine(fixture.Root, "state.json"); var before = File.ReadAllBytes(stateFile);
            var next = fixture.Release("0.4.1"); fixture.Serve(next); var manifest = await service.CheckAsync();
            using var cancel = new CancellationTokenSource(); var seenActivate = false;
            var progress = new SynchronousProgress(value => { if (value.Stage == "activate") { seenActivate = true; cancel.Cancel(); } });
            await Check.ThrowsAsync<OperationCanceledException>(() => service.InstallAsync(manifest, progress, cancel.Token));
            Check.True(seenActivate, "Did not exercise the pre-commit interruption point");
            Check.True(before.SequenceEqual(File.ReadAllBytes(stateFile)), "Cancelled update changed active state");
            Check.True((await service.StatusAsync()).ActiveValid); Check.Equal("0.4.0", (await service.StatusAsync()).ActiveVersion);
            await service.InstallAsync(manifest); Check.Equal("0.4.1", (await service.StatusAsync()).ActiveVersion);
        });
        suite.AddAsync("updates.unregistered partial release from an interrupted install can recover", async () =>
        {
            using var fixture = new UpdateFixture(); var service = fixture.Service();
            var first = fixture.Release("0.4.0"); await service.InstallBootstrapAsync(first.Feed, first.Package);
            var next = fixture.Release("0.4.1"); fixture.Serve(next);
            var orphan = Path.Combine(fixture.Root, "releases", next.Version); Directory.CreateDirectory(orphan);
            File.WriteAllBytes(Path.Combine(orphan, "Talos.App.dll"), next.Files["Talos.App.dll"]);
            Check.Equal("0.4.0", (await service.StatusAsync()).ActiveVersion);
            var status = await service.InstallAsync(await service.CheckAsync());
            Check.True(status.ActiveValid); Check.Equal("0.4.1", status.ActiveVersion);
            foreach (var file in next.Files) Check.True(File.ReadAllBytes(Path.Combine(await service.GetActiveDirectoryAsync(), file.Key)).SequenceEqual(file.Value));
        });
        suite.AddAsync("updates.symlink release parent cannot redirect writes outside data root", async () =>
        {
            using var fixture = new UpdateFixture(); var outside = fixture.Root + "-outside"; Directory.CreateDirectory(outside);
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "releases"), outside);
                var release = fixture.Release("0.4.0");
                await Check.ThrowsAsync<UpdateException>(() => fixture.Service().InstallBootstrapAsync(release.Feed, release.Package));
                Check.Equal(0, Directory.EnumerateFileSystemEntries(outside).Count());
            }
            finally { Directory.Delete(outside, true); }
        });
        suite.AddAsync("updates.tampered signed state cannot authorize assembly loading", async () =>
        {
            using var fixture = new UpdateFixture(); var service = fixture.Service(); var release = fixture.Release("0.4.0");
            await service.InstallBootstrapAsync(release.Feed, release.Package);
            var path = Path.Combine(fixture.Root, "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(path))!;
            var feed = state["installed"]!["0.4.0"]!["feed"]!;
            var payload = Convert.FromBase64String(feed["payload"]!.GetValue<string>()); payload[^2] ^= 1;
            feed["payload"] = Convert.ToBase64String(payload); File.WriteAllText(path, state.ToJsonString());
            await Check.ThrowsAsync<UpdateException>(() => service.GetActiveDirectoryAsync());
        });
    }
    private static Dictionary<string, byte[]> FilesWith(string path) => new()
    {
        ["Talos.App.dll"] = [1, 2, 3], ["Talos.Core.dll"] = [4, 5, 6], [path] = [7]
    };
    private sealed class SynchronousProgress(Action<UpdateProgress> action) : IProgress<UpdateProgress>
    { public void Report(UpdateProgress value) => action(value); }
}
