# Architecture

## Principles

1. **UBT is the dependency authority.** UECI should not attempt to permanently reimplement Unreal's `Build.cs` semantics.
2. **No full engine requirement.** Git source and GitDependencies payloads are independent lazy providers.
3. **VFS is an optimization, not a prerequisite.** Every build must have a materialized fallback.
4. **Secrets never enter project config.** Only environment-variable names are stored.
5. **A clean Linux workstation can run the entire unit test suite.** CI adds coverage; it is not the test harness.

## Layers

```text
                      UECI build orchestration
                              │
                       logical EngineView
                              │
          ┌───────────────────┼───────────────────┐
          │                   │                   │
    Git source layer    GitDependencies layer   host SDK provider
          │                   │                   │
  GitHub partial clone   Epic dependency CDN   native platform SDK
          │                   │                   │
          └───────────────────┼───────────────────┘
                              │
                      writable overlay
                              │
                ┌─────────────┴─────────────┐
                │                           │
          materialized tree             mounted tree
                │                           │
          all platforms          FUSE / WinFsp / macOS
```

## GitDependencies model

The Epic manifest is already a content-addressed graph:

```text
File.Name
   │
File.Hash
   ▼
Blob.Hash ── Size
   │
   ├── PackHash
   └── PackOffset
         │
         ▼
       Pack.Hash
         ├── Size
         ├── CompressedSize
         └── RemotePath
```

UECI preserves this structure instead of inventing another lock format. v0.2 adds a materializer that groups selected blobs by pack, downloads each required gzip payload once, validates the `UEPACK00` decompressed header, extracts blobs in ascending `PackOffset` order, and verifies every blob SHA-1 before caching it.

## Epic source model

`ueci epic init` creates a Git repository and fetches a single Unreal ref using protocol v2 and `--filter=blob:none`. The working tree stays empty. A requested individual file can be obtained with `git cat-file`. For larger source selections such as the UBT bootstrap seed, UECI uses a cone-mode sparse checkout and prefers `git backfill --sparse` so missing promisor blobs are downloaded in batches before worktree population. Older Git versions retain a lazy-checkout fallback for compatibility.

## Future writable overlay

The immutable lower layers must stay clean. UBT/UAT can write to `Engine/Intermediate`, `Engine/Saved`, plugin `Intermediate`, and plugin `Binaries` through an upper layer. Mounted backends should implement copy-on-write semantics; materialized mode can use an ordinary writable directory.

## v0.2 cache/materialization path

```text
Commit.gitdeps.xml
        │
        ▼
     planner
        │
        ▼
 group by PackHash
        │
        ▼
 Epic dependency CDN
        │
        ▼
 packs/<sha1>.gz       optional persistent cache
        │
      gunzip
        │
   UEPACK00 + blobs
        │
        ▼
 blobs/<sha1>          verified content cache
        │
        ▼
 materialized Engine paths
```

`--no-pack-cache` keeps the compressed pack only as a temporary file while retaining verified blobs. This is intended for ephemeral runners where peak disk usage matters more than avoiding a future CDN download.

## v0.3 UBT bootstrap

```text
Epic blobless Git
      │
      ├── UBT + Shared C# source
      └── Commit.gitdeps.xml
                 │
                 ▼
       bundled .NET SDK resolver
                 │
                 ▼
      GitDependencies overlay
                 │
                 ▼
 dotnet build UnrealBuildTool.csproj
                 │
                 ▼
       UnrealBuildTool.dll
                 │
       runtimeconfig validation
                 │
                 ▼
             UBT -help
```

The source repository is the authority for UBT code. GitDependencies supplies Epic's managed build support and host SDK. A precompiled UBT binary is **not** assumed to exist in Git.


## v0.4 lazy plugin build

```text
plugin descriptor
      │
      ▼
ephemeral project + target rules
      │
      ▼
real UBT, restricted to plugin modules
      │
      ├── missing module ──► tracked `.Build.cs` ──► sparse Git subtree
      ├── missing source  ──► pinned Epic Git blob/subtree
      ├── missing payload ─► Commit.gitdeps.xml ──► verified blob cache
      ├── missing Linux SDK ─► Linux_SDK.json ──► Epic native toolchain CDN
      └── success
             │
             ▼
        plugin package + report
```

Discovery is monotonic and bounded. The Linux native clang/sysroot archive is treated as an external SDK provider because Epic normally installs it through `Setup.sh` rather than `Commit.gitdeps.xml`; UECI defers that large download until UBT emits a platform-SDK failure. The authoritative extracted payload lives outside the Git-managed `Engine/` subtree. Materialized builds retain their workspace-local compatibility store, while the mounted cold-runner backend uses the shared UECI cache at `toolchains/installed/linux-x64/<version>`. UECI projects that store into Epic's expected `Engine/Extras/ThirdPartyNotUE/SDKs/...` path and recreates the projection after sparse updates. UECI never treats a failed build as permission to materialize all of `Engine/Source`; it retries only when a diagnostic can be mapped to a concrete module/path/SDK requirement.
