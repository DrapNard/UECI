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

- [ ] Pack downloader with bounded concurrency
- [ ] Persistent pack/blob cache
- [ ] SHA-1 validation before use
- [ ] Determine/implement pack decompression from Epic's format
- [ ] Extract only selected blobs
- [ ] Disk budget planner
- [ ] Cache garbage collection

## Milestone 0.3 — Unreal bootstrap/resolution

- [ ] Bootstrap minimum .NET/UBT/UAT/UHT inputs
- [ ] Obtain `Commit.gitdeps.xml` from the selected Git commit automatically
- [ ] Ask UBT for real target/module rules instead of parsing `Build.cs` as YAML-like text
- [ ] Requirement discovery/retry loop
- [ ] Host/target platform filtering

## Milestone 0.4 — plugin packaging

- [ ] Discover `.uplugin`
- [ ] Synthetic `.uproject`/targets when needed
- [ ] Compile code plugin
- [ ] Package deterministic artifact
- [ ] Linux x64
- [ ] Windows x64
- [ ] macOS arm64

## Milestone 0.5 — VFS

- [ ] Stable `EngineView` API
- [ ] Linux FUSE driver
- [ ] WinFsp driver/adaptor
- [ ] Evaluate macFUSE vs FSKit constraints
- [ ] Copy-on-write upper layer
- [ ] Mount capability detection with automatic materialized fallback

## Milestone 1.0

- [ ] Stable `.ueci.yml`
- [ ] GitHub Action builds plugins end-to-end
- [ ] Release binaries for common host architectures
- [ ] Proven build under hosted-runner disk budgets for supported plugin classes
- [ ] Security review of token handling and fork-PR workflow
