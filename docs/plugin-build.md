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


## Mounted backend (v0.5 alpha.4)

On Linux x64, `--backend fuse` bypasses the materialized discovery loop. The virtual namespace already contains every tracked Epic Git path and every GitDependencies file, so missing module/header/library discovery happens naturally through filesystem access while one UBT process remains alive. UBT itself is compiled inside the mount with Epic's bundled .NET SDK. The synthetic project and plugin copy remain outside the mount, while Engine-generated outputs use the persistent COW upper.

The Linux native toolchain is not part of Git/GitDependencies, so UECI proactively installs it before invoking the plugin target. Its authoritative payload is stored outside the FUSE mount and exposed to UBT by the normal `Engine/Extras/ThirdPartyNotUE/SDKs/...` projection.

Use `./scripts/smoke-plugin-vfs.sh` for the first real build gate. The materialized backend remains available as `--backend materialized`.
