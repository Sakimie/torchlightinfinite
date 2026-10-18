# C# preview 0.4.0 shared contract

User authorizes migration of the test application to C#. Build a COMPLETE offline Windows x64 package, not another patch requiring Python/_internal. Existing published Python 0.3.x feeds/artifacts stay intact. New preview feed: https://raw.githubusercontent.com/Sakimie/torchlightinfinite/talos-updates/csharp/latest.json . Publish preview separately after real builds and meaningful tests; do not claim Windows UI execution on Linux.

## Projects and ownership

- Talos.Core/net10.0: backend agent owns csproj, GameState.cs, GameAdvisor.cs, EngineCatalog.cs, etc. Pure C#, NO Python/Node/subprocess/network. Port our OWN lib/offline-engine.js/offline-solver.js mechanics/visible-state heuristics, not the unlicensed reference source. No old engine validation statistics transfer to this new implementation.
- Talos.Core/Vision/: vision agent owns pure pixel recognition/types here. No Windows dependency. Desktop converts images to RGBA. Original user-provided assets/templates.json and PNG crops can be read as matching reference; coordinate calibration/manual confirmation mandatory. Do not claim full Chinese OCR or measured accuracy.
- Talos.App/net10.0-windows: UI agent owns WPF library/capture/overlay/review/update UI, explicit entry `public static int Talos.App.AppHost.Run(string contextJson)`. References Core and Updates. Programmatic UI is fine. No dependency on Python or Node.
- Talos.Updates/net10.0: updater agent owns csproj/service/models. Native ECDSA P-256/SHA256 signature verify, exact payload bytes, .NET built-in crypto; NO Node. Common LaunchContext record below. Stable API below. Manage immutable payload releases, atomic active state, local rollback, explicit-only HTTPS. Updater assembly is included in stable host and app references the same assembly.
- Talos.Host/net10.0-windows: ROOT owns stable self-contained single-file .NET WPF host. Embeds signed initial payload ZIP + feed + new public key. Starts STA, installs bootstrap OFFLINE into LocalAppData/TalosHelperCSharp/preview, verifies selected release, loads Talos.App.dll from release using AssemblyLoadContext.Default and invokes AppHost.Run(contextJson). Resolve Talos.Core from version folder; Updates remains host-shared. All .NET native dependencies bundled/self-extracted. No executable needs prior runtime installation.
- Talos.Tests/net10.0 console checks: QA agent owns meaningful cross-platform tests, add files/fixtures here without external test packages. Tests Core/Vision/Updates and actual payload/signature. Windows WPF compilation is cross-build only.
- ROOT owns SDK setup, solution/root props, signing key/public key, bundling/publishing and source snapshots. Strategy audit agent independently checks C# vs own JS legal actions/heuristics and reports material discrepancies.

## Core public API (coordinate additive refinements)

namespace Talos.Core:

`public sealed record RoomState { public int Index {get;init;} public string Kind {get;init;}="unknown"; public string? Species {get;init;} public int? Tier {get;init;} public bool Event {get;init;} public string? EventKind {get;init;} public bool Ivory {get;init;} public int? NestDue {get;init;} }`

`public sealed record GameState` with init properties: Day=1, Position=0, Phase="patrol", List<RoomState> Rooms (16 index0 office), List<int> Destinations, List<string> Tools, Vault=0, Objective="other", ObjectiveProgress=0, Milestone=false, UsedAction=false, ChainArmed=false, ChainConsumed=0, UsurpStage=1 (1..4,5=done), string? EventKind, List<string> OfferIds, string? ResumePhase, bool Confirmed=false, bool UnsupportedPresent=false, string Source="manual".

Kinds unknown/office/locked/empty/nest/inmate. Species mammoth/lion/wolf/rat; tiers0..4 only, rainbow must be explicitly banked. Stages patrol/manage/office/event/tool/gift/chain/ratway. Room indexes0..15, UI shows1..16. Do not infer current day from remaining-days OCR.

`public sealed record AdviceCandidate(string ActionId,string Label,string Reason,double Score,int? Room=null,int? Target=null);`
`public sealed record AdviceResult(AdviceCandidate Best,IReadOnlyList<AdviceCandidate> Alternatives,string StrategyId,string Limitations);`
`public static class GameAdvisor { public static IReadOnlyList<string> Validate(GameState state); public static AdviceResult Recommend(GameState state); public static GameState CreateExample(); }`
`EngineCatalog` public IReadOnlyDictionary<string,string> Species/Phases/Kinds/Tools/Events/Gifts/Objectives and IReadOnlyList<string> Tiers. Expose unsupported mechanisms explicitly; reject unknown tools/events/flags instead of silently dropping them. Unknown room is never treated as empty/locked. Recommend requires Confirmed.

