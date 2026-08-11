# UECI

**UECI is an experimental, minimal Unreal Engine substrate for CI/CD.** Its goal is to build Unreal Engine code plugins without installing a full Unreal Engine tree on every runner.

> Status: **v0.3 alpha / technical prototype.** Authenticated Epic source bootstrap, real GitDependencies CDN materialization, and a minimal UnrealBuildTool bootstrap now exist. Full plugin compilation and mounted VFS backends are still roadmap items.

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

## What v0.3 already does

- Streams huge `Commit.gitdeps.xml` files without loading the whole XML document.
- Builds deterministic `path → blob → pack` indexes.
- Resolves the exact CDN pack URL, pack offset, and file size for a manifest path.
- Plans subsets by exact path or directory prefix and deduplicates blobs/packs.
- Validates all file→blob and blob→pack references.
- Authenticates to `EpicGames/UnrealEngine` using a **read-only token supplied only through the environment**.
- Initializes an Epic source repository with `git fetch --filter=blob:none` and no full checkout.
- Materializes an individual Git blob on demand.
- Materializes selected tracked Epic Git subtrees into the blobless source store without checking out the full engine.
- Downloads Epic GitDependencies packs directly from the manifest-generated CDN URL.
- Supports the validated gzip + `UEPACK00` pack layout and absolute decompressed `PackOffset` semantics.
- Groups requested blobs by pack and extracts them in one forward gzip pass.
- Verifies every extracted blob against its manifest SHA-1 before it enters the cache.
- Keeps separate compressed-pack and verified-blob caches, with `--no-pack-cache` for disk-constrained runners.
- Restores executable bits from `IsExecutable` on Unix hosts.
- Rejects output-root path traversal during batch materialization.
- Reads `UnrealBuildTool.runtimeconfig.json` and resolves Epic's matching bundled .NET host/runtime from the manifest.
- Bootstraps `UnrealBuildTool.dll` from a minimal Git seed plus GitDependencies overlay and probes it with the Epic-bundled `dotnet`.
- Provides `ubt run` to forward arbitrary arguments to the bootstrapped UBT.
- Provides a future-proof VFS contract without making FUSE/WinFsp mandatory.
- Has dependency-free local tests: no GitHub runner and no Epic token are required.

## Requirements

For development/tests:

- .NET SDK 8.x
- Git 2.x
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

Once the Epic token works, UECI can create a blobless source store, check out only the managed UBT seed, overlay the required GitDependencies files, select the correct bundled .NET runtime for the host, and probe UBT:

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

UECI itself still targets .NET 8 for easy development. The UBT process uses the .NET runtime shipped by the selected Unreal commit, resolved from `UnrealBuildTool.runtimeconfig.json` rather than from a hard-coded engine version. See [`docs/ubt-bootstrap.md`](docs/ubt-bootstrap.md).

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

The root `action.yml` now bootstraps and probes UnrealBuildTool using the blobless Epic source plus minimal GitDependencies runtime. It still intentionally does not pretend that plugin building is complete yet.

```yaml
- uses: your-org/ueci@v0.3.0-alpha.1
  with:
    epic-token: ${{ secrets.EPIC_GITHUB_TOKEN }}
    engine-ref: release
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
3. **v0.3 — Unreal-aware resolver** 🚧
   - UBT + Epic bundled .NET bootstrap ✅
   - evaluate real `.Build.cs` / target rules
   - retry/lazy discovery when a requirement is missed
4. **v0.4 — plugin build**
   - synthetic project/target as needed
   - package code plugins
   - Linux/Windows/macOS matrix
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
