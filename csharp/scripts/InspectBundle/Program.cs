using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

if (args.Length != 2) throw new ArgumentException("Expected Windows EXE and output directory");
string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
Assembly model = Assembly.LoadFrom("/workspace/.dotnet/sdk/10.0.402/Microsoft.NET.HostModel.dll");
Type bundler = model.GetType("Microsoft.NET.HostModel.Bundle.Bundler", true)!;
MethodInfo detect = bundler.GetMethod("IsBundle", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
object?[] detection = [input, 0L];
if (detect.Invoke(null, detection) is not true) throw new InvalidDataException("Expected genuine .NET single-file bundle");
long header = (long)detection[1]!;
using var stream = File.OpenRead(input);
using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
stream.Position = header;
uint major = reader.ReadUInt32(), minor = reader.ReadUInt32();
int count = reader.ReadInt32();
if (major != 6 || count is < 5 or > 5000) throw new InvalidDataException("Unsupported bundle manifest");
string id = reader.ReadString();
reader.ReadInt64(); reader.ReadInt64(); reader.ReadInt64(); reader.ReadInt64(); ulong bundleFlags = reader.ReadUInt64();
var entries = new List<Entry>();
for (int i = 0; i < count; i++)
{
    var entry = new Entry(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadByte(), reader.ReadString());
    if (entry.Offset < 0 || entry.Size < 0 || entry.CompressedSize < 0 || entry.Offset + (entry.CompressedSize > 0 ? entry.CompressedSize : entry.Size) > stream.Length)
        throw new InvalidDataException("Bundle entry outside executable");
    entries.Add(entry);
}
byte[] Content(Entry entry)
{
    stream.Position = entry.Offset;
    byte[] packed = reader.ReadBytes(checked((int)(entry.CompressedSize > 0 ? entry.CompressedSize : entry.Size)));
    if (entry.CompressedSize == 0) return packed;
    using var compressed = new MemoryStream(packed);
    using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
    using var result = new MemoryStream();
    deflate.CopyTo(result);
    byte[] bytes = result.ToArray();
    if (bytes.LongLength != entry.Size) throw new InvalidDataException("Bundle decompression size mismatch");
    return bytes;
}
var names = entries.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
File.WriteAllText(Path.Combine(output, "bundle-manifest.json"), JsonSerializer.Serialize(entries,
    new JsonSerializerOptions { WriteIndented = true }));
string[] needed = ["System.Private.CoreLib.dll", "PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll", "Talos.Updates.dll", "TalosHelper.dll"];
foreach (string name in needed) if (!names.Contains(name)) throw new InvalidDataException("Bundled runtime dependency missing: " + name);
byte[] host = Content(entries.Single(entry => entry.Name == "TalosHelper.dll"));
using var hostStream = new MemoryStream(host);
using var pe = new PEReader(hostStream);
MetadataReader metadata = pe.GetMetadataReader();
int resourceRva = pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress;
byte[] resources = pe.GetSectionData(resourceRva).GetContent().ToArray();
var hashes = new Dictionary<string, string>();
foreach (ManifestResourceHandle handle in metadata.ManifestResources)
{
    ManifestResource resource = metadata.GetManifestResource(handle);
    string name = metadata.GetString(resource.Name);
    if (!resource.Implementation.IsNil || !name.StartsWith("Talos.", StringComparison.Ordinal)) continue;
    int offset = checked((int)resource.Offset), size = BitConverter.ToInt32(resources, offset);
    byte[] bytes = resources.AsSpan(offset + 4, size).ToArray();
    string filename = name switch { "Talos.PublicKey" => "public-key.pem", "Talos.BootstrapFeed" => "latest.json", "Talos.BootstrapPackage" => "payload.zip", _ => throw new InvalidDataException("Unexpected embedded resource") };
    File.WriteAllBytes(Path.Combine(output, filename), bytes);
    hashes[name] = Convert.ToHexStringLower(SHA256.HashData(bytes));
}
if (hashes.Count != 3) throw new InvalidDataException("Incomplete signed bootstrap in host");
byte[] runtime = Content(entries.Single(entry => entry.Name.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)));
using JsonDocument configuration = JsonDocument.Parse(runtime);
JsonElement runtimeOptions = configuration.RootElement.GetProperty("runtimeOptions");
if (!runtimeOptions.TryGetProperty("includedFrameworks", out _) || runtimeOptions.TryGetProperty("frameworks", out _) || runtimeOptions.TryGetProperty("framework", out _))
    throw new InvalidDataException("Host unexpectedly depends on an installed framework");
string runtimeVersion = runtimeOptions.GetProperty("includedFrameworks").EnumerateArray()
    .Single(framework => framework.GetProperty("name").GetString() == "Microsoft.NETCore.App")
    .GetProperty("version").GetString()!;
string[] desktopNative = ["PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "vcruntime140_cor3.dll"];
var externalNative = new Dictionary<string, string>();
string packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
foreach (string name in desktopNative)
{
    if (names.Contains(name)) continue;
    string adjacent = Path.Combine(Path.GetDirectoryName(input)!, name);
    string officialFile = Path.Combine(packageRoot, "microsoft.windowsdesktop.app.runtime.win-x64", runtimeVersion, "runtimes", "win-x64", "native", name);
    if (!File.Exists(adjacent) || !File.ReadAllBytes(adjacent).AsSpan().SequenceEqual(File.ReadAllBytes(officialFile)))
        throw new InvalidDataException("External native runtime file missing or differs from official pack: " + name);
    externalNative[name] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(adjacent)));
}
bool noBundleExtractionRequired = bundleFlags == 0 && entries.All(entry => entry.Type is 1 or 3 or 4);
if (externalNative.Count > 0 && (!noBundleExtractionRequired || !names.Contains("Talos.Storage.dll")))
    throw new InvalidDataException("Portable host has an extraction dependency or lacks its data storage module");
