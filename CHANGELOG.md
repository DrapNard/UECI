# Changelog

## [0.5.0-alpha.24] - 2026-08-14

### Fixed

- Pin runner-managed historical UnrealBuildTool execution to the exact `Microsoft.NETCore.App` runtime already hosting UECI, including `dotnet --fx-version`, so UE5.0 cannot fall back to an installed .NET Core 3.1 runtime that requires the obsolete OpenSSL 1.1 stack.
- Detect legacy Linux SDK environment variables from the exact commit's `UnrealBuildTool/Platform/Linux` sources and export only the variables that generation actually reads, while keeping the projected Epic compiler first in `PATH` through `CC`/`CXX`.
- Add an explicit legacy compiler probe to boundary-build diagnostics so UE4 Linux registration failures distinguish an unusable projected compiler from UBT SDK-selection logic.
- Let UE4.6 source releases fall back to UE4.5 `Required_*` dependency archives when no complete UE4.6 archive set is published, extracting only the Ionic.Zip/RPCUtility managed support files required to compile that exact UE4.6 UBT source.

### Added

- Regression coverage for exact runner framework pinning and source-detected legacy Linux SDK environment requirements.

### Validation

- The alpha.23 boundary smoke is green on UE5.1, UE5.7, and UE5.8, each producing `libUECIHost-UECIMinimal.so`; alpha.24 narrows the active boundary work to UE4.6, UE4.20, and UE5.0.
- CLI version advanced to `0.5.0-alpha.24`.

## [0.5.0-alpha.23] - 2026-08-14

### Fixed

- Disable Engine plugins enabled by default in the synthetic UE5 project descriptor while explicitly enabling the plugin under test, preventing older UE5 releases from pulling Paper2D/Engine/ApplicationCore into the deliberately lean host before the equivalent TargetRules switch existed.
- Make runner-managed historical UBT execution request `LatestMajor` roll-forward both in the generated runtimeconfig and on the `dotnet` host command line, avoiding fallback to an installed but OpenSSL-incompatible .NET Core 3.1 runtime after a successful modern-SDK bootstrap.
- Expose host-specific Unreal Build Accelerator native payloads lazily from GitDependencies and pass UBA opt-out command-line switches only when the exact UBT source advertises them, covering newer UE5 releases that validate UBA before the local executor configuration takes effect.
- Stop presenting Windows cross-compilation/AutoSDK environment variables to native-Linux legacy UBT. The projected Epic compiler remains first in `PATH` with `CC`/`CXX`, while Linux UBT can validate its native Setup.sh-style SDK layout without an injected incomplete AutoSDK root.
- Let UE4.6 source tags reuse a complete historical `Required_1of2.zip`/`Required_2of2.zip` dependency set from a sibling 4.6.x GitHub release when the exact source patch release does not carry those transitional Ionic.Zip/RPCUtility assets.

### Added

- Regression coverage for runner runtimeconfig roll-forward, UE5 project-level default-plugin suppression, source-detected UBA invocation flags, UBA native seed visibility, and legacy rejection of modern UBA flags.

### Validation

- The alpha.22 boundary smoke produced the first fully green matrix target on UE5.7, including `libUECIHost-UECIMinimal.so`; alpha.23 keeps that result as the current proven release while advancing the remaining boundary failures.
- CLI version advanced to `0.5.0-alpha.23`.

## [0.5.0-alpha.22] - 2026-08-14

- Expand the mounted managed safety working set with `Engine/Config` plus GitDependencies-backed resources under `Engine/Source/Programs/Shared`, covering runtime config redirects, Oodle helper libraries and Horde protobuf inputs discovered by the alpha.21 boundary matrix.
- Treat a runner-.NET UBT bootstrap as runner-managed end-to-end instead of requiring its generated runtimeconfig to resolve back into an Epic bundled shared-framework layout; this completes the OpenSSL-3 fallback path for UE5.0-era .NET Core hosts.
- Prepend the projected legacy Epic Linux compiler to `PATH` and expose `CC`/`CXX` alongside `LINUX_ROOT`/`LINUX_MULTIARCH_ROOT`, allowing native UE4 UBT platform registration to inspect the intended compiler rather than the runner's clang.
- Probe transitional UE4.6 release dependency ZIPs when available and index tracked `Engine/Binaries/DotNET` support files, covering legacy Ionic.Zip/RPCUtility layouts without weakening GitDependencies integrity checks.
- CLI version advanced to `0.5.0-alpha.22`.

