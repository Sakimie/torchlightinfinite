namespace Talos.Updates;

public sealed record LaunchContext(string Version, string VersionRoot, string DataRoot,
    string HostPath, string PublicKeyPem, string FeedUrl);

public sealed record UpdatePackage(string Url, long Bytes, string Sha256);
public sealed record ReleaseFile(string Path, long Bytes, string Sha256);

/// <summary>A verified, immutable release. Only UpdateService can construct it.</summary>
public sealed class ReleaseManifest
{
    public string Schema { get; }
    public string App { get; }
    public string Version { get; }
    public string LauncherMin { get; }
    public string PublishedAt { get; }
    public IReadOnlyList<string> Notes { get; }
    public string EntryAssembly { get; }
    public UpdatePackage Package { get; }
    public IReadOnlyList<ReleaseFile> Files { get; }
    internal string EnvelopeJson { get; }
    internal string Identity { get; }

    internal ReleaseManifest(string version, string launcherMin, string publishedAt,
        IEnumerable<string> notes, string entryAssembly, UpdatePackage package,
        IEnumerable<ReleaseFile> files, string envelopeJson, string identity)
    {
        Schema = "talos-csharp-release-v1";
        App = "TalosHelper";
        Version = version;
        LauncherMin = launcherMin;
        PublishedAt = publishedAt;
        Notes = Array.AsReadOnly(notes.ToArray());
        EntryAssembly = entryAssembly;
        Package = package;
        Files = Array.AsReadOnly(files.ToArray());
        EnvelopeJson = envelopeJson;
        Identity = identity;
    }
}

public sealed record UpdateStatus(string? ActiveVersion, bool ActiveValid,
    IReadOnlyList<string> InstalledVersions)
{
    public IReadOnlyList<string> History { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed record UpdateProgress(string Stage, long Done, long Total, string Message);

public class UpdateException : Exception
{
    public string Code { get; }
    public UpdateException(string message, string code = "update", Exception? inner = null)
        : base(message, inner) => Code = code;
}
