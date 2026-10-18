using System.Text.Json;
using Talos.Updates;

namespace Talos.Tests;

internal static class PayloadChecks
{
    public static void Register(Checks suite, string feedPath, string packagePath, string publicKeyPath)
    {
        suite.AddAsync("payload.actual signed bootstrap installs offline and verifies all declared files", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "talos-actual-payload-" + Guid.NewGuid().ToString("N"));
            var transport = new FakeTransport { DenyAll = true };
            try
            {
                var feed = await File.ReadAllBytesAsync(feedPath); var package = await File.ReadAllBytesAsync(packagePath);
                var service = new UpdateService(root, await File.ReadAllTextAsync(publicKeyPath), UpdateFixture.FeedUrl, new HttpClient(transport));
                var status = await service.InstallBootstrapAsync(feed, package);
                Check.True(status.ActiveValid); var active = await service.GetActiveDirectoryAsync();
                using var envelope = JsonDocument.Parse(feed);
                using var payload = JsonDocument.Parse(Convert.FromBase64String(envelope.RootElement.GetProperty("payload").GetString()!));
                Check.Equal(payload.RootElement.GetProperty("version").GetString(), status.ActiveVersion);
                var count = 0;
                foreach (var file in payload.RootElement.GetProperty("files").EnumerateArray())
                {
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(active, file.GetProperty("path").GetString()!));
                    Check.Equal(file.GetProperty("bytes").GetInt64(), bytes.LongLength);
                    Check.Equal(file.GetProperty("sha256").GetString(), UpdateFixture.Sha(bytes)); count++;
                }
                Check.True(count >= 2); Check.Equal(0, transport.Requests.Count);
                // Verify integrity rejection with the exact real signed manifest too.
                File.AppendAllText(Path.Combine(active, "Talos.App.dll"), "QA corruption");
                Check.False((await service.StatusAsync()).ActiveValid);
                await Check.ThrowsAsync<UpdateException>(() => service.GetActiveDirectoryAsync());
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }
}
