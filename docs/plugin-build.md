# Lazy plugin build

`ueci build-plugin` is the v0.4 experimental end-to-end path from an authorized Epic source ref to a packaged code plugin without installing a full Unreal Engine tree.

## Why UBT drives discovery

UECI deliberately does **not** treat `.Build.cs` as a declarative source of truth. UnrealBuildTool executes the rules and remains the authority for which modules, headers, libraries, generated tools, and platform SDK inputs are required. UECI may conservatively inspect standard dependency-list mutations after a rule file has already been materialized, solely to prefetch a few small likely module directories before the next UBT pass.

The v0.4 builder therefore uses a retry loop:

```text
.uplugin
   │
   ▼
ephemeral UECIHost project
   │
   ▼
real UnrealBuildTool
   │
   ├── success ───────────────► package plugin
   │
   └── failure diagnostics
            │
            ▼
       requirement parser
            │
      ┌─────┴─────────┐
      │               │
 Epic Git source   GitDependencies
      │               │
      └─────┬─────────┘
            ▼
          retry UBT
```

The first native Runtime source seed is intentionally small: `Core`, `TraceLog`, and `Projects`, plus the existing UBT/shared managed seed and the tiny host-platform SDK config needed for discovery. `Launch` is added only when an Editor phase is required. If UBT reports a missing module, UECI finds that module's tracked `.Build.cs` in the pinned Epic commit and expands the sparse checkout to that module directory. After that explicit requirement, up to two bounded rounds of standard dependency-list hints can prefetch small module directories; hints over 1,500 tracked files are refused until UBT explicitly asks for them. Missing Engine paths and unresolved-library diagnostics are resolved against Git alongside `Commit.gitdeps.xml`; missing dependency payloads are materialized through the verified blob cache. On native Linux x86_64, a platform-SDK diagnostic is handled separately from GitDependencies: UECI reads `Engine/Config/Linux/Linux_SDK.json`, uses its `MainVersion`, downloads Epic's `native-linux-<MainVersion>.tar.gz`, stores the extracted toolchain persistently under `.ueci/toolchains/`, and projects it into the Engine SDK path expected by UBT. The large archive is only fetched after UBT proves it is needed.

Because the Epic Git worktree and GitDependencies payloads overlap in one physical Engine directory during the materialized backend phase, each sparse Git expansion is followed by an overlay integrity check. UECI tracks every GitDependencies file it has materialized; if sparse checkout displaced one, only the missing paths are copied back from the CAS (or fetched from the CDN on a genuine cache miss) before the next UBT pass.

## Host project

The source plugin is copied into `.ueci/plugin-work/<PluginName>/Plugins/<PluginName>`. Stale generated platform `Binaries`, `Intermediate`, `Saved`, `.git`, and `.ueci` state is intentionally not copied. `Binaries/ThirdParty` is preserved because many plugins legitimately ship vendor runtime payloads there.

UECI generates a lean `UECIHost` Program target for Runtime modules and a `UECIHostEditor` Editor target when editor modules exist. The Program target disables implicit Engine/CoreUObject/ApplicationCore, developer tools, target/shader formats, Slate, ICU, tracing, and enabled-by-default Engine plugins while explicitly retaining plugin support. This avoids dragging a Core-only plugin through Game-target `Launch`/TargetPlatform tooling. UBT is passed `-Module=<Name>` for each plugin module so the experiment does not intentionally request a full host target build.

The `.uplugin` descriptor is used to enumerate module names/types and create the host project. Rule semantics remain inside UBT; the Build.cs hint parser is only a bounded latency optimization.

## CLI

```bash
export UECI_EPIC_GITHUB_TOKEN='...'

dotnet run --project src/Ueci.Cli -- \
  build-plugin ./MyPlugin/MyPlugin.uplugin \
  --engine-dir /tmp/ueci-engine \
  --out /tmp/ueci-package \
  --ref release \
  --no-pack-cache
```

Useful controls:

- `--platform Linux|Win64|Mac` overrides the target platform; by default it follows the host RID.
- `--configuration Development` selects the UBT configuration.
- `--max-discovery-passes N` bounds the lazy retry loop (default 32, max 64).
- normal GitDependencies cache flags remain available.

Each UBT pass is saved under `.ueci/plugin-work/<PluginName>/Logs`. If discovery stalls, UECI reports the full log path and the last diagnostic lines rather than silently downloading broad Engine subtrees.

## Packaging

On success, UECI copies the built plugin into the requested output directory, keeping `Binaries` and source/content/config resources while excluding `Intermediate`, `Saved`, `.git`, and `.ueci`. A sibling `ueci-build.json` records the pinned Epic commit, target platform/configuration, built modules, number of discovery passes, and UECI-observed download bytes.

## Testing model

The normal `./scripts/test.sh` suite is still offline and token-free. It tests descriptor parsing, host generation, diagnostic-to-requirement extraction, tracked-file lookup, UBT argument generation, and packaging with temporary local files.

The networked integration test is explicit:

