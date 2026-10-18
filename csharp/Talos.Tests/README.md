# Native C# checks

Run from the repository root with the pinned .NET 10 SDK:

```sh
/workspace/.dotnet/dotnet run --project csharp/Talos.Tests/Talos.Tests.csproj -- --json
```

No external test framework or runtime packages are required. The runner exits nonzero if any check fails. Unknown or incomplete arguments fail before running tests.

To also verify a real signed build payload without running its binaries:

```sh
/workspace/.dotnet/dotnet run --project csharp/Talos.Tests/Talos.Tests.csproj -- \
  --json --bootstrap-feed /path/feed.json \
  --bootstrap-package /path/payload.zip --public-key /path/public-key.pem
```

The default suite checks observed-state validation, eight decision stages, deterministic rule boundaries, synthetic RGBA recognition and mandatory manual review. Updater checks use freshly generated ECDSA P-256 keys, inert payload bytes, isolated temporary directories and a fake HTTP handler. They cover authentication, Windows paths, ZIP limits, cancellation before commit, partial-install recovery, corrupt-version rollback and offline local APIs. The optional real-payload check authenticates the signed feed, installs and hashes every declared file, then confirms corruption blocks loading. It never loads an assembly or executes a Windows binary.

These checks are Linux console evidence. They do not measure screenshot accuracy or game rewards, confirm uncertain in-game timing, or run Windows WPF/capture/hotkey/DPI behavior. Cancellation and preconstructed orphan fixtures exercise recovery boundaries; they do not emulate arbitrary hardware power loss.