## [0.5.0-alpha.21] - 2026-08-13

### Fixed

- Trim and validate local Unreal matrix version arguments so whitespace cannot turn `4.6` into an invalid Epic ref.
- Materialize Shared MSBuild `.props`/`.targets` controls from GitDependencies, including `UnrealEngine.CSharp.targets`, for modern UE5 cold UBT bootstrap.
- Materialize legacy managed UBT support assemblies such as `Ionic.Zip.Reduced.dll` even when a modern Epic .NET SDK bundle is selected.
- Export UE4 Linux SDK roots with the historical `LINUX_MULTIARCH_ROOT` trailing separator and `UE_SDKS_ROOT` AutoSDK root so UBT can register the installed Linux platform.
- Fall back from ancient Epic .NET hosts that cannot load the runner OpenSSL/ICU stack to the runner's already-required modern .NET SDK, with major runtime roll-forward for the generated UBT.

## [0.5.0-alpha.20] - 2026-08-13

### Fixed

- Recognizes the strict numeric/GUID pack-directory convention used by UE4.6-4.10 `Commit.gitdeps.xml` files that omit `BaseUrl`, while keeping arbitrary custom relative manifests fail-closed.
- Makes embedded compatibility seeds forward-compatible with persisted profiles by merging current safety Git metadata into learned profiles without a global Engine index or blob download.
- Expands the managed bootstrap safety set to the complete `Engine/Source/Programs/Shared` subtree and discovers tiny release-specific build inputs such as `UnrealEngine.csproj.props` and `Microsoft.VisualStudio.Setup.Configuration.Interop.dll` wherever GitDependencies stores them.
- Runs bundled .NET hosts with invariant globalization so UE5.0's historical .NET Core 3.1 SDK can bootstrap on minimal modern CI runners without a host ICU package.
- Fixes the two remaining alpha.19 preflight false negatives: the historical bundled-.NET fixture now contains a coherent host/runtime root, and the classic UE4 target assertion no longer mistakes `OutExtraModuleNames` for the modern property.

### Changed

- CLI version advanced to `0.5.0-alpha.20`.

## [0.5.0-alpha.19] - 2026-08-13

### Fixed

- Hardens the first complete 32-release Linux matrix run. Exact identifier detection no longer mistakes classic UE4 `OutExtraModuleNames` for modern `ExtraModuleNames`, fixing both alpha.18 compatibility preflight failures and classic synthetic-host emission.
- Adds initial historical GitDependencies handling for manifests without a root `BaseUrl`, and lets commits that predate `Engine/Build/Commit.gitdeps.xml` continue with a Git-only virtual Engine view. UE4.5 matrix jobs can overlay Epic's historical Required/Optional release ZIP assets before mounting. Alpha.20 extends this to the numeric/GUID pack layout observed in UE4.6-4.10.
- Broadens bundled .NET SDK/runtime discovery to historical Linux directory layouts instead of assuming the UE5.8 `<bundle>/<rid>` shape.
- Adds the legacy `Engine/Source/Programs/DotNETCommon` and early `EnvVarsToXML` sibling trees, the required `Engine/Binaries/DotNET` managed payload, and shared MSBuild props such as `UnrealEngine.csproj.props` to cold bootstrap seeds.
- Accepts comments/trailing commas in Engine-generated JSON metadata/runtimeconfig files and hardens FUSE unmount recovery after disk-full or interrupted builds.

### Added

- Adds `scripts/test-unreal-matrix-local.sh`: a bounded-disk local UE4.5-4.27 + UE5.0-5.8 sweep with a shared cache, per-version logs, subset selection, stale-mount recovery, and automatic workspace cleanup. It defaults outside `/tmp` and redirects `TMPDIR` to avoid tmpfs/quota exhaustion.
- Adds regression tests for legacy GitDependencies URL resolution, missing tracked Epic files, historical bundled-.NET layouts, tolerant runtime metadata, and the expanded UE4 managed seed.

### Changed

- Historical release resolution now pins the highest available patch tag even when that release has no standalone `Commit.gitdeps.xml` asset, preventing moving branch heads from changing a compatibility job.
- CLI version advanced to `0.5.0-alpha.19`.

## [0.5.0-alpha.18] - 2026-08-13

### Added