```bash
export UECI_EPIC_GITHUB_TOKEN='...'
./scripts/smoke-plugin.sh
```

By default it builds `fixtures/MinimalPlugin/UECIMinimal.uplugin`, a single Runtime module depending only on `Core`. It runs on an ordinary development machine; GitHub Actions is not required.

## Current alpha limitations

- The retry parser is intentionally conservative. A new UBT diagnostic shape can stop discovery instead of guessing a huge subtree; Build.cs prefetch hints are capped and never replace UBT evaluation.
- Program-only plugin modules are rejected for now.
- Runtime and editor phases are implemented, but the first real validation target is the minimal Runtime fixture on Linux. Native Linux toolchain installation is implemented lazily; the end-to-end plugin compile still needs real-Epic smoke validation.
- UAT `BuildPlugin` is not used yet because it pulls a much broader automation surface. Once the minimal UBT path is proven, UECI can add an optional UAT-compatible packaging mode.
- Cross-compiling to a platform different from the host is not the v0.4 goal; Windows and macOS are expected to use their native hosted/self-hosted environments.
## Executor diagnostics

Some UE 5.8 source builds pre-create Unreal Build Accelerator before platform/toolchain validation even when CI-oriented XML configuration asks for local executors. UECI therefore resolves `Engine/Source/Programs/Shared/EpicGames.UBA/Library.props` together with the host-specific `Engine/Binaries/*/UnrealBuildAccelerator` payload during the UBT bootstrap, before `UnrealBuildTool.csproj` is compiled. This matters on a warm cache because adding the native UBA payload after UBT was already built may leave the managed UBA integration unavailable. Missing-UBA diagnostics are still handled as a compatibility fallback; that fallback recompiles UBT after materializing UBA. The full UBT log at `Engine/Programs/UnrealBuildTool/Log.txt` is merged with stdout/stderr before requirement parsing, because that log can contain the Linux SDK requirement that the short console error omits.


## Mounted backend (v0.5 alpha.9)

On Linux x64, `--backend fuse` bypasses the materialized discovery loop. Build mode first exposes a commit-scoped minimal Engine profile (or the embedded alpha.6 working-set seed); if that working set proves incomplete, UECI retries once with the complete virtual namespace and learns the additional lower paths for the next run. The generic `ueci mount` command still exposes every tracked Epic Git/GitDependencies path. UBT itself is compiled inside the mount with Epic's bundled .NET SDK. The synthetic project and plugin copy remain outside the mount, while Engine-generated outputs use the persistent COW upper.


Alpha.6 keeps the FUSE protocol connection persistent per worker, enables kernel/readdir metadata caching, precomputes immutable lower directory listings, reuses `git cat-file --batch` for lazy source content and safely batch-prefetches the known UBT managed-source seed only when the Epic metadata repository is verified as a one-commit shallow snapshot.

Alpha.7 adds a second layer: the mounted **build** backend no longer has to expose the complete Engine after a commit has been learned. A commit profile stores exact Git entries plus only the GitDependencies files observed by the build. Profiles live under the shared UECI cache rather than the ephemeral build workspace. Unknown commits first use an embedded Linux x64 seed derived from the alpha.6 smoke run; a missing module/source requirement triggers one complete dynamic retry, and that retry writes the exact profile used by following jobs. `ueci mount` itself is unchanged and still exposes the complete namespace.

Alpha.8 keeps that profile model and fixes the final Program-host link exposed by the first real alpha.7 run. `UECIHost` is still intentionally independent from Engine `Launch`; for the Linux runtime validation target it provides a tiny never-executed `main` plus the two Core globals normally owned by Launch. Plugin modules are emitted directly into `ExtraModuleNames`, while linker failures are excluded from the profile-fallback heuristic so an ordinary native error cannot cause a second full-Engine pass.

Alpha.9 keeps the synthetic host lean through the post-link stage by setting `bAllowRuntimeSymbolFiles = false` on `UECIHost`. Runtime symbol extraction is not useful for UECI's never-executed validation binary, and disabling it prevents Linux UBT from requiring the standalone `dump_syms` tool solely to finish the smoke build.

The profile intentionally learns files that UBT stats/opens, not every sibling returned by `readdir`. This is what prevents EngineRules/UHT from rediscovering unrelated modules on the next run.

Generated managed UBT/shared-project outputs are cached by Epic commit and restored into the COW upper before mounting. Engine Rules assemblies are deliberately profile-sensitive and are rebuilt against the active namespace on each mounted plugin build; this avoids restoring a `UE5Rules.dll` created from a smaller seed after the commit profile expands.

The Linux native toolchain is not part of Git/GitDependencies, so UECI proactively installs it before invoking the plugin target. Its authoritative payload is stored outside the FUSE mount and exposed to UBT by the normal `Engine/Extras/ThirdPartyNotUE/SDKs/...` projection.

Use `./scripts/smoke-plugin-vfs.sh` for the first real build gate. The smoke enables aggregated VFS verbosity by default; set `UECI_VFS_VERBOSE=0` to silence those summaries. The materialized backend remains available as `--backend materialized`.
