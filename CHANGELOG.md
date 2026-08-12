# Changelog

## [0.4.0-alpha.8] - 2026-08-12

### Fixed

- Preserve Epic native Linux toolchains across later sparse-checkout expansions by adding the installed external SDK directory to the active sparse specification.
- Add an offline local-Git regression test reproducing Git removing ignored/untracked SDK files outside the sparse cone.

### Changed

- CLI version advanced to `0.4.0-alpha.8`.

All notable changes to UECI will be documented here.

## [0.4.0-alpha.7] - 2026-08-12

### Fixed

- Moves the host Unreal Build Accelerator payload into the UBT bootstrap plan so `Engine/Binaries/<host>/UnrealBuildAccelerator` exists **before** `UnrealBuildTool.csproj` is compiled. The UE 5.8 Linux smoke showed that materializing the native UBA files only after UBT had already been built still left `ExecutorFactory.GetUBAExecutor()` reporting UBA unavailable.
- Makes the UBA bootstrap dependency explicit through `EpicBundledUbaResolver`: it requires both `Engine/Source/Programs/Shared/EpicGames.UBA/Library.props` and the host-specific native UBA prefix from `Commit.gitdeps.xml`.
- Forces the Epic-bundled `dotnet build` of UBT to run with `--no-incremental`, preventing an UBT assembly compiled before the native UBA payload was present from surviving a warm UECI cache.
- Keeps lazy UBA discovery as a compatibility fallback and recompiles UBT automatically if that fallback ever adds the host UBA payload after bootstrap.
- CLI version advanced to `0.4.0-alpha.7`.

### Testing

- Adds an offline resolver test for the managed `EpicGames.UBA/Library.props` + native host UBA pair.
- The optional real-manifest smoke now verifies the complete Linux host UBA bootstrap plan rather than only the native binary subtree.

## [0.4.0-alpha.6] - 2026-08-12

### Fixed

- Linux native toolchain extraction now stages under the Engine SDK directory so installation stays on the destination filesystem and no longer fails with `EXDEV` / `Invalid cross-device link` when the UECI cache and Engine live on different mounts.
- Toolchain installation keeps a validated cached archive after filesystem installation errors instead of deleting a good multi-hundred-megabyte download.
- Added a defensive recursive-copy fallback that preserves symbolic links and Unix executable modes for unusual bind-mount or nested-mount layouts.
- CLI version advanced to `0.4.0-alpha.6`.

## [0.4.0-alpha.5] - 2026-08-12

### Fixed

- Treats `UBA is not available` as a lazy build-executor requirement instead of stalling the plugin discovery loop. UECI materializes only the host-specific `Engine/Binaries/*/UnrealBuildAccelerator` GitDependencies subtree when UE 5.8 insists on pre-creating UBA.
- Plugin discovery now appends `Engine/Programs/UnrealBuildTool/Log.txt` to the captured diagnostics. UBT often keeps actionable platform/SDK details in this full log while stderr only contains the terminal executor failure, so one failed pass can now expose UBA and the Linux SDK together.
- CLI version advanced to `0.4.0-alpha.5`.

### Testing

- The offline diagnostic fixture covers the real UE 5.8 UBA failure text.
- The optional real-manifest smoke verifies that the Linux UBA GitDependencies prefix is present and plannable.

## [0.4.0-alpha.4] - 2026-08-12

### Fixed

- Fully disables both UE 5.8 UBA executor paths for the minimal plugin build by setting `bAllowUBAExecutor=false` and the still-read (but deprecated) `bAllowUBALocalExecutor=false`.
- Writes the same hermetic executor policy into all UBT configuration scopes used by UECI: `Engine/Saved/UnrealBuildTool`, the isolated UECI user profile, and the synthetic HostProject. This avoids depending on `XmlConfig` precedence or generated defaults.
- Fixes the real Linux smoke build still entering `ExecutorFactory.GetUBAExecutor()` even though the project-local remote UBA switch was disabled.
- Strengthens the Linux SDK diagnostic fixture with the exact UE 5.8 `Unable to find valid SDK(s) for Linux` / `Required=v26_clang-20.1.8-rockylinux8` shape observed during the real build.

### Changed

- CLI version advanced to `0.4.0-alpha.4`.

## [0.4.0-alpha.3] - 2026-08-12

### Fixed

- Makes plugin UBT invocations hermetic with respect to workstation-level `BuildConfiguration.xml` files by using an isolated UBT home/profile.
- Generates `<PROJECT>/Saved/UnrealBuildTool/BuildConfiguration.xml` for the synthetic host project and disables UBA, XGE, FASTBuild, and SN-DBS executors so the minimal engine working set uses the local executor instead of requiring accelerator binaries.
- Attempts to isolate the first real plugin discovery pass from workstation UBA settings; alpha.4 completes this by disabling both UBA switches at every UBT config scope.
- Prevents workstation-level deprecated settings from leaking into reproducible UECI builds.

### Tests

- Extends the offline host-project test to verify the generated project-local UBT executor configuration.

### Changed

- CLI version advanced to `0.4.0-alpha.3`.

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