- Adds an exact-commit Unreal compatibility detector that reads `Engine/Build/Build.version` or historical `ENGINE_*_VERSION` macros from `Launch/Resources/Version.h`, then feature-detects the UBT `TargetRules`, `ModuleRules`, configuration and command-line surface instead of assuming the UE 5.8 API.
- Adds legacy UBT runtime support: UE4 MSBuild projects can use Epic-bundled Mono/xbuild payloads when present, system Mono as a fallback, and ready-to-run `Engine/Binaries/DotNET/UnrealBuildTool.exe` artifacts without recompiling old UBT projects unnecessarily.
- Generates version-compatible synthetic host rules. UE4 generations before the `ReadOnlyTargetRules`/`ExtraModuleNames` transition use `TargetInfo`, classic `SetupBinaries`, the legacy `ShouldCompileMonolithic()` override when required, and no modern target/configuration members; modern branches retain the alpha.17 lean modular host.
- Filters UBT command-line flags and `BuildConfiguration.xml` fields against capabilities present in the exact Engine source, keeping old parsers free of modern UE5-only switches.
- Adds legacy Linux toolchain discovery from Engine setup scripts plus a conservative UE4.20–4.27 native-toolchain mapping fallback, and exposes `LINUX_ROOT`/`LINUX_MULTIARCH_ROOT` to legacy UBT when an Epic toolchain projection is available, while allowing older branches without an official native bundle to reach their own compiler validation on the runner.
- Adds `scripts/prepare-compat-fixture.sh` and a 32-release GitHub Actions matrix covering stable Epic branches UE 4.5 through 4.27 and UE 5.0 through 5.8. Every matrix row performs a real FUSE plugin build and fails unless the package contains a native Linux `.so`.
- Adds per-release `cache-scope` isolation to the composite Action so compatibility jobs never restore UBT/profile state from a different Engine release family.
- Adds an optional `--manifest` / Action `manifest-path` override and a matrix helper that prefers an Epic release `Commit.gitdeps.xml` asset when one exists, pins the release tag that owns that asset, and otherwise falls back to the manifest tracked by the exact stable-branch commit.
- Adds regression tests for legacy Mono resolution, compatibility feature detection, legacy UBT discovery, classic UE4 host generation, legacy invocation filtering, and setup-script toolchain discovery.

### Changed

- Broadens the mounted fast seed and generated managed-artifact cache to preserve the legacy UE4 UBT/Core/Projects/Launch working set while retaining the exact-commit learned-profile fallback.
- CLI version advanced to `0.5.0-alpha.18`.
- Linux release compatibility is now the active validation target; Windows and macOS mounted backends remain the next platform ports after the release matrix is green.

## [0.5.0-alpha.17] - 2026-08-13

### Performance

- Optimizes the mounted backend specifically for ephemeral/cold CI runners instead of introducing a workspace-native object cache. Cold UBT Git backfill overlaps FUSE startup; once the view is live, UBT compilation, synthetic host generation, and Epic Linux toolchain acquisition run concurrently so independent bootstrap work overlaps on a fresh machine.
- Moves the authoritative extracted Linux toolchain from the disposable mounted workspace into the shared UECI cache at `toolchains/installed/linux-x64/<version>`. The Engine SDK path remains a lightweight projection, so deleting the Engine workspace no longer forces a local re-extraction when the shared cache survives or is restored by CI.
- Adds a native Linux archive extraction fast path (`tar`, with `pigz` when already installed) with a fully managed fallback, and increases sequential toolchain download/read buffers to 1 MiB. Mounted builds discard the redundant compressed toolchain archive after a verified persistent install.
- Stores `Commit.gitdeps.xml` and exact Git blob-size indexes by Epic commit, moves the blobless Epic metadata repository/lazy Git CAS into the shared cache, and begins profile lookup concurrently with manifest acquisition.
- Validates cached `Commit.gitdeps.xml` on load and automatically rematerializes it from the pinned Epic commit when persistent cache content is malformed.
- Extends the embedded Linux seed with the `HAL/PerModuleInline.inl` input learned by the successful alpha.14/alpha.16 modular smoke, reducing first-run dynamic fallback risk before an exact commit profile exists.
- Content-only plugins now bypass UBT compilation and native toolchain installation entirely on the mounted backend.
- `EnginePresentationMode.Auto` now selects the FUSE backend automatically for native `linux-x64 -> Linux` builds; the CLI default `--backend` is now `auto`.

### CI