string nativeRuntimeMode = "bundled-dlls";
object? staticRuntimeEvidence = null;
if (!names.Contains("coreclr.dll") || !names.Contains("clrjit.dll"))
{
    if (names.Contains("coreclr.dll") || names.Contains("clrjit.dll"))
        throw new InvalidDataException("Incomplete native runtime DLL pair");
    // .NET 10 singlefilehost links CoreCLR and JIT into the native PE itself.
    // Require three independent facts instead of treating missing DLLs as success:
    // official runtime-pack exclusion metadata, a byte-identical SDK .text section,
    // and the linked runtime/JIT export names in the delivered executable.
    string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    string template = Path.Combine(packages, "microsoft.netcore.app.host.win-x64", runtimeVersion, "runtimes", "win-x64", "native", "singlefilehost.exe");
    string runtimeList = Path.Combine(packages, "microsoft.netcore.app.runtime.win-x64", runtimeVersion, "data", "RuntimeList.xml");
    XDocument official = XDocument.Load(runtimeList);
    string[] linkedLibraries = ["coreclr.dll", "clrjit.dll"];
    foreach (string library in linkedLibraries)
        if (!official.Descendants("File").Any(file => ((string?)file.Attribute("Path"))?.EndsWith("/" + library, StringComparison.Ordinal) == true &&
            (string?)file.Attribute("DropFromSingleFile") == "true"))
            throw new InvalidDataException("Runtime pack does not authorize linked runtime layout: " + library);
    var deliveredCode = NativeText(input);
    var officialCode = NativeText(template);
    if (deliveredCode != officialCode) throw new InvalidDataException("Native runtime code differs from the official singlefilehost template");
    string[] requiredExports = ["CLRJitAttachState", "g_CLREngineMetrics", "g_dacTable", "DotNetRuntimeInfo", "DotNetRuntimeContractDescriptor"];
    var exports = NativeExports(input);
    foreach (string name in requiredExports)
        if (!exports.Contains(name)) throw new InvalidDataException("Linked native runtime export missing: " + name);
    nativeRuntimeMode = "statically-linked-singlefilehost";
    staticRuntimeEvidence = new { runtimeVersion, linkedLibraries, runtimePackDropFromSingleFile = true,
        officialNativeTextMatches = true, nativeTextBytes = deliveredCode.Bytes, nativeTextSha256 = deliveredCode.Sha256,
        runtimeExports = requiredExports };
}
stream.Position = 0;
string executableSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
var report = new { passed = true, windowsExeExecuted = false, scope = "Static PE/bundle/resource verification on Linux; Windows UI, capture, hotkeys and execution were not tested.",
    executableBytes = stream.Length, executableSha256, bundleVersion = $"{major}.{minor}", embeddedFiles = count,
    bundleFlags, noBundleExtractionRequired, bundleEntryTypes = entries.GroupBy(entry => entry.Type).ToDictionary(group => group.Key, group => group.Count()), externalNative,
    nativeRuntimeMode, staticRuntimeEvidence, runtimeDependenciesBundled = needed,
    includedFrameworks = runtimeOptions.GetProperty("includedFrameworks"), resources = hashes };
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(output, "bundle-inspection.json"), json);
Console.WriteLine(json);

// These helpers only read PE data; no executable or embedded managed assembly is run.
static (int Bytes, string Sha256) NativeText(string filename)
{
    using var file = File.OpenRead(filename);
    using var executable = new PEReader(file);
    if (executable.PEHeaders.CoffHeader.Machine != Machine.Amd64 || executable.PEHeaders.CorHeader != null)
        throw new InvalidDataException("Expected native Windows x64 host");
    var text = executable.PEHeaders.SectionHeaders.Single(section => section.Name == ".text");
    file.Position = text.PointerToRawData;
    byte[] bytes = new byte[text.SizeOfRawData]; file.ReadExactly(bytes);
    return (bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
}

static HashSet<string> NativeExports(string filename)
{
    using var file = File.OpenRead(filename);
    using var executable = new PEReader(file);
    using var binary = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
    long Offset(int rva)
    {
        var section = executable.PEHeaders.SectionHeaders.Single(item => rva >= item.VirtualAddress &&
            (long)rva < (long)item.VirtualAddress + item.SizeOfRawData);
        return (long)section.PointerToRawData + rva - section.VirtualAddress;
    }
    file.Position = Offset(executable.PEHeaders.PEHeader!.ExportTableDirectory.RelativeVirtualAddress) + 24;
    int count = binary.ReadInt32(); binary.ReadInt32(); int table = binary.ReadInt32();
    if (count is < 1 or > 4096) throw new InvalidDataException("Invalid PE export table");
    var names = new HashSet<string>(StringComparer.Ordinal);
    for (int i = 0; i < count; i++)
    {
        file.Position = Offset(table) + i * 4; int address = binary.ReadInt32();
        file.Position = Offset(address); var name = new StringBuilder();
        byte next;
        while ((next = binary.ReadByte()) != 0)
        {
            if (name.Length > 255 || next > 127) throw new InvalidDataException("Invalid PE export name");
            name.Append((char)next);
        }
        names.Add(name.ToString());
    }
    return names;
}

internal sealed record Entry(long Offset, long Size, long CompressedSize, byte Type, string Name);
