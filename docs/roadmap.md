# Roadmap

## Milestone 0.1 — substrate

- [x] Streaming GitDependencies summary
- [x] File/blob/pack index
- [x] Integrity validation
- [x] Path/prefix planning
- [x] Epic read-only Git credential path
- [x] Blobless Git source initialization
- [x] Single tracked-file materialization
- [x] Cross-platform local test harness
- [x] Composite GitHub Action bootstrap

## Milestone 0.2 — GitDependencies materializer

- [x] Pack downloader with bounded concurrency
- [x] Persistent pack/blob cache
- [x] SHA-1 validation before use
- [x] Implement validated gzip + `UEPACK00` pack decompression
- [x] Extract only selected blobs, grouped by pack in offset order
- [ ] Disk budget planner
- [ ] Cache garbage collection

## Milestone 0.3 — Unreal bootstrap/resolution

- [x] Bootstrap minimum Epic-bundled .NET + UBT inputs
- [ ] Bootstrap UAT/UHT inputs required by plugin builds
- [x] Obtain `Commit.gitdeps.xml` from the selected Git commit automatically
- [x] Ask UBT for real target/module rules instead of parsing `Build.cs` as YAML-like text
- [x] Requirement discovery/retry loop (experimental v0.4 path)
- [x] Host runtime/SDK filtering for bundled .NET
- [x] Source-build UnrealBuildTool with Epic bundled SDK
- [x] Host-to-UBT target platform mapping for plugin builds

## Milestone 0.4 — plugin packaging

- [x] Discover/parse `.uplugin` module descriptors
- [x] Synthetic `.uproject` + lean modular Game/Editor targets
- [x] UBT module-targeted compile loop (real Linux minimal-fixture smoke validated)
- [x] Package plugin + build report
- [x] Lazy native Linux x86_64 clang/sysroot toolchain acquisition (real compile validation complete)
- [x] Linux x64 real minimal-fixture smoke validation
- [ ] Windows x64
- [ ] macOS arm64

## Milestone 0.5 — VFS

- [ ] Stable `EngineView` API
- [x] Linux FUSE driver
- [ ] WinFsp driver/adaptor
- [ ] Evaluate macFUSE vs FSKit constraints
- [x] Copy-on-write upper layer
- [x] Automatic Linux mounted selection with materialized fallback on unsupported hosts

## Milestone 1.0

- [ ] Stable `.ueci.yml`
- [ ] GitHub Action builds plugins end-to-end
- [ ] Release binaries for common host architectures
- [ ] Proven build under hosted-runner disk budgets for supported plugin classes
- [ ] Security review of token handling and fork-PR workflow


## Immediate next targets after alpha.17

- Windows: native mounted Engine presentation via WinFsp while preserving the same `IEngineReadLayer`/COW contracts, exact-commit profiles, cache layout, timing model, and synthetic plugin build flow.
- macOS: native mounted Engine presentation after Windows, with both Apple Silicon and x64 host RIDs considered by the shared cache/toolchain abstractions.
- Linux remains the performance/reference backend; further optimization should be justified by cold-runner phase timings rather than by a large persistent native-object workspace.