- The composite GitHub Action builds UECI once, resolves the exact Epic object id with the new `ueci epic resolve` command, uses that immutable id in an `actions/cache` key, and passes the same object id to the actual bootstrap/build so a moving branch cannot race the cache key. A same-OS/architecture restore-key can seed a new Engine commit with shared CAS/toolchain state; a final prune keeps only the active commit's profiles/manifests/indexes/UBT artifacts/metadata before the new exact-key cache is saved. The cache contains cold-start state, never the disposable Engine workspace.
- Adds Action inputs for `backend` and `cache-enabled`, plus `engine-commit`, `cache-hit`, and `package-dir` outputs.
- Guards the Epic-derived cold cache from untrusted workflow contexts: fork pull requests and `pull_request_target` always skip cache restore/save.

### Observability

- Mounted plugin builds now report a phase timing breakdown for Engine metadata, artifact restore/save, UBT prefetch/compile/native build, FUSE mount startup, host generation, toolchain download/extraction/projection, native-product collection, packaging, and total wall-clock time. Overlapped phases remain visible individually.
- Splits raw missing-path probes from candidate immutable Engine profile misses. UBT's thousands of expected `.ueci`, HOME, generated-output and optional-SDK probes remain measurable without being presented as profile defects.

### Changed

- CLI version advanced to `0.5.0-alpha.17`.
- Linux cold-runner work is now the optimization baseline; the next mounted-backend targets are Windows and macOS.

## [0.5.0-alpha.16] - 2026-08-13

### Fixed

- Restores the missing `System.Text.Json` namespace import in the alpha.15 build-product collector regression test so `JsonDocument` and `JsonElement` compile under `Ueci.Tests`.

### Changed

- CLI version advanced to `0.5.0-alpha.16`.

## [0.5.0-alpha.15] - 2026-08-13

### Fixed

- Harvests modular native plugin build products emitted beside UECI's unique synthetic target and copies only binaries matching the requested plugin modules into `Plugins/<Name>/Binaries/<Platform>` before packaging. This addresses the alpha.14 smoke where UBT completed successfully but the packaged plugin contained only source files.
- Preserves matching `.modules` metadata while filtering out synthetic host/unrelated module entries, so the packaged plugin metadata only references binaries that are actually included.
- Fails inside `build-plugin` with the scanned output roots and observed native products when UBT reports success but no module binary can be located, instead of deferring the failure to the external smoke script.
- Filters fast-profile fallback diagnostics through parsed missing include/Engine paths, preventing thousands of harmless `.git`, `.ueci`, generated-output and optional-runtime probes from obscuring the real missing source path.

### Changed

- CLI version advanced to `0.5.0-alpha.15`.

## [0.5.0-alpha.14] - 2026-08-13

### Fixed

- Gives the synthetic modular Runtime/Game target a `TargetBuildEnvironment.Unique` environment. UE 5.8 rejects the lean UECI Game target when it shares UnrealGame build products while changing compile/plugin settings; a unique environment keeps those intentionally minimal settings isolated instead of overriding UnrealGame's shared environment.

### Changed

- CLI version advanced to `0.5.0-alpha.14`.

## [0.5.0-alpha.13] - 2026-08-13

### Fixed

- Builds Runtime plugin modules through a lean modular `TargetType.Game` host instead of `TargetType.Program`. Unreal Engine's `Runtime` module host type explicitly excludes Program targets; `RuntimeAndProgram` is the separate descriptor type that opts into Programs. This keeps validation faithful to ordinary Runtime plugins while retaining `-Module=<name>` modular outputs.
- Replaces mounted-build tail-only failure reporting with actionable diagnostic excerpts. Parallel UBT/UBA execution can report the failed action early and then continue independent compile actions, which previously pushed the real error out of the last 80 lines printed by UECI.
- Applies the same contextual failure excerpt to the non-mounted iterative builder.

### Changed

- CLI version advanced to `0.5.0-alpha.13`.

## [0.5.0-alpha.12] - 2026-08-13

### Fixed

- Switches the synthetic runtime `UECIHost` from `TargetLinkType.Monolithic` to `TargetLinkType.Modular`. The successful alpha.11 smoke proved that the monolithic target compiled and linked `UECIMinimal`, but folded it into the host executable, leaving the packaged plugin without the native module binary Unreal expects under `Binaries/<Platform>`.
- Re-enables module-targeted UBT invocations for the mounted backend now that the synthetic targets are modular. Runtime and Editor phases pass their exact plugin module list through `-Module=<Name>`, so UBT emits standalone plugin shared libraries instead of spending the final link on an irrelevant validation executable.
- Makes the synthetic Editor target explicitly modular as well, keeping module-output behavior deterministic for future Editor-only plugin validation.

