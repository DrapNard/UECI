# UECI

**UECI is an experimental, minimal Unreal Engine substrate for CI/CD.** Its goal is to build Unreal Engine code plugins without installing a full Unreal Engine tree on every runner.

> Status: **v0.5 alpha / technical prototype.** Authenticated Epic source bootstrap, real GitDependencies CDN materialization, UnrealBuildTool bootstrap, the materialized lazy plugin-build fallback, and native lazy Engine mounts now exist. `build-plugin --backend fuse` supports Linux x64 through FUSE3 and macOS through macFUSE. Windows remains on the materialized fallback.

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

The long-term design supports both **materialized mode** (portable, no special privileges) and **mounted mode** (FUSE3 on Linux, macFUSE on macOS, WinFsp on Windows) behind the same engine-view contract.

## What UECI already does

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
- Detects the exact Engine/UBT capability surface per Epic commit and supports both modern .NET UBT and legacy UE4 Mono/MSBuild/precompiled `UnrealBuildTool.exe` bootstraps.
- Materializes the UBT + shared C# source seed, then either reuses a release's precompiled legacy `UnrealBuildTool.exe` or compiles `UnrealBuildTool.csproj` with the Engine generation's bundled/runtime-compatible managed toolchain before probing UBT.
- Provides `ubt run` to forward arbitrary arguments to the bootstrapped UBT.
- Parses `.uplugin` module descriptors and keeps UBT as the authority for executable `.Build.cs` rules; a bounded parser only uses standard module dependency lists as optional prefetch hints.
- Creates an ephemeral project with a lean modular Game target for Runtime modules and an Editor target when required, then copies the plugin without stale build outputs.
- Invokes the real UBT with `-Module=<PluginModule>` so plugin rules remain authoritative.
- Converts missing-module/path/SDK diagnostics into bounded lazy Epic Git/GitDependencies materialization passes, including unresolved third-party library paths emitted by UBT.
- Batches up to two levels of small `.Build.cs` dependency hints between UBT passes while refusing large hinted subtrees until UBT explicitly requires them.
- Treats an explicit UBT missing-module diagnostic as authoritative over speculative prefetch: UECI force-refreshes the exact `*.Build.cs` from the pinned Epic commit and invalidates the generated Engine rules assembly before retrying.
- On native Linux x86_64, resolves Epic's generation-matched Linux SDK/toolchain; UE4.20–4.27 can pre-project the historical Epic clang/sysroot, while pre-4.20 UE4 resolves an era-compatible native clang instead of silently accepting the runner's modern compiler.
- Packages the built plugin with `Binaries` plus a machine-readable `ueci-build.json` report.
- Ships a minimal Runtime plugin fixture and an opt-in real plugin smoke test that runs on a normal workstation.
- Provides a future-proof VFS contract without making FUSE/WinFsp mandatory.
- Mounts a complete virtual Unreal Engine namespace on Linux through FUSE3 with metadata-only `stat`/`readdir`, lazy Git/GitDependencies content fetches, a shared CAS, and a persistent copy-on-write upper layer.
- Embeds a small libfuse3 helper as source and compiles it once into the local UECI cache instead of shipping a prebuilt native binary.
- Has dependency-free local tests: no GitHub runner and no Epic token are required.

## Unreal release compatibility matrix

Alpha.23 continues hardening the executable release-compatibility contract introduced in alpha.18 from repeated real Linux boundary runs. Alpha.22 produced the first complete green plugin build on UE5.7 through the FUSE backend, including a packaged native `.so`; the remaining release families are still treated as test-driven compatibility work rather than claimed support. `.github/workflows/unreal-version-matrix.yml` contains 31 independent Linux/FUSE jobs for the stable Epic release branches; UE 4.6/Linux is excluded because Epic no longer serves its required historical SDL archive:

- UE4: `4.5` through `4.27`
- UE5: `5.0` through `5.8`

