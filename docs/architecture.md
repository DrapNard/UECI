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
              ┌───────────────┴───────────────┐
              │                               │
        Git source layer              GitDependencies layer
              │                               │
      GitHub partial clone             Epic dependency CDN
              │                               │
              └───────────────┬───────────────┘
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

`ueci epic init` creates a Git repository and fetches a single Unreal ref using protocol v2 and `--filter=blob:none`. The working tree stays empty. A requested tracked file is later obtained with `git cat-file`, allowing Git's promisor remote to retrieve only the missing blob.

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

The first Unreal-aware consumer composes a small physical engine root before attempting any C++ build:

```text
blobless Epic Git
   │
   └── checkout selected pathspecs
         Engine/Binaries/DotNET
         Engine/Build/Build.version
         Engine/Build/Commit.gitdeps.xml
                    │
                    ▼
       UnrealBuildTool.runtimeconfig.json
                    │
                    ▼
        Epic bundled runtime resolver
                    │
       Commit.gitdeps.xml + host RID
                    │
          ┌─────────┴─────────┐
          │                   │
Engine/Binaries/DotNET   ThirdParty/DotNet
 GitDeps overlay          dotnet + host + shared runtime
          │                   │
          └─────────┬─────────┘
                    ▼
           UnrealBuildTool.dll
```

This is intentionally a seed, not a static list claimed to be sufficient for plugin compilation. The next resolver stage will let UBT evaluate real target/module rule assemblies, observe requirements that are absent from the materialized tree, then request those paths from either Git or GitDependencies. The permanent architecture therefore keeps UBT above the providers rather than translating `.Build.cs` into a second UECI rule language.