### Performance

- Keeps the learned 5,612-Git-file / 81-GitDependencies profile unchanged. The alpha.11 real smoke skipped the global Engine index, completed the mounted UBT build in 119 seconds, downloaded 0 additional bytes, and reached packaging before the smoke correctly detected the missing native plugin binary.

### Changed

- CLI version advanced to `0.5.0-alpha.12`.

## [0.5.0-alpha.11] - 2026-08-13

### Fixed

- Corrects the UE 5.8 XML setting name from `bDisableDumpSYMs` to the case-sensitive documented `bDisableDumpSyms`. Alpha.10 wrote the wrong property casing, so UBT silently kept `dump_syms` in the Linux post-link script.
- Adds the documented `-NoDumpSyms` UBT command-line guard to synthetic plugin validation builds as a second, scope-independent protection. The XML policy remains in Engine, Project, and isolated HOME configuration scopes.

### Performance

- Keeps the persisted 5,612-Git-file / 81-GitDependencies fast profile unchanged. The alpha.10 warm smoke still reached the final link in about 1m44s wall-clock with UHT at ~11 ms; alpha.11 only removes the unnecessary symbol-extraction post-link dependency.

### Changed

- CLI version advanced to `0.5.0-alpha.11`.

## [0.5.0-alpha.10] - 2026-08-13

### Fixed

- Disables Linux `dump_syms` at the hermetic UnrealBuildTool configuration layer with `bDisableDumpSYMs=true`. The real alpha.9 smoke showed that target-level `bAllowRuntimeSymbolFiles=false` alone still left `../Binaries/Linux/dump_syms` in `Link-UECIHost.sh`; the BuildConfiguration switch directly disables that post-link tool invocation for UECI's synthetic validation builds.
- Applies the same symbol-extraction policy to all three UBT configuration scopes used by mounted builds (Engine, synthetic Project, and isolated UECI HOME), preventing a user/global config from re-enabling the post-link dependency. `bAllowRuntimeSymbolFiles=false` remains on the lean runtime target as an additional target-level guard.

### Performance

- Keeps the learned 5,612-Git-file / 81-GitDependencies fast profile unchanged. The alpha.9 warm smoke reached the final link in about 1m44s wall-clock while skipping the global Engine tree/size index.

### Changed

- CLI version advanced to `0.5.0-alpha.10`.

## [0.5.0-alpha.9] - 2026-08-13

### Fixed

- Disables runtime symbol-file generation on the synthetic Linux `UECIHost` Program target. Unreal Build Tool otherwise appends `dump_syms` to the post-link script; the alpha.8 smoke completed the native link but failed with exit code 127 because the deliberately minimal mounted Engine does not materialize `Engine/Binaries/Linux/dump_syms`. The validation host is never executed or distributed, so runtime crash-symbol generation provides no value here.
- Keeps the fix scoped to the synthetic runtime validation target; plugin compilation/linking, normal debug information, and Editor validation behavior are unchanged.

### Performance

- Retains the persisted 5,612-Git-file / 81-GitDependencies commit profile learned by alpha.7/alpha.8. The alpha.8 warm smoke reached the final post-link step in about 1m42s wall-clock without rebuilding the complete 408k-path Engine namespace.

### Changed

- CLI version advanced to `0.5.0-alpha.9`.

## [0.5.0-alpha.8] - 2026-08-12

### Fixed

- Completes the lean synthetic `TargetType.Program` link without pulling the full Engine `Launch` module back into the runtime validation target. `UECIHost` now provides the three process-level symbols observed missing in the real alpha.7 smoke (`main`, `GInternalProjectName`, and `GForeignEngineDir`) behind a Program-only compile definition, so the same host module remains safe inside Editor targets.
- Explicitly adds each plugin module to the generated target `ExtraModuleNames`. Runtime validation includes runtime modules; Editor validation includes runtime + editor modules, making the target build itself authoritative instead of relying only on plugin enablement side effects.
- Tightens fast-profile fallback classification. Genuine native linker errors no longer trigger a complete dynamic Engine retry because unrelated UBA probe text happens to contain `No such file or directory`; full-Engine fallback is reserved for explicit missing Engine/module/target/materialization diagnostics.

### Performance

