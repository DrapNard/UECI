# Lazy plugin build

`ueci build-plugin` is the v0.4 experimental end-to-end path from an authorized Epic source ref to a packaged code plugin without installing a full Unreal Engine tree.

## Why UBT drives discovery

UECI deliberately does **not** interpret `.Build.cs` as a declarative dependency file. UnrealBuildTool executes the rules and remains the authority for which modules, headers, libraries, generated tools, and platform SDK inputs are required.

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

The first native source seed is intentionally small: `Core`, `TraceLog`, `Projects`, and `Launch`, plus the existing UBT/shared managed seed and the tiny host-platform SDK config needed for discovery. If UBT reports a missing module, UECI finds that module's tracked `.Build.cs` in the pinned Epic commit and expands the sparse checkout to that module directory. Missing Engine paths are resolved against Git first/alongside `Commit.gitdeps.xml`; missing dependency payloads are materialized through the verified blob cache. On native Linux x86_64, a platform-SDK diagnostic is handled separately from GitDependencies: UECI reads `Engine/Config/Linux/Linux_SDK.json`, uses its `MainVersion`, downloads Epic's `native-linux-<MainVersion>.tar.gz`, and extracts the toolchain into `Engine/Extras/ThirdPartyNotUE/SDKs/HostLinux/Linux_x64/`. The large archive is only fetched after UBT proves it is needed.

Because the Epic Git worktree and GitDependencies payloads overlap in one physical Engine directory during the materialized backend phase, each sparse Git expansion is followed by an overlay integrity check. UECI tracks every GitDependencies file it has materialized; if sparse checkout displaced one, only the missing paths are copied back from the CAS (or fetched from the CDN on a genuine cache miss) before the next UBT pass.

## Host project

The source plugin is copied into `.ueci/plugin-work/<PluginName>/Plugins/<PluginName>`. Stale generated platform `Binaries`, `Intermediate`, `Saved`, `.git`, and `.ueci` state is intentionally not copied. `Binaries/ThirdParty` is preserved because many plugins legitimately ship vendor runtime payloads there.

UECI generates both a `UECIHost` Game target and a `UECIHostEditor` Editor target. Runtime modules are built through the Game target; editor/developer modules are built through the Editor target. UBT is passed `-Module=<Name>` for each plugin module so the experiment does not intentionally request a full host target build.

The `.uplugin` descriptor is only used to enumerate module names/types and create the host project. Rule semantics remain inside UBT.

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
- `--max-discovery-passes N` bounds the lazy retry loop (default 16, max 64).
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

- The retry parser is intentionally conservative. A new UBT diagnostic shape can stop discovery instead of guessing a huge subtree.
- Program-only plugin modules are rejected for now.
- Runtime and editor phases are implemented, but the first real validation target is the minimal Runtime fixture on Linux. Native Linux toolchain installation is implemented lazily; the end-to-end plugin compile still needs real-Epic smoke validation.
- UAT `BuildPlugin` is not used yet because it pulls a much broader automation surface. Once the minimal UBT path is proven, UECI can add an optional UAT-compatible packaging mode.
- Cross-compiling to a platform different from the host is not the v0.4 goal; Windows and macOS are expected to use their native hosted/self-hosted environments.