Each row prepares the minimal fixture with the rule constructor expected by that release, resolves the exact Epic commit, runs the normal mounted `build-plugin` path, and fails unless `Binaries/Linux` contains a native `.so`. Diagnostics are uploaded per release so one legacy failure does not hide the rest of the matrix.
For each release family, the workflow pins the highest available patch tag (for example `4.27.2-release`) before resolving its exact commit. If that release exposes a standalone `Commit.gitdeps.xml` asset it is passed explicitly; otherwise UECI uses the dependency layout associated with that pinned release. UE4.5, which predates `Commit.gitdeps.xml`, overlays its historical Required/Optional release archives into the writable VFS layer before the build.

Compatibility-seed changes apply even when an older learned profile already exists: the persisted working set keeps its exact-size entries, while current safety Git metadata and release-specific managed GitDependencies files are merged without a full Engine index. Alpha.23 extends this to UBA host payload visibility and continues the cross-release UBT bootstrap (`Programs/Shared`, historical managed references, generated MSBuild support, and explicit project plugin suppression) without requiring the shared cache to be deleted between matrix runs.

The compatibility layer prefers **source feature detection** over hard-coded minor-version behavior. The Engine version is still used for broad runtime generations and conservative fallbacks, while individual TargetRules/ModuleRules/XML/CLI features are emitted only when the pinned UBT source exposes them. Moving development branches such as `release`, `master`, `ue5-main`, `ue6-main`, `dev-*`, `plus`, and `chaos` remain valid explicit refs, but are intentionally not used as reproducible release-matrix evidence.

A plugin must itself support the Engine release being tested; UECI adapts its synthetic host and bootstrap, not arbitrary plugin source APIs. The repository secret `UECI_EPIC_GITHUB_TOKEN` is required to execute the private Epic release matrix.

Run the same matrix locally with `./scripts/test-unreal-matrix-local.sh`. Alpha.20 defaults its matrix workspace to `$HOME/UECI-Matrix` rather than `/tmp`, redirects `TMPDIR` there, shares a single content-addressed cache, cleans each heavy per-version workspace after its result is recorded, and preserves only compact logs/results unless `UECI_MATRIX_KEEP_WORKSPACES=1` is set. A subset can be passed as positional versions.

```bash
./scripts/test-unreal-matrix-local.sh
./scripts/test-unreal-matrix-local.sh 4.5 4.11 4.27 5.0 5.5 5.6 5.8
```

## Requirements

For development/tests:

- .NET SDK 8.x
- Git 2.x (Git 2.49+ strongly recommended for fast blobless source materialization via `git backfill`)
- Linux, Windows, or macOS
- Mounted mode on Linux: FUSE3 + `pkg-config` + a C compiler (`cc`, Clang, or GCC)

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

## Mount a virtual Engine (Linux/FUSE3 or macOS/macFUSE)

The v0.5 mounted backend exposes the whole pinned Unreal namespace without checking out Engine source contents. Directory metadata comes from Git tree metadata + `Commit.gitdeps.xml`; the first `open()` of an immutable file blocks only that filesystem request while UECI fills the CAS. Writes go to a persistent copy-on-write upper layer outside the mount.

`v0.5.0-alpha.6` attacks the mounted-build hot path itself. FUSE workers keep persistent Unix-socket sessions to the managed resolver, directory scans use `READDIRPLUS`-style attributes plus kernel metadata/readdir caching, immutable lower-directory listings are precomputed, and verbose mode aggregates metadata traffic instead of printing every `stat(2)`. Git content hydration also keeps a persistent `git cat-file --batch` process; on UECI's verified one-commit shallow Epic snapshot, the small known UBT bootstrap source seed is batch-prefetched before the mounted build. Exact Git sizes remain metadata-only and cached by Epic commit, so `stat(2)` does not fetch source contents.