- Preserves the exact learned alpha.7 profile across the upgrade. A warm commit profile can therefore go straight back to the ~5.6k-file working set instead of repeating the 408k-path discovery pass. Engine Rules assemblies are rebuilt against the active profile instead of being reused across different profile shapes; the alpha.7 run measured this at only ~3.8 seconds total.

### Changed

- CLI version advanced to `0.5.0-alpha.8`.

## [0.5.0-alpha.7] - 2026-08-12

### Performance

- Adds commit-scoped minimal mounted-Engine profiles. Known Epic commits restore the exact Git OID/mode/size working set and GitDependencies subset directly from the shared UECI cache, skipping the global ~224k-file `git ls-tree`, GitHub blob-size crawl, and ~409k-path namespace build.
- Seeds unknown commits from the observed alpha.6 Linux x64 UBT/UHT working set (2,215 cold Git blobs plus the stable UBT managed-source prefixes) and uses path-limited `git backfill` before constructing the small namespace.
- Learns a pruned profile from files UBT actually stats/opens; unrelated `readdir` siblings are intentionally not retained, so later rules/UHT scans cannot rediscover the complete Engine by accident.
- Adds a commit-scoped generated-artifact cache for UBT managed build outputs, shared project outputs, and Engine Rules assemblies. Warm runs restore these into the COW upper before the mount and reuse an already complete `UnrealBuildTool.dll` output when possible.
- Stores learned Engine profiles and generated-artifact snapshots under the normal shared UECI cache so fresh CI workspaces can benefit when that cache is restored.

### Reliability

- Unknown/incomplete fast profiles automatically retry once through the complete dynamic Engine index when UECI or UBT reports a missing source/module/target requirement; the fallback run updates the exact commit profile for subsequent jobs.
- Generated upper artifacts are commit-bound and discarded when the Epic commit changes, preventing stale UBT/Rules binaries from leaking across refs.
- The generic `ueci mount` command keeps its complete Engine namespace; minimal profiles are enabled only by the mounted plugin-build backend.

### Fixed

- Mounted plugin targets no longer pass `-Module=<plugin>` to the monolithic synthetic Program host. Alpha.6 reached a valid target action graph and then failed in `BuildMode.GatherOutputItems()` with `Unable to find output items for module`; mounted builds now build the synthetic target once and let its enabled plugin modules participate normally.

### Changed

- CLI version advanced to `0.5.0-alpha.7`.

## [0.5.0-alpha.6] - 2026-08-12

### Performance

- Reuses one Unix-domain socket per native FUSE worker instead of `socket/connect/accept/close` for every metadata request, and buffers multi-entry `LIST` responses instead of flushing once per directory entry.
- Enables 60-second kernel attribute/entry caching and cached directory enumeration; `readdir` now supplies complete child attributes so the kernel can prefill inode metadata.
- Precomputes immutable lower-directory entry arrays and returns them directly when no COW upper/whiteout merge is necessary.
- `--vfs-verbose` now aggregates high-volume STAT/LIST/read-open traffic while keeping cold CAS fills and mutations individually visible; the mounted plugin smoke enables this optimized verbosity by default.
- Periodic FUSE summaries now report request throughput, connection count, and directory-entry volume; the mounted smoke also prints end-to-end command elapsed time for direct before/after comparisons.
- Reuses a persistent `git cat-file --batch` process for lazy Git CAS fills instead of spawning one Git process per blob.
- Batch-prefetches the known UBT managed-source seed with path-limited `git backfill` only when the Epic metadata repository is verified as a one-commit shallow snapshot; reused/deep repositories stay fully lazy to avoid historical overfetch.
- Parallelizes uncached GitHub Git-tree size metadata requests across independent subtrees.

### Fixed

- Preserves unknown Git blob size as `-1` internally instead of silently converting it to zero, allowing READDIRPLUS to avoid caching false EOF metadata on non-GitHub fallbacks.
- Disposes the persistent Git batch process with the virtual Engine context.

### Changed

- CLI version advanced to `0.5.0-alpha.6`.

## [0.5.0-alpha.5] - 2026-08-12

### Fixed

- Mounted Epic builds no longer hydrate Git blobs merely because UBT/MSBuild calls `stat(2)`. UECI now enriches the metadata-only local Git tree with exact blob lengths from GitHub's Git Trees API, which returns blob `sha` + `size` without blob contents.
- GitHub's recursive tree response can truncate above its documented limits; UECI detects `truncated`, recursively splits only those oversized subtrees, and merges exact sizes by blob SHA.
- The exact-size index is cached by Epic commit under VFS state, so subsequent mounts do not repeat GitHub metadata requests.
- The previous targeted-stat hydration remains only as a correctness fallback for non-GitHub repositories or incomplete metadata.

