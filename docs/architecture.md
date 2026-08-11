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

UECI v0.1 deliberately preserves this structure instead of inventing another lock format.

## Epic source model

`ueci epic init` creates a Git repository and fetches a single Unreal ref using protocol v2 and `--filter=blob:none`. The working tree stays empty. A requested tracked file is later obtained with `git cat-file`, allowing Git's promisor remote to retrieve only the missing blob.

## Future writable overlay

The immutable lower layers must stay clean. UBT/UAT can write to `Engine/Intermediate`, `Engine/Saved`, plugin `Intermediate`, and plugin `Binaries` through an upper layer. Mounted backends should implement copy-on-write semantics; materialized mode can use an ordinary writable directory.