`v0.5.0-alpha.7` adds a build-only minimal Engine profile on top of that VFS. The first run for an unknown Epic commit starts from the observed alpha.6 UBT/UHT working set; if that seed is insufficient, UECI retries once with the complete virtual Engine and records the files UBT actually stats/opens. The learned commit profile is stored in the shared UECI cache with exact Git OIDs/modes/sizes and the GitDependencies subset, so later CI workspaces skip the global Git tree/size crawl and expose only the pruned working set. Generated UBT managed outputs are cached by commit; profile-sensitive Engine Rules are rebuilt against the active namespace. The standalone `ueci mount` command intentionally keeps the complete namespace for debugging and browsing.

`v0.5.0-alpha.8` completes the lean Program-host path reached by the real alpha.7 smoke. The runtime host supplies a tiny build-only Linux entrypoint instead of depending on Engine `Launch`, explicitly includes the plugin modules in `ExtraModuleNames`, and treats native linker errors as native failures rather than profile misses. This keeps the learned minimal profile hot while still making UBT compile and link the plugin modules as part of the synthetic target.

`v0.5.0-alpha.9` disables runtime symbol-file generation for that synthetic Program host. UE 5.8 otherwise adds `dump_syms` to the Linux post-link script; UECI does not need crash-runtime symbols for a never-executed validation executable, so the minimal profile no longer has to carry the standalone `Engine/Binaries/Linux/dump_syms` utility.

`v0.5.0-alpha.10` also disables `dump_syms` in UBT's hermetic `BuildConfiguration.xml`. The alpha.9 real smoke proved the target-level flag alone does not suppress the Linux post-link helper on this UE 5.8 commit; the configuration-level switch removes that standalone binary dependency from every synthetic validation UBT invocation.

`v0.5.0-alpha.11` fixes the exact UE 5.8 property spelling to `bDisableDumpSyms` (alpha.10 accidentally used `bDisableDumpSYMs`) and also passes `-NoDumpSyms` on synthetic plugin UBT invocations. This makes suppression independent of XML scope precedence and prevents the minimal mounted Engine from ever requiring the standalone `dump_syms` helper.

`v0.5.0-alpha.13` uses a lean modular **Game** host for Runtime modules and keeps the modular Editor host for Editor modules. UE Runtime modules explicitly exclude Program targets, so alpha.12 could request `-Module=<plugin-module>` against an incompatible host type. Module-targeted Game/Editor builds preserve the plugin's declared host semantics while still emitting packageable native libraries and retaining the minimal Engine profile. Alpha.13 also surfaces actionable UBT errors with context even when parallel actions continue after the first failure.

`v0.5.0-alpha.14` gives that synthetic Runtime/Game target a **unique UBT build environment**. Its deliberately lean compile/plugin settings differ from UnrealGame, so sharing UnrealGame build products is rejected by UBT; isolating the target keeps the minimal settings legal without forcing an unsafe shared-environment override.

The mounted backend also normalizes **synthetic-target build products** before packaging. UE may place modular plugin libraries beside a `TargetBuildEnvironment.Unique` host rather than directly under the plugin tree; UECI now harvests only native products whose names match the requested plugin modules, filters matching `.modules` metadata, and copies them into `Plugins/<Name>/Binaries/<Platform>` before the final package is assembled.

```bash
export UECI_EPIC_GITHUB_TOKEN='github_pat_...'
mkdir -p /tmp/ueci-engine-view

dotnet run --project src/Ueci.Cli -- \
  mount /tmp/ueci-engine-view \
  --metadata-dir .ueci/vfs-source \
  --state-dir .ueci/vfs-state \
  --ref release \
  --no-pack-cache \
  --verbose
```

Then, from another terminal:

```bash
ls /tmp/ueci-engine-view/Engine/Source/Runtime/Core/Public
head /tmp/ueci-engine-view/Engine/Source/Runtime/Core/Public/CoreMinimal.h
```