### Changed

- VFS startup now runs three metadata jobs in parallel: local `git ls-tree` path/object indexing, GitHub blob-size metadata, and `Commit.gitdeps.xml` parsing.
- CLI version advanced to `0.5.0-alpha.5`.

## [0.5.0-alpha.4] - 2026-08-12

### Added

- Adds `build-plugin --backend fuse` for the first end-to-end Linux x64 mounted plugin build path. It prepares a private virtual Engine, compiles UnrealBuildTool through FUSE using Epic's bundled .NET SDK, installs the native Linux toolchain, runs each plugin target once, packages the result, and unmounts automatically.
- Adds a reusable `LinuxFuseMountSession` so callers can start a mount, run arbitrary build processes against it, and reliably unmount/stop the protocol server through async disposal.
- Adds `scripts/smoke-plugin-vfs.sh` as the real minimal-plugin compile gate for the mounted backend.
- Adds mounted-build I/O metrics for hydrated Git blobs/bytes and GitDependencies downloads.

### Changed

- The synthetic plugin host can live outside the Engine root; mounted builds keep project/plugin build outputs on the normal host filesystem while only Engine writes use COW.
- The Linux toolchain installer can use an explicit persistent store outside the Engine/mount and project it into the virtual Engine.
- The embedded FUSE helper now forwards `fallocate`, `copy_file_range`, and `lseek` to backing file descriptors for build-tool compatibility. libfuse documents local locking as kernel-managed when lock/flock callbacks are absent.
- CLI version advanced to `0.5.0-alpha.4`.

### Compatibility

- `materialized` remains the default `build-plugin` backend. FUSE is opt-in (`--backend fuse`) and currently Linux x64 only, preserving hosted-CI portability.

## [0.5.0-alpha.3] - 2026-08-12

### Fixed

- FUSE now returns an exact POSIX `st_size` for lazily indexed Git files. Git tree objects do not contain blob sizes, so a targeted `stat(2)` hydrates only that single blob into CAS instead of reporting a fake zero length that makes the kernel return EOF before `read()`.
- `readdir` remains metadata-only: listing the virtual Engine still does not hydrate source contents.
- The VFS smoke test now verifies non-zero `stat` size, cold content read, and warm CAS read separately.

## [0.5.0-alpha.2] - 2026-08-12

### Fixed

- The VFS Git-tree index no longer runs `git ls-tree --long`. In a `blob:none` partial clone, requesting every blob size can force Git to hydrate missing blobs and turn a metadata-only mount bootstrap into a massive source download. Git source sizes are now deferred until the file is actually opened and cached.
- Git-backed `stat` metadata upgrades from an unknown/zero size to the real CAS file size after first open. GitDependencies files keep exact manifest sizes from startup.
- The real VFS smoke no longer kills a healthy startup after 30 seconds; it waits up to 10 minutes by default, prints a five-second heartbeat, and reports the mount process exit code if startup fails.

### Added

- Streaming Git-tree progress every 25k blobs with path rate, metadata bytes, managed-memory estimate, and elapsed time.
- GitDependencies manifest progress, virtual namespace merge progress, and explicit READY timing in the smoke test.
- `ueci mount --verbose` request tracing for FUSE `STAT`, `LIST`, `RESOLVE`, write/COW, rename and link operations. The VFS smoke enables it by default (`UECI_VFS_VERBOSE=0` disables request tracing).
- Regression coverage for deferred Git blob sizes and size promotion after CAS materialization.

## [0.5.0-alpha.1] - 2026-08-12

### Added

- Adds `ueci mount <mountpoint>` as the first real mounted Engine backend on Linux using FUSE3.
- Builds a complete virtual namespace from a metadata-only Epic Git tree index plus `Commit.gitdeps.xml`; `stat`/`readdir` do not check out Engine source contents.
- Adds lazy immutable content providers for Epic Git blobs and verified GitDependencies blobs. The first file open may block while its content enters the shared CAS; warm opens reuse the local backing blob.
- Adds a persistent writable copy-on-write upper layer with whiteouts, file copy-up, generated directories/files, symlinks, mode changes, deletes and file renames.
- Adds an embedded native libfuse3 helper that forwards metadata/path-resolution requests to Ueci.Core over a Unix-domain socket and serves opened files through real backing descriptors.
- Adds `scripts/smoke-vfs.sh` for an opt-in real Linux mount/read/write smoke test.

