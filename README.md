# UECI

**UECI is an experimental, minimal Unreal Engine substrate for CI/CD.** Its goal is to build Unreal Engine code plugins without installing a full Unreal Engine tree on every runner.

> Status: **v0.4 alpha / technical prototype.** Authenticated Epic source bootstrap, real GitDependencies CDN materialization, UnrealBuildTool bootstrap, and an experimental lazy code-plugin build loop now exist. Mounted VFS backends and broad cross-platform plugin validation are still roadmap items.

## Why

A normal Unreal source setup materializes a very large source/dependency tree before a plugin build starts. UECI instead treats Unreal Engine as two lazy providers:

```text
                 Unreal Engine view
                        │
          ┌─────────────┴─────────────┐
          │                           │
  Epic Git source             GitDependencies
  blobless partial clone      Commit.gitdeps.xml
          │                           │
          └─────────────┬─────────────┘
                        │
              materialize or mount
                        │
                       UBT
                        │
                    plugin build
```

The long-term design supports both **materialized mode** (portable, no special privileges) and **mounted mode** (FUSE on Linux, WinFsp on Windows, an appropriate macOS backend) behind the same engine-view contract.

## What v0.4 already does

- Streams huge `Commit.gitdeps.xml` files without loading the whole XML document.
- Builds deterministic `path → blob → pack` indexes.
- Resolves the exact CDN pack URL, pack offset, and file size for a manifest path.
- Plans subsets by exact path or directory prefix and deduplicates blobs/packs.
- Validates all file→blob and blob→pack references.
- Authenticates to `EpicGames/UnrealEngine` using a **read-only token supplied only through the environment**.
- Initializes an Epic source repository with `git fetch --filter=blob:none` and no full checkout.
- Materializes an individual Git blob on demand.
- Materializes selected tracked Epic Git subtrees into the blobless source store without checking out the full engine.
- Uses `git backfill` when available to batch missing partial-clone blobs before checkout, avoiding pathological one-blob-at-a-time promisor fetches.
- Downloads Epic GitDependencies packs directly from the manifest-generated CDN URL.
- Supports the validated gzip + `UEPACK00` pack layout and absolute decompressed `PackOffset` semantics.
- Groups requested blobs by pack and extracts them in one forward gzip pass.
- Verifies every extracted blob against its manifest SHA-1 before it enters the cache.
- Keeps separate compressed-pack and verified-blob caches, with `--no-pack-cache` for disk-constrained runners.
- Restores executable bits from `IsExecutable` on Unix hosts.
- Rejects output-root path traversal during batch materialization.
- Resolves and materializes Epic's bundled .NET **SDK** for the current host directly from `Commit.gitdeps.xml`.
- Materializes the UBT + shared C# source seed, compiles `UnrealBuildTool.csproj`, validates the generated runtime config, then probes UBT with the same Epic-bundled `dotnet`.
- Provides `ubt run` to forward arbitrary arguments to the bootstrapped UBT.
- Parses `.uplugin` module descriptors without interpreting `.Build.cs` semantics.
- Creates an ephemeral project with Game/Editor targets and copies the plugin without stale build outputs.
- Invokes the real UBT with `-Module=<PluginModule>` so plugin rules remain authoritative.
- Converts missing-module/path/SDK diagnostics into bounded lazy Epic Git/GitDependencies materialization passes.
- On native Linux x86_64, resolves `Linux_SDK.json` and lazily downloads/extracts Epic's matching native clang/sysroot toolchain only when UBT reports that the Linux SDK is missing.
- Packages the built plugin with `Binaries` plus a machine-readable `ueci-build.json` report.
- Ships a minimal Runtime plugin fixture and an opt-in real plugin smoke test that runs on a normal workstation.
- Provides a future-proof VFS contract without making FUSE/WinFsp mandatory.
- Has dependency-free local tests: no GitHub runner and no Epic token are required.

## Requirements

For development/tests:

- .NET SDK 8.x
- Git 2.x (Git 2.49+ strongly recommended for fast blobless source materialization via `git backfill`)
- Linux, Windows, or macOS

Epic integration additionally requires a GitHub account with access to the private `EpicGames/UnrealEngine` repository and a read-only token for that account.

## Local development

```bash
git clone <your-fork>
cd ueci
./scripts/test.sh
```

PowerShell:

```powershell
./scripts/test.ps1
```

The tests use a tiny synthetic GitDependencies fixture. They do **not** require Unreal Engine, Epic credentials, Docker, FUSE, or a CI runner.

To additionally smoke-test a real Epic manifest:

```bash
UECI_REAL_MANIFEST=/path/to/Commit.gitdeps.xml ./scripts/test.sh
```

## Inspect GitDependencies

