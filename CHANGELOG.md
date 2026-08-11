# Changelog

All notable changes to UECI will be documented here.

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