### Changed

- Extends `GitDependenciesMaterializer` with a CAS-only `EnsureBlobAsync` path so VFS reads do not require a throwaway materialized output file.
- Keeps the v0.4 materialized plugin-build discovery loop as the portable fallback while mounted-mode plugin orchestration is integrated in a later v0.5 alpha.
- CLI version advanced to `0.5.0-alpha.1`.

### Testing

- Adds an offline virtual-engine test that overlays GitDependencies over a local blobless Git fixture, validates lazy Git/GitDependencies reads, verifies CAS reuse, exercises copy-up/whiteout/recreate semantics, and checks merged directory enumeration without requiring FUSE.

## [0.4.0-alpha.11] - 2026-08-12

### Fixed

- Fixes a real lazy-discovery stall observed on `CorePreciseFP`: a module could already be present in the sparse worktree because of speculative Build.cs prefetch while UBT's generated `UE5Rules` assembly still lacked that module definition.
- Explicit UBT missing-module diagnostics now force-refresh the selected `*.Build.cs` directly from the pinned Epic commit even when its directory is already sparse.
- Invalidates `Engine/Intermediate/Build/BuildRules` after an explicit module-rule refresh so the next UBT process rebuilds Engine rules from the authoritative source set.

### Testing

- Adds an offline partial-Git regression test that preloads `CorePreciseFP`, corrupts its local rule file and creates a stale rules cache, then verifies that explicit requirement materialization restores the authoritative rule and removes the generated cache.

### Changed

- CLI version advanced to `0.4.0-alpha.11`.

## [0.4.0-alpha.10] - 2026-08-12

### Fixed

- Replaces the synthetic Runtime host's `TargetType.Game` with a lean standalone `TargetType.Program`. The real alpha.9 smoke reached 16 passes because the Game target pulled unrelated `Launch -> SessionServices -> TargetPlatform -> TextureFormat` developer-tool rules into a Core-only plugin build.
- Disables implicit Engine/CoreUObject/ApplicationCore, developer tools, target/shader formats, Slate, ICU, trace, and enabled-by-default Engine plugins for the Runtime host while explicitly retaining plugin support. UBT remains free to report additional requirements for plugins that genuinely need broader surfaces.
- Parses UBT's `Library '<path>' was not resolvable to a file` diagnostics and routes those exact/suffix paths through the GitDependencies resolver. This covers the BLAKE3, Oodle, zlib, jemalloc, and ICU-style diagnostics observed in the real Linux smoke.

### Changed

- Adds a bounded two-level prefetch of standard `.Build.cs` module dependency lists (`Public/PrivateDependencyModuleNames`, include-path module lists, dynamically-loaded modules). This is an optimization only; UBT still evaluates the real C# rules and remains the build authority.
- Refuses prefetch hints whose module subtree exceeds 1,500 tracked files, preventing heuristic discovery from accidentally materializing another very large Engine subtree.
- Raises the plugin discovery safety ceiling from 16 to 32 passes for genuinely deep graphs and prints each newly discovered requirement in the progress log.
- Runtime-only smoke seeds no longer include `Engine/Source/Runtime/Launch`; Editor plugins still add Launch when needed.
- CLI version advanced to `0.4.0-alpha.10`.

### Testing

- Adds offline tests for the lean Program target, unresolved-library diagnostics, module dependency hint parsing, and tracked-subtree sizing.

## [0.4.0-alpha.9] - 2026-08-12

### Fixed

- Move the authoritative native Linux toolchain payload out of the Git-managed `Engine/` subtree into `.ueci/toolchains/linux-x64/<version>`, preventing later sparse-checkout expansions from deleting clang/sysroot contents.
- Project the persistent toolchain store into Epic's expected `Engine/Extras/ThirdPartyNotUE/SDKs/HostLinux/Linux_x64/<version>` location and recreate that projection immediately after sparse source expansion.
- Migrate usable in-tree toolchains from alpha.6-alpha.8 into the persistent store when possible, avoiding an unnecessary re-download on warm working sets.
- Restore an existing persistent toolchain projection after the initial native sparse seed before the first plugin UBT pass.

### Testing

- Replace the sparse-cone protection regression with an offline test that expands a partial Git worktree, removes the Engine-side SDK projection, and restores it from `.ueci/toolchains` without a second archive download.
- CLI version advanced to `0.4.0-alpha.9`.

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