```bash
dotnet run --project src/Ueci.Cli -- \
  gitdeps inspect /path/to/Commit.gitdeps.xml
```

Resolve one file:

```bash
dotnet run --project src/Ueci.Cli -- \
  gitdeps lookup /path/to/Commit.gitdeps.xml \
  Engine/Binaries/DotNET/AgentInterface.dll
```

Estimate the packs needed for a subtree:

```bash
dotnet run --project src/Ueci.Cli -- \
  gitdeps plan /path/to/Commit.gitdeps.xml \
  --prefix Engine/Binaries/DotNET \
  --prefix Engine/Source/Runtime/Core
```

Fetch one real dependency file directly from Epic's CDN:

```bash
dotnet run --project src/Ueci.Cli -- \
  gitdeps fetch /path/to/Commit.gitdeps.xml \
  Engine/Binaries/Linux/UnrealVersionSelector-Linux-Shipping \
  --out /tmp/UnrealVersionSelector-Linux-Shipping
```

Materialize a planned subtree while deduplicating shared blobs/packs:

```bash
dotnet run --project src/Ueci.Cli -- \
  gitdeps materialize /path/to/Commit.gitdeps.xml \
  --root .ueci/materialized \
  --prefix Engine/Binaries/DotNET \
  --prefix Engine/Source/Runtime/Core
```

By default compressed packs and verified blobs are cached. On a disk-constrained ephemeral machine, keep only verified blobs:

```bash
dotnet run --project src/Ueci.Cli -- \
  gitdeps fetch /path/to/Commit.gitdeps.xml \
  Engine/Binaries/Linux/UnrealVersionSelector-Linux-Shipping \
  --out /tmp/UnrealVersionSelector-Linux-Shipping \
  --no-pack-cache
```

Override the cache with `--cache-dir PATH` or `UECI_CACHE_DIR`. Batch materialization defaults to two concurrent packs and accepts `--max-concurrent-packs N` (1–32).

## Epic GitHub access

Never put the token in `.ueci.yml`, a remote URL, or a command-line argument.

```bash
export UECI_EPIC_GITHUB_TOKEN='github_pat_...'

dotnet run --project src/Ueci.Cli -- epic probe --ref release
```

Create a **blobless** local source store and materialize only Epic's GitDependencies manifest:

```bash
dotnet run --project src/Ueci.Cli -- \
  epic bootstrap \
  --dir .ueci/engine \
  --manifest-out .ueci/Commit.gitdeps.xml \
  --ref release
```

The lower-level `epic init` and `epic materialize` commands are also available independently for debugging and future lazy source fetches.

UECI injects the Git authorization header through per-process environment configuration. It does not persist the token to `.git/config`, the remote URL, `.ueci.yml`, or normal logs.

## Bootstrap UnrealBuildTool

Once the Epic token works, UECI can create a blobless source store, check out only the managed UBT/shared source seed, overlay the required GitDependencies files, select the correct bundled .NET SDK for the host, compile UBT, and probe it:

```bash
export UECI_EPIC_GITHUB_TOKEN='github_pat_...'

dotnet run --project src/Ueci.Cli -- \
  ubt bootstrap \
  --dir /tmp/ueci-engine \
  --ref release \
  --no-pack-cache
```

Then invoke the already bootstrapped UBT directly through UECI:

```bash
dotnet run --project src/Ueci.Cli -- \
  ubt run \
  --dir /tmp/ueci-engine \
  -- -help
```

UECI itself still targets .NET 8 for easy development. UBT is compiled with the .NET SDK shipped by the selected Unreal commit; after compilation UECI validates the generated `UnrealBuildTool.runtimeconfig.json` against that same bundle. No Unreal/.NET version pair is hard-coded. See [`docs/ubt-bootstrap.md`](docs/ubt-bootstrap.md).

For the Epic Git source seed, Git 2.49+ is strongly recommended. UECI creates a cone-mode sparse checkout for the UBT source seed and uses `git backfill --sparse` when available to batch missing blobs from the `--filter=blob:none` repository before populating the worktree. Older Git versions still fall back to lazy checkout, but that path can be dramatically slower on Unreal's source tree.

