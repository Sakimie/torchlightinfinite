using System.Text;
using System.Text.Json;
using Talos.Core.Following;

namespace Talos.Tests;

internal static class FollowingChecks
{
    public static void Register(Checks suite)
    {
        suite.Add("following.matcher needs domain and action semantics including custom phrases", () =>
        {
            var matcher = new LogLineMatcher(Settings());
            Check.True(matcher.Match("典狱 吞噬完成") is not null);
            Check.True(matcher.Match("Tartarus devour") is not null);
            Check.True(matcher.Match("Tartarus reward shown") is not null);
            var start = matcher.Match("S16GamePlay Start"); Check.True(start is not null && start.IsRunStartHint);
            foreach (var noise in new[] { "player damage devour", "network event fired", "reward shown", "典狱 UI keepalive", "" })
                Check.True(matcher.Match(noise) is null, "Unrelated log noise matched: " + noise);
            Check.Throws<ArgumentException>(() => new LogLineMatcher(Settings() with { CustomKeywords = ["foo"] }));
            var custom = new LogLineMatcher(Settings() with { CustomKeywords = ["典狱 foo"] });
            Check.True(custom.Match("典狱 foo") is not null); Check.True(custom.Match("player foo") is null); Check.True(custom.Match("foo") is null);
        });
        suite.Add("following.coalescing waits for a quiet window and emits once", () =>
        {
            var settings = Settings(); var clock = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
            var coalescer = new LogTriggerCoalescer(settings);
            coalescer.Observe(new(LogTriggerKind.BoardChanged, false), clock);
            Check.True(coalescer.HasPending); Check.True(coalescer.Advance(clock.AddMilliseconds(999)).Trigger is null);
            coalescer.Observe(new(LogTriggerKind.BoardChanged, false), clock.AddMilliseconds(1000));
            Check.True(coalescer.Advance(clock.AddMilliseconds(2499)).Trigger is null);
            var settled = coalescer.Advance(clock.AddMilliseconds(2500)); Check.True(settled.Trigger is not null); Check.False(coalescer.HasPending);
            Check.True(coalescer.Advance(clock.AddHours(1)).Trigger is null, "One batch emitted more than once");
        });
        suite.Add("following.continuous log activity expires without a forced unstable trigger", () =>
        {
            var clock = DateTimeOffset.Parse("2026-10-17T00:00:00Z"); var coalescer = new LogTriggerCoalescer(Settings());
            for (var seconds = 0; seconds < 8; seconds++) coalescer.Observe(new(LogTriggerKind.BoardChanged, false), clock.AddSeconds(seconds));
            var expired = coalescer.Advance(clock.AddSeconds(8)); Check.True(expired.PendingExpired); Check.True(expired.Trigger is null); Check.False(coalescer.HasPending);
            Check.True(coalescer.Advance(clock.AddSeconds(20)).Trigger is null);
            coalescer.Observe(new(LogTriggerKind.RunStarted, true), clock.AddSeconds(21));
            var next = coalescer.Advance(clock.AddMilliseconds(22500)); Check.True(next.Trigger is not null && next.Trigger.IsRunStartHint);
            coalescer.Observe(new(LogTriggerKind.BoardChanged, false), clock.AddSeconds(30)); coalescer.Reset();
            Check.False(coalescer.HasPending); Check.True(coalescer.Advance(clock.AddSeconds(40)).Trigger is null);
        });
        suite.AddAsync("following.tail starts at EOF and shares the file with the writer", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllText(temp.Path, "S16GamePlay Start\n典狱 吞噬完成\n", new UTF8Encoding(false));
            await using var reader = new LogTailReader(temp.Path, Settings()); reader.Start();
            Check.Equal(0, (await reader.ReadAsync()).Lines.Count);
            using (var writer = new FileStream(temp.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            { writer.Write(Encoding.UTF8.GetBytes("Tartarus devour\n")); writer.Flush(true); var next = await reader.ReadAsync(); Check.True(next.Lines.SequenceEqual(new[] { "Tartarus devour" })); }
            Check.Equal(0, (await reader.ReadAsync()).Lines.Count);
        });
        suite.AddAsync("following.tail preserves split UTF8 characters and CRLF without premature half lines", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllBytes(temp.Path, []);
            await using var reader = new LogTailReader(temp.Path, Settings()); reader.Start();
            const string expected = "Tartarus devour 典狱"; var bytes = Encoding.UTF8.GetBytes(expected);
            Append(temp.Path, bytes[..^1]); Check.Equal(0, (await reader.ReadAsync()).Lines.Count);
            Append(temp.Path, [bytes[^1], (byte)'\r']); Check.Equal(0, (await reader.ReadAsync()).Lines.Count);
            Append(temp.Path, [(byte)'\n']); var result = await reader.ReadAsync();
            Check.True(result.Lines.SequenceEqual(new[] { expected })); Check.False(result.Lines[0].Contains('\ufffd'));
        });
        suite.AddAsync("following.pre-connection partial log line is not replayed when it later completes", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllText(temp.Path, "Tartarus devour", new UTF8Encoding(false));
            await using var reader = new LogTailReader(temp.Path, Settings()); reader.Start();
            Append(temp.Path, Encoding.UTF8.GetBytes(" completed\nTartarus reward shown\n")); var read = await reader.ReadAsync();
            Check.True(read.Lines.SequenceEqual(new[] { "Tartarus reward shown" }));
        });
        suite.AddAsync("following.decoder handles one new byte per poll across a multibyte character", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllBytes(temp.Path, []);
            await using var reader = new LogTailReader(temp.Path, Settings() with { ReadBudgetBytes = 1 }); reader.Start();
            const string line = "典狱 吞噬完成"; var all = Encoding.UTF8.GetBytes(line + "\n"); var lines = new List<string>();
            foreach (var value in all) { Append(temp.Path, [value]); var read = await reader.ReadAsync(); Check.True(read.BytesRead <= 1); lines.AddRange(read.Lines); }
            Check.True(lines.SequenceEqual(new[] { line }));
        });
        suite.AddAsync("following.missing log waits then reads a newly created file from its beginning", async () =>
        {
            using var temp = new LogFixture(); await using var reader = new LogTailReader(temp.Path, Settings()); reader.Start();
            Check.True((await reader.ReadAsync()).WaitingForFile);
            File.WriteAllText(temp.Path, "Tartarus devour\n", new UTF8Encoding(false)); var appeared = await reader.ReadAsync();
            Check.False(appeared.WaitingForFile); Check.True(appeared.Lines.SequenceEqual(new[] { "Tartarus devour" }));
        });
        suite.AddAsync("following.truncation and replacing the log reset stale decoder state", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllText(temp.Path, new string('a', 512) + "\n", new UTF8Encoding(false));
            await using var reader = new LogTailReader(temp.Path, Settings()); reader.Start();
            File.WriteAllText(temp.Path, "Tartarus devour\n", new UTF8Encoding(false)); var shortened = await reader.ReadAsync();
            Check.True(shortened.Reset); Check.True(shortened.Lines.SequenceEqual(new[] { "Tartarus devour" }));
            var replacement = temp.Path + ".new"; File.WriteAllText(replacement, new string('b', 300) + "\n典狱 吞噬完成\n", new UTF8Encoding(false)); File.Move(replacement, temp.Path, true);
            var replaced = await reader.ReadAsync(); Check.True(replaced.Reset); Check.True(replaced.Lines.Contains("典狱 吞噬完成"));
            Check.False(replaced.Lines.Any(line => line.Contains("Tartarus devour")), "Replayed data from the replaced inode");
        });
        suite.AddAsync("following.same-size overwrite with an unchanged prefix detects the changed tail anchor", async () =>
        {
            using var temp = new LogFixture(); var prefix = new string('x', 480) + "\n";
            File.WriteAllText(temp.Path, prefix + new string('y', 31) + "\n", new UTF8Encoding(false));
            await using var reader = new LogTailReader(temp.Path, Settings()); reader.Start(); var size = new FileInfo(temp.Path).Length;
            File.WriteAllText(temp.Path, prefix + "Tartarus devour".PadRight(31) + "\n", new UTF8Encoding(false));
            Check.Equal(size, new FileInfo(temp.Path).Length); var changed = await reader.ReadAsync();
            Check.True(changed.Reset); Check.Equal("Tartarus devour", changed.Lines.Last().TrimEnd());
        });
        suite.AddAsync("following.read budget skips old backlog and discards the leading partial line", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllBytes(temp.Path, []);
            await using var reader = new LogTailReader(temp.Path, Settings() with { ReadBudgetBytes = 24 }); reader.Start();
            Append(temp.Path, Encoding.UTF8.GetBytes(new string('x', 300) + "\nTartarus devour\n")); var result = await reader.ReadAsync();
            Check.True(result.SkippedBytes); Check.True(result.BytesRead <= 24); Check.True(result.Lines.SequenceEqual(new[] { "Tartarus devour" }));
            Check.Equal(new FileInfo(temp.Path).Length, result.Position);
        });
        suite.AddAsync("following.overlong lines are dropped whole instead of becoming false log events", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllBytes(temp.Path, []);
            await using var reader = new LogTailReader(temp.Path, Settings() with { MaxLineCharacters = 32 }); reader.Start();
            Append(temp.Path, Encoding.UTF8.GetBytes("Tartarus devour " + new string('x', 300) + "\nS16GamePlay Start\n")); var result = await reader.ReadAsync();
            Check.True(result.DroppedLines >= 1); Check.True(result.Lines.SequenceEqual(new[] { "S16GamePlay Start" }));
        });
        suite.AddAsync("following.cancelled reads do not advance the tail or corrupt the next line", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllBytes(temp.Path, []);
            await using var reader = new LogTailReader(temp.Path, Settings()); reader.Start();
            Append(temp.Path, Encoding.UTF8.GetBytes("Tartarus devour\n")); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(cancel.Token));
            Check.True((await reader.ReadAsync()).Lines.SequenceEqual(new[] { "Tartarus devour" }));
        });
        suite.AddAsync("following.service defaults disabled and only new scoped lines produce a delayed hint", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllText(temp.Path, "S16GamePlay Start\n", new UTF8Encoding(false));
            var now = DateTimeOffset.Parse("2026-10-17T00:00:00Z"); await using var service = new LogFollowService(() => now);
            service.Start(temp.Path, new()); var disabled = await service.PollAsync();
            Check.Equal(LogFollowState.Disabled, disabled.Status.State); Check.True(disabled.Trigger is null); Check.Equal(0L, disabled.Status.TotalBytesRead);
            service.Start(temp.Path, Settings()); var connected = await service.PollAsync();
            Check.Equal(LogFollowState.Watching, connected.Status.State); Check.Equal(0L, connected.Status.TotalMatches); Check.False(connected.HasNewMatch);
            Append(temp.Path, Encoding.UTF8.GetBytes("player damage devour\n")); Check.False((await service.PollAsync()).HasNewMatch);
            Append(temp.Path, Encoding.UTF8.GetBytes("Tartarus devour\n")); var changed = await service.PollAsync();
            Check.True(changed.HasNewMatch); Check.True(changed.Trigger is null); Check.True(changed.Status.HasPending);
            now = now.AddMilliseconds(1499); Check.True((await service.PollAsync()).Trigger is null);
            now = now.AddMilliseconds(1); var ready = await service.PollAsync();
            Check.True(ready.Trigger is not null); Check.Equal(LogTriggerKind.BoardChanged, ready.Trigger!.Kind); Check.False(ready.Status.HasPending);
            Check.True((await service.PollAsync()).Trigger is null);
        });
        suite.AddAsync("following.service stop and cancellation cannot return a stale pending trigger", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllBytes(temp.Path, []); var now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
            await using var service = new LogFollowService(() => now); service.Start(temp.Path, Settings());
            Append(temp.Path, Encoding.UTF8.GetBytes("Tartarus devour\n")); Check.True((await service.PollAsync()).Status.HasPending);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => service.PollAsync(cancel.Token));
            service.Stop(); now = now.AddSeconds(30); var stopped = await service.PollAsync();
            Check.Equal(LogFollowState.Disabled, stopped.Status.State); Check.True(stopped.Trigger is null); Check.False(stopped.Status.HasPending);
            Append(temp.Path, Encoding.UTF8.GetBytes("S16GamePlay Start\n")); service.Start(temp.Path, Settings());
            var restarted = await service.PollAsync(); Check.False(restarted.HasNewMatch); Check.True(restarted.Trigger is null);
        });
        suite.AddAsync("following.public poll status and trigger never expose source log text", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllBytes(temp.Path, []); var now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
            await using var service = new LogFollowService(() => now); service.Start(temp.Path, Settings());
            const string secret = "SENSITIVE_SESSION_TOKEN_DO_NOT_EXPOSE";
            Append(temp.Path, Encoding.UTF8.GetBytes("Tartarus devour " + secret + "\n")); var matched = await service.PollAsync(); Check.True(matched.HasNewMatch);
            Check.False(JsonSerializer.Serialize(matched).Contains(secret, StringComparison.Ordinal));
            now = now.AddSeconds(2); var triggered = await service.PollAsync(); Check.True(triggered.Trigger is not null);
            Check.False(JsonSerializer.Serialize(triggered).Contains(secret, StringComparison.Ordinal));
        });
        suite.AddAsync("following.a temporarily locked existing log recovers at EOF without replaying history", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllText(temp.Path, "S16GamePlay Start SENSITIVE_OLD_LOG\n", new UTF8Encoding(false));
            await using var service = new LogFollowService();
            using (var writer = new FileStream(temp.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                service.Start(temp.Path, Settings()); var fault = await service.PollAsync();
                Check.Equal(LogFollowState.Faulted, fault.Status.State); Check.True(fault.RequiresReview); Check.True(fault.Trigger is null);
                Check.False(JsonSerializer.Serialize(fault).Contains("SENSITIVE_OLD_LOG", StringComparison.Ordinal));
            }
            var recovered = await service.PollAsync(); Check.Equal(LogFollowState.Watching, recovered.Status.State);
            Check.False(recovered.HasNewMatch); Check.True(recovered.Trigger is null); Check.Equal(0L, recovered.Status.TotalMatches);
            Append(temp.Path, Encoding.UTF8.GetBytes("Tartarus devour\n")); Check.True((await service.PollAsync()).HasNewMatch);
        });
        suite.AddAsync("following.an overlong unterminated line immediately requires review and cancels pending hints", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllBytes(temp.Path, []); var now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
            await using var service = new LogFollowService(() => now); service.Start(temp.Path, Settings() with { MaxLineCharacters = 32 });
            Append(temp.Path, Encoding.UTF8.GetBytes("Tartarus devour\n")); Check.True((await service.PollAsync()).Status.HasPending);
            Append(temp.Path, Encoding.UTF8.GetBytes(new string('x', 100))); var dropped = await service.PollAsync();
            Check.True(dropped.RequiresReview); Check.False(dropped.Status.HasPending); Check.True(dropped.Trigger is null);
            now = now.AddSeconds(2); Check.True((await service.PollAsync()).Trigger is null);
        });
        suite.AddAsync("following.a read gap invalidates a pending batch instead of triggering from stale history", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllBytes(temp.Path, []); var now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
            await using var service = new LogFollowService(() => now); service.Start(temp.Path, Settings() with { ReadBudgetBytes = 32 });
            Append(temp.Path, Encoding.UTF8.GetBytes("Tartarus devour\n")); Check.True((await service.PollAsync()).Status.HasPending);
            Append(temp.Path, Encoding.UTF8.GetBytes(new string('x', 300) + "\nordinary noise\n")); now = now.AddSeconds(2);
            var gap = await service.PollAsync(); Check.True(gap.RequiresReview); Check.False(gap.Status.HasPending); Check.True(gap.Trigger is null);
        });
        suite.AddAsync("following.reset or missing logs require review even without matching a game event", async () =>
        {
            using var temp = new LogFixture(); File.WriteAllBytes(temp.Path, []); await using var service = new LogFollowService(); service.Start(temp.Path, Settings());
            Append(temp.Path, Encoding.UTF8.GetBytes("ordinary unrelated noise\n")); var ordinary = await service.PollAsync(); Check.False(ordinary.HasNewMatch);
            File.WriteAllText(temp.Path, "n\n", new UTF8Encoding(false)); var reset = await service.PollAsync();
            Check.True(reset.RequiresReview); Check.False(reset.HasNewMatch); Check.True(reset.Trigger is null);
            File.Delete(temp.Path); var missing = await service.PollAsync(); Check.True(missing.RequiresReview); Check.Equal(LogFollowState.WaitingForFile, missing.Status.State); Check.True(missing.Trigger is null);
        });
    }

    private static LogFollowSettings Settings() => new() { Enabled = true };
    private static void Append(string path, byte[] bytes)
    { using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); stream.Write(bytes); stream.Flush(true); }
    private sealed class LogFixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "talos-follow-qa-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "client.log");
        public LogFixture() => Directory.CreateDirectory(directory);
        public void Dispose() => Directory.Delete(directory, true);
    }
}
