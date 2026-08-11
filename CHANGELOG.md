# Changelog

All notable changes to UECI will be documented here.

## [0.3.0-alpha.1] - 2026-08-12

### Added

- Minimal UnrealBuildTool bootstrap from the blobless Epic Git source plus GitDependencies.
- Incremental tracked-subtree materialization through authenticated Git checkout pathspecs.
- `UnrealBuildTool.runtimeconfig.json` parser and host-specific Epic bundled .NET resolver.
- Runtime selection by required framework major/minor with the highest available matching patch.
- Minimal bundled runtime materialization (`dotnet`, host/fxr, required shared frameworks) instead of the full Epic .NET SDK.
- `ueci ubt bootstrap` with optional UBT `-help` probe.
- `ueci ubt run` for forwarding arbitrary arguments to the bootstrapped UBT.
- Offline tests for runtime config parsing, bundled runtime selection, and UBT layout detection.
- Opt-in `scripts/smoke-ubt.sh` real Epic integration test.

### Changed

- Nullable reference annotations are enabled solution-wide.
- The composite GitHub Action now bootstraps/probes UBT rather than stopping after manifest retrieval.
- CLI version advanced to `0.3.0-alpha.1`.

## [0.2.0-alpha.1] - 2026-08-12

### Added

- Direct GitDependencies CDN pack downloader.
- gzip + `UEPACK00` pack decoding with decompressed absolute `PackOffset` extraction.
- SHA-1 verification before blobs are admitted into the content cache.
- Persistent compressed-pack and verified-blob caches.
- `--no-pack-cache`, `--cache-dir`, and bounded `--max-concurrent-packs` controls.
- `gitdeps fetch` for a single manifest path.
- `gitdeps materialize` for exact-path/prefix batch materialization.
- Grouped one-pass extraction for multiple selected blobs in the same pack.
- Unix executable-bit restoration from `IsExecutable`.
- Output-root traversal protection for batch materialization.
- Fully offline synthetic pack tests for extraction, cache behavior, corruption, and magic validation.

### Changed

- CLI version advanced to `0.2.0-alpha.1`.
- Architecture and testing documentation now describe the validated GitDependencies pack format.

## [0.1.0-alpha.1] - 2026-08-12

### Added

- Streaming parser for Epic `Commit.gitdeps.xml`.
- File → blob → pack resolution and integrity checks.
- Path/prefix pack planning with deduplication.
- Read-only Epic GitHub credential plumbing.
- Blobless `EpicGames/UnrealEngine` source initialization.
- On-demand tracked-file materialization with `git cat-file`.
- Initial VFS contracts for materialized/FUSE/WinFsp/macOS evolution.
- Dependency-free local test executable and optional real-manifest smoke test.
- Composite GitHub Action bootstrap.
- Project governance, security, contribution, and architecture documentation.