Use `./scripts/smoke-vfs.sh` for the opt-in real-host smoke. Unit tests still do not require `/dev/fuse`. See [`docs/vfs.md`](docs/vfs.md).

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

UECI itself still targets .NET 8 for easy development. UBT is normally compiled with the .NET SDK shipped by the selected Unreal commit. When a historical Epic host cannot run on the current distro and UECI deliberately falls back to the runner SDK, it normalizes legacy/self-contained `runtimeconfig.json` metadata to the runner's `Microsoft.NETCore.App` version and enables major roll-forward. See [`docs/ubt-bootstrap.md`](docs/ubt-bootstrap.md).

For the Epic Git source seed, Git 2.49+ is strongly recommended. UECI creates a cone-mode sparse checkout for the UBT source seed and uses `git backfill --sparse` when available to batch missing blobs from the `--filter=blob:none` repository before populating the worktree. Older Git versions still fall back to lazy checkout, but that path can be dramatically slower on Unreal's source tree.

Sparse worktree updates, GitDependencies overlays, and externally installed SDKs are treated as separate layers. If a sparse checkout update displaces a file previously overlaid by GitDependencies (for example Epic's bundled `dotnet` host), UECI restores only the missing overlay paths from its content-addressed blob cache. Native Linux toolchains use a persistent store under the shared UECI cache at `toolchains/installed/linux-x64/<version>` and a lightweight projection at `Engine/Extras/ThirdPartyNotUE/SDKs/HostLinux/Linux_x64/<version>`. Sparse updates may remove the projection, but never the authoritative payload; UECI recreates it before the next UBT pass.

## Build a plugin (experimental)

Two backends now coexist. `auto` selects `fuse` for native Linux x64 and macOS builds, keeping `materialized` as the portable fallback elsewhere. `fuse` mounts the exact learned/seeded pinned Engine working set (with one dynamic fallback when required), compiles UBT **inside the virtual Engine**, and invokes each UBT target once while Git/GitDependencies files hydrate into CAS on normal filesystem access. Linux installs Epic's native toolchain in persistent state; macOS uses the local Xcode toolchain.

```bash
export UECI_EPIC_GITHUB_TOKEN='github_pat_...'

dotnet run --project src/Ueci.Cli -- \
  build-plugin ./MyPlugin/MyPlugin.uplugin \
  --engine-dir /tmp/ueci-engine \
  --out /tmp/ueci-package \
  --ref release \
  --backend fuse \
  --no-pack-cache
```

The mounted build keeps the host project outside the FUSE worktree and stores the authoritative extracted Linux toolchain in the shared UECI cache, projecting it into the mounted Engine only for UBT. Engine-generated `Intermediate`, `Saved`, UBT `bin/obj`, and other writes land in the persistent COW upper. At the end UECI reports Git blobs/bytes hydrated and GitDependencies network bytes so the real working set can be measured.

Real-host smokes:

```bash
./scripts/smoke-plugin.sh       # materialized fallback
./scripts/smoke-plugin-vfs.sh   # Linux/FUSE mounted build
./scripts/smoke-plugin-vfs-macos.sh # macOS/macFUSE mounted build; enforces <5 minutes
```

See [`docs/plugin-build.md`](docs/plugin-build.md) and [`docs/vfs.md`](docs/vfs.md).

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


> **Executor bootstrap:** UE 5.8 can pre-create UBA before platform validation. UECI therefore materializes the tiny managed `EpicGames.UBA/Library.props` plus the host-specific native UBA payload **before compiling UBT**. Missing-UBA diagnostics remain supported as a lazy fallback, and the full UBT log is merged into discovery so the same failed pass can reveal the native platform SDK requirement.

## Cold-runner optimization (alpha.17)

The Linux mounted path is optimized for an ephemeral runner rather than assuming a previous native build workspace. After the virtual Engine is mounted, UECI starts three independent operations together: managed UBT preparation, synthetic host generation, and Linux toolchain acquisition. On a truly empty cache this overlaps the largest bootstrap stages; on a restored cache each stage independently collapses to a local hit.

The shared cache is intentionally separate from `--engine-dir`:

```text
<cache-dir>/
├── blobs/                       # verified GitDependencies CAS blobs
├── engine-artifacts/<commit>/   # managed UBT/shared outputs
├── engine-manifests/            # Commit.gitdeps.xml by exact commit
├── engine-indexes/              # exact Git blob-size metadata by commit
├── engine-profiles/             # minimal Engine working sets
├── epic-metadata/               # blobless Git metadata stores
├── git-blobs/                   # content-addressed lazy Epic Git blobs
├── native/fuse3/                # compiled tiny FUSE helper
└── toolchains/
    ├── installed/linux-x64/<version>/ # extracted Epic native clang/sysroot
    ├── legacy-clang/linux-x64/<version>/ # pre-UE4.20 era-compatible LLVM compiler
    ├── legacy-stdlib/linux-x64/<version>/ # isolated era-compatible C++ headers
    └── legacy-system-headers/linux-x64/   # modern-host shims for headers removed by glibc
```

The disposable Engine workspace still contains COW/generated state for the current job only. UECI deliberately does **not** require a previous job's native Core/plugin object files for this optimization pass. Every mounted build also prints a timing table so cold-runner regressions can be attributed to metadata, toolchain, UBT, native compilation, collection, or packaging instead of being inferred from total wall time.

The composite Action persists this cold-start cache under an exact Epic-commit key on trusted jobs. On a new commit it may restore the latest same-OS/architecture cache as a seed, reusing shared CAS/toolchain content; after the build it prunes old commit-scoped profiles, manifests, indexes, UBT artifacts, and metadata before the new exact-key cache is saved. Because the cache can contain Epic-derived metadata, lazy Git blobs, and managed UBT outputs, UECI never restores or saves it for fork pull requests or `pull_request_target`; those jobs run uncached. Do not treat an Actions cache as a secret store.

## GitHub Action (prototype)

The root `action.yml` can either bootstrap UBT only or, when `plugin-path` is supplied, run the lazy plugin build and package the result. Alpha.17 resolves the exact Epic ref object id first and keys the GitHub Actions cold-start cache with it, so moving refs such as `release` cannot accidentally reuse a cache as if the Engine commit were unchanged.

```yaml
- uses: your-org/ueci@v0.5.0-alpha.32
  with:
    epic-token: ${{ secrets.EPIC_GITHUB_TOKEN }}
    engine-ref: release
    plugin-path: MyPlugin/MyPlugin.uplugin
    package-dir: .ueci/package
    backend: auto
    cache-enabled: true
```

For pull requests from untrusted forks, do not expose an Epic token to checked-out untrusted code. Keep credentialed Unreal builds on trusted events/branches.

The Action currently uses `actions/setup-dotnet@v6` and `actions/cache@v5`; self-hosted runners therefore need a GitHub Actions runner recent enough for the Node 24 action runtime (2.327.1 or newer). GitHub-hosted runners already satisfy this requirement.

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
   - UBT bootstrap includes host UBA support before managed compilation; hermetic executor config remains in place ✅
   - synthetic project + lean modular Game/Editor targets ✅
   - bounded diagnostic-driven materialization/retry loop with conservative Build.cs prefetch hints ✅ experimental
   - package plugin + build report ✅
   - validate minimal Linux fixture, then Windows/macOS
5. **v0.5 — mounted engine view**
   - Linux FUSE backend ✅
   - cold-runner bootstrap/cache/timing pass ✅ alpha.17
   - multi-release UE4/UE5 compatibility foundation + 32-release Linux matrix ✅ alpha.18
   - Windows native mounted backend (WinFsp) ← next
   - macOS native mounted backend ← next after Windows
   - writable copy-on-write overlay ✅ Linux
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