Sparse worktree updates and GitDependencies are treated as two independent layers. If a sparse checkout update displaces a file previously overlaid by GitDependencies (for example Epic's bundled `dotnet` host), UECI detects the missing overlay path and restores only the displaced files from its content-addressed blob cache before invoking UBT again. This prevents source-discovery passes from destroying their own build runtime.

## Build a plugin (experimental)

The v0.4 path creates a temporary project under the lazy engine root, enables the source plugin, then asks the real UBT to build only the plugin modules. When UBT exposes a concrete missing Engine requirement, UECI materializes it from the pinned Epic Git commit or `Commit.gitdeps.xml` and retries.

```bash
export UECI_EPIC_GITHUB_TOKEN='github_pat_...'

dotnet run --project src/Ueci.Cli -- \
  build-plugin ./MyPlugin/MyPlugin.uplugin \
  --engine-dir /tmp/ueci-engine \
  --out /tmp/ueci-package \
  --ref release \
  --no-pack-cache
```

The normal unit suite remains offline/token-free, including a synthetic `.tar.gz` Linux-toolchain test. On a native Linux x86_64 smoke build, UECI only downloads Epic's clang/sysroot toolchain if UBT reports that the Linux SDK is missing. With `--no-pack-cache`, the downloaded toolchain archive is removed after successful extraction.

To exercise the real Epic path on a normal machine with the bundled fixture:

```bash
export UECI_EPIC_GITHUB_TOKEN='...'
./scripts/smoke-plugin.sh
```

See [`docs/plugin-build.md`](docs/plugin-build.md) for the discovery loop, packaging behavior, and current alpha limitations.

## Project config

The initial config can be generated with:

```bash
dotnet run --project src/Ueci.Cli -- init \
  --engine-ref release \
  --plugin DualSenseMultiplatform.uplugin \
  --targets linux-x64,win-x64,macos-arm64
```

Example:

```yaml
schema: 1
engine:
  ref: release
  repository: https://github.com/EpicGames/UnrealEngine.git
plugin:
  path: DualSenseMultiplatform.uplugin
targets:
  - linux-x64
  - win-x64
  - macos-arm64
presentation:
  mode: auto
credentials:
  token_env: UECI_EPIC_GITHUB_TOKEN
```

The credential field stores only the **environment variable name**, never a secret.

## GitHub Action (prototype)

The root `action.yml` can either bootstrap UBT only or, when `plugin-path` is supplied, run the experimental v0.4 lazy plugin build and package the result.

```yaml
- uses: your-org/ueci@v0.4.0-alpha.4
  with:
    epic-token: ${{ secrets.EPIC_GITHUB_TOKEN }}
    engine-ref: release
    plugin-path: MyPlugin/MyPlugin.uplugin
    package-dir: .ueci/package
```

For pull requests from untrusted forks, do not expose an Epic token to checked-out untrusted code. Keep credentialed Unreal builds on trusted events/branches.

## Roadmap

1. **v0.1 — manifest + Epic source substrate** ✅
   - GitDependencies parser/index/planner
   - read-only Epic Git auth
   - blobless source store
   - local deterministic tests
2. **v0.2 — GitDependencies fetch/materialize** ✅ alpha
   - gzip/`UEPACK00` pack extraction
   - compressed pack + verified blob caches
   - SHA-1 validation and executable-bit restoration
   - grouped multi-blob extraction and materialized subtrees
3. **v0.3 — Unreal-aware resolver** ✅ alpha
   - UBT + Epic bundled .NET bootstrap ✅
   - real UBT remains the rules authority ✅
4. **v0.4 — plugin build** 🚧
   - Hermetic UBT executor configuration across Engine/user/Project scopes (remote + local UBA, XGE, FASTBuild, SN-DBS disabled) ✅
   - synthetic project + Game/Editor targets ✅
   - bounded diagnostic-driven materialization/retry loop ✅ experimental
   - package plugin + build report ✅
   - validate minimal Linux fixture, then Windows/macOS
5. **v0.5 — mounted engine view**
   - Linux FUSE backend
   - WinFsp backend
   - macOS backend investigation
   - writable copy-on-write overlay
6. **v1.0 — production GitHub Action**
   - stable config schema
   - cache keys/locks
   - release binaries
   - hard disk-budget enforcement

See [`docs/architecture.md`](docs/architecture.md), [`docs/gitdependencies-format.md`](docs/gitdependencies-format.md), and [`docs/roadmap.md`](docs/roadmap.md).

## Redpoint VFS

The Redpoint `Redpoint.Vfs.Layer.GitDependencies` work is treated as an important reference/spike candidate, not a mandatory dependency of the v0.1 core. Keeping it optional lets the local parser and tests remain buildable with only the .NET SDK while we validate compatibility with current Unreal manifests. See [`experiments/redpoint-vfs/README.md`](experiments/redpoint-vfs/README.md).

## Security and Epic content

UECI is designed to avoid redistributing Unreal Engine source or binary dependency payloads. Users obtain Unreal data from Epic/GitHub using their own authorized access, and caches should remain private. See [`SECURITY.md`](SECURITY.md) and [`docs/epic-source-access.md`](docs/epic-source-access.md).

## License

UECI itself is licensed under **MPL-2.0**. The license applies to UECI source files, not to software/plugins produced by builds. Unreal Engine remains governed by Epic's own license/EULA.
