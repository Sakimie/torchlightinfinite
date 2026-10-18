using System.Diagnostics;
using System.Text.Json;

namespace Talos.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--json" && !json) { json = true; continue; }
            if (args[i] is "--bootstrap-feed" or "--bootstrap-package" or "--public-key" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && !paths.ContainsKey(args[i]))
            { paths.Add(args[i], args[++i]); continue; }
            Console.Error.WriteLine("Usage: dotnet run --project csharp/Talos.Tests [-- --json [--bootstrap-feed feed.json --bootstrap-package payload.zip --public-key public.pem]]");
            return 2;
        }
        if (paths.Count is not (0 or 3)) { Console.Error.WriteLine("All three bootstrap paths must be supplied together."); return 2; }
        var suite = new Checks();
        CoreChecks.Register(suite);
        VisionChecks.Register(suite);
        UpdateChecks.Register(suite);
        StorageChecks.Register(suite);
        SimulationChecks.Register(suite);
        FollowingChecks.Register(suite);
        if (paths.Count == 3) PayloadChecks.Register(suite, paths["--bootstrap-feed"], paths["--bootstrap-package"], paths["--public-key"]);
        var results = await suite.RunAsync();
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        else
        {
            foreach (var result in results)
                Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Name}{(result.Error is null ? "" : ": " + result.Error)}");
            Console.WriteLine($"{results.Count(result => result.Passed)}/{results.Count} checks passed.");
        }
        return results.All(result => result.Passed) ? 0 : 1;
    }
}

internal sealed record CheckResult(string Name, bool Passed, double Milliseconds, string? Error);

internal sealed class Checks
{
    private readonly List<(string Name, Func<Task> Run)> _checks = [];
    public void Add(string name, Action run) => AddAsync(name, () => { run(); return Task.CompletedTask; });
    public void AddAsync(string name, Func<Task> run) => _checks.Add((name, run));
    public async Task<List<CheckResult>> RunAsync()
    {
        var results = new List<CheckResult>();
        foreach (var check in _checks)
        {
            var watch = Stopwatch.StartNew();
            try { await check.Run(); results.Add(new(check.Name, true, watch.Elapsed.TotalMilliseconds, null)); }
            catch (Exception ex) { results.Add(new(check.Name, false, watch.Elapsed.TotalMilliseconds, ex.ToString())); }
        }
        return results;
    }
}

internal static class Check
{
    public static void True(bool value, string message = "Expected true")
    { if (!value) throw new InvalidOperationException(message); }
    public static void False(bool value, string message = "Expected false") => True(!value, message);
    public static void Equal<T>(T expected, T actual, string? message = null)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(message ?? $"Expected {expected}, got {actual}"); }
    public static void Near(double expected, double actual, double tolerance = 1e-9)
    { True(double.IsFinite(actual) && Math.Abs(expected - actual) <= tolerance, $"Expected {expected} ± {tolerance}, got {actual}"); }
    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
    public static async Task<T> ThrowsAsync<T>(Func<Task> action, string? message = null) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new InvalidOperationException(message ?? $"Expected {typeof(T).Name}");
    }
}