Scores are relative heuristics, not probabilities or global-optimum promises. Defaults, guide fixes (usurp2 one+2, instigate one event, muzzle threshold3) should be explicit. Preserve documented timing/probability assumptions. No retraining claim.

## Vision public API

Agent defines pixel types under Talos.Core.Vision and messages exact API to UI/QA. Use flat RGBA byte[] and rectangles so Core tests run Linux. Invalid/low-confidence results remain unknown and cannot bypass human review. Provide pure crop/resize/match + calibration; no runtime network. Notify UI promptly of final interface.

## Update models/API

namespace Talos.Updates:

`public sealed record LaunchContext(string Version,string VersionRoot,string DataRoot,string HostPath,string PublicKeyPem,string FeedUrl);`

`UpdateService(string dataRoot,string publicKeyPem,string feedUrl, HttpClient? client=null)`
- `Task<UpdateStatus> StatusAsync(CancellationToken cancellationToken=default)` only local, returns ActiveVersion, ActiveValid, InstalledVersions, warnings if useful.
- `Task<ReleaseManifest> CheckAsync(CancellationToken cancellationToken=default)` explicit HTTPS, verifies signed envelope, returns authenticated manifest identity (InstallAsync cannot accept forged mutable objects).
- `Task<UpdateStatus> InstallAsync(ReleaseManifest manifest,IProgress<UpdateProgress>? progress=null,CancellationToken cancellationToken=default)` verified HTTPS download -> safe ZIP validation/staging -> immutable version -> atomic active switch. Return status.
- `Task<UpdateStatus> InstallBootstrapAsync(byte[] signedFeed,byte[] package,CancellationToken cancellationToken=default)` same verification offline; if already initialized do not downgrade.
- `Task<UpdateStatus> RollbackAsync(string? version=null,CancellationToken cancellationToken=default)` only validated healthy local version, no HTTPS.
- `Task<string> GetActiveDirectoryAsync(CancellationToken cancellationToken=default)` fully verify active directory, returns version root for host assembly loading. No PE execution.

UpdateStatus record/class exposes `string? ActiveVersion`, `bool ActiveValid`, `IReadOnlyList<string> InstalledVersions`. UpdateProgress exposes `string Stage`, `long Done`, `long Total`, `string Message`. Service uses `%LocalAppData%/TalosHelperCSharp/preview/releases/<version>`; state.json holds authenticated signed manifests, not user-editable arbitrary entry paths. OS lock, interruption recovery, signature/files hash checks, path traversal/symlink/Windows alias/ZIP bomb controls are meaningful requirements. Keep all networking out of startup/Status/Rollback/GetActiveDirectory. Cancellation before commit preserves old active.

New signed envelope `{ "schema":"talos-signed-csharp-feed-v1", "algorithm":"ES256", "payload":"base64 exact UTF8 manifest", "signature":"base64 64byte IEEE-P1363 ECDSA P256 SHA256 signature" }`. Signer uses Node crypto only as developer build tool; product uses built-in ECDsa.ImportFromPem/VerifyData with IeeeP1363FixedFieldConcatenation. New public key csharp/update-public-key.pem; private key outside repo, never package/log it. Old Ed25519 files unchanged.

Manifest camelCase fields: schema="talos-csharp-release-v1", app="TalosHelper", version="0.4.0", launcherMin="1.0.0", publishedAt=UTC ISO8601, notes=string[], entryAssembly="Talos.App.dll", package={url:https immutablecommit ZIP,bytes:int,sha256:hex}, files=[{path:relative version file,bytes:int,sha256:hex}]. ZIP only version files including Talos.App.dll/Talos.Core.dll/templates; no host/runtime. Feed itself not in ZIP, avoid circular hashes. Host boots with embedded exact feed+payload. Root stages payload publicly then signs immutable URL before embedding.

## Workflow and QA

Root installs pinned verified official .NET10 SDK into /workspace/.dotnet. Use /workspace/.dotnet/dotnet or export PATH locally. No third-party C# runtime packages needed unless justified. Windows projects EnableWindowsTargeting=true. Console tests run net10.0 Linux; WPF publish targets win-x64 self-contained. Preserve cryptographic and TLS verification. Expected no Windows PE execution here; never claim Windows screenshot/hotkey/DPI tests passed. Complete package should contain a single main EXE plus instructions/licenses, no original v0.3 dependency.
