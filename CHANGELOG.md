# Changelog

All notable changes to UECI will be documented here.

## [0.4.0-alpha.2] - 2026-08-12

### Fixed

- Repairs GitDependencies overlay files that can be displaced when the plugin discovery loop expands the sparse Epic Git worktree.
- Fixes the real Linux plugin smoke failure where `Engine/Binaries/ThirdParty/DotNet/.../dotnet` existed during UBT bootstrap but disappeared before the first plugin UBT invocation.
- Tracks the bootstrap GitDependencies working set and restores only missing paths from the content-addressed blob cache after sparse updates, avoiding a network re-download on a warm cache.
- Newly discovered GitDependencies paths join the same overlay tracker so later sparse-discovery passes cannot silently invalidate already-satisfied requirements.

### Tests

- Adds an offline CAS repair test that materializes a synthetic GitDependencies overlay, deletes a materialized file, and verifies the repair performs zero additional downloads.

### Changed

- CLI version advanced to `0.4.0-alpha.2`.

## [0.4.0-alpha.1] - 2026-08-12

### Added

- Experimental `ueci build-plugin <Plugin.uplugin>` end-to-end command.
- `.uplugin` module descriptor reader with Runtime/Editor/Program classification.
- Ephemeral `UECIHost` project with Game and Editor targets; stale generated platform `Binaries`, `Intermediate`, and `Saved` inputs are excluded while plugin-owned `Binaries/ThirdParty` payloads are preserved.
- UBT invocation restricted with `-Module=<name>` for the plugin modules while keeping `.Build.cs` execution inside real UnrealBuildTool.
- Full pinned Epic Git tree index for locating missing module rule files and source suffixes without checking out the tree.
- Diagnostic parser for missing modules, Engine paths/includes/files, and platform SDK failures.
- Lazy native Linux x86_64 toolchain installer: reads `Engine/Config/Linux/Linux_SDK.json`, downloads the matching Epic `native-linux-<MainVersion>.tar.gz` only after UBT reports a missing Linux SDK, safely extracts it under the Engine SDK layout, and reuses/removes the archive according to cache policy.
- Bounded lazy retry loop that expands sparse Epic source subtrees or materializes GitDependencies payloads only when UBT exposes a concrete requirement.
- Plugin packaging that preserves produced `Binaries`, removes `Intermediate`/`Saved`, and emits `ueci-build.json`.
- Minimal `UECIMinimal` Runtime plugin fixture plus `scripts/smoke-plugin.sh` for an opt-in real Epic build on an ordinary workstation.
- Offline local tests for descriptor parsing, host generation, diagnostic extraction, tracked-path indexing, UBT argument generation, packaging, Linux SDK version resolution, and synthetic native-toolchain extraction/cache behavior.

### Changed

- Composite GitHub Action can optionally run `build-plugin` when `plugin-path` is provided; UBT-only bootstrap remains available when it is omitted.
- CLI version advanced to `0.4.0-alpha.1`.

### Known experimental limits

- The first real validation target is the minimal Runtime fixture on Linux; new UBT diagnostic shapes may intentionally stop discovery instead of materializing broad Engine subtrees.
- Program-only plugin modules are rejected for now.
- UAT `BuildPlugin` is not used yet; packaging is the narrower UBT-driven path.

## [0.3.0-alpha.5] - 2026-08-12

### Fixed

- Stops assuming a successful `dotnet build` must place `UnrealBuildTool.dll` in `Engine/Binaries/DotNET/UnrealBuildTool`.
- Discovers a runnable `UnrealBuildTool.dll` + `UnrealBuildTool.runtimeconfig.json` pair in the canonical output or the UBT project's `bin/**` output tree.
- `ueci ubt run` uses the same post-build output resolver, so a non-canonical MSBuild output remains runnable after bootstrap.
- A successful MSBuild invocation that produces no runnable UBT pair now reports captured MSBuild stdout/stderr and any DLL candidates instead of claiming the DLL should have come from Epic Git.
- Bootstrap progress prints the resolved UBT assembly path before the runtime probe.

### Tests

- Adds an offline test for discovering a generated `bin/Debug/net10.0/UnrealBuildTool.dll` output.

## [0.3.0-alpha.4] - 2026-08-12

### Fixed

- Avoids the pathological slow path observed while materializing the UBT/shared source seed from a `--filter=blob:none` Epic repository.
- Uses cone-mode sparse checkout plus `git backfill --sparse` when available to batch the missing source-seed blobs before worktree population.
- Retains the existing lazy-checkout behavior as a compatibility fallback when `git backfill` is unavailable.
- Reports the number of tracked files in the Epic Git seed and the selected materialization strategy before the potentially expensive operation.

### Tests

- Adds a local bare-repository partial-clone test that verifies sparse source materialization and confirms unrelated paths stay absent without requiring Epic credentials or network access.

## [0.3.0-alpha.3] - 2026-08-12

### Fixed

- Fixes C# `CS0136` compilation errors in `EpicBundledDotNetSdkResolver` caused by local variable shadowing.
- Keeps SDK candidate parsing semantically unchanged while using distinct candidate/selected variable names.

## [0.3.0-alpha.2] - 2026-08-12

### Fixed

- UBT bootstrap no longer assumes `UnrealBuildTool.dll` is tracked in Epic Git.
- Materializes `Engine/Source/Programs/UnrealBuildTool` and shared managed sources, then compiles UBT from source.
- Resolves the complete host-specific Epic bundled .NET SDK from `Commit.gitdeps.xml` before compilation.
- Materializes root/managed build-support files from GitDependencies before invoking MSBuild.
- Reads and validates `UnrealBuildTool.runtimeconfig.json` only after UBT has produced it.
- Adds an isolated local NuGet/DOTNET home under the bootstrap root and disables multilevel runtime lookup for reproducibility.

### Tests

- Adds offline bundled SDK resolution coverage.
- Real-manifest smoke validation now confirms a host SDK can be selected.

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
