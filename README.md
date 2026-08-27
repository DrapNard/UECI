# UECI

UECI builds Unreal Engine code plugins in CI without materializing a complete Unreal Engine checkout on every runner.

It presents a pinned Unreal Engine commit as a lazy virtual filesystem: Epic source files come from a blobless Git clone and binary dependencies come from `Commit.gitdeps.xml`. UnrealBuildTool (UBT) remains the authority for the build; UECI only fetches and caches the data needed to run it.

> Status: experimental alpha. Linux/FUSE3 and macOS/macFUSE mounted backends are implemented. Windows currently uses the materialized backend.

## Highlights

- Builds real Unreal code plugins with the generation-appropriate UBT runtime.
- Avoids a full Engine source checkout through lazy Git and GitDependencies providers.
- Uses a verified content-addressed cache for Git blobs, dependency blobs, packs, UBT outputs, and native toolchains.
- Supports Linux x64 via FUSE3 and macOS via macFUSE; the default backend selects the best supported host mode.
- Generates an ephemeral, minimal host project so plugin rules remain authoritative.
- Emits a package containing plugin binaries and an `ueci-build.json` build report.
- Exercises UE 4.5, 4.7–4.27, and UE 5.0–5.8 on Linux in the release compatibility workflow. UE 4.6/Linux is excluded because Epic no longer serves its required historical SDL archive.

## How it works

```text
Epic Git (blobless) ─┐
                     ├─ virtual Engine view ── UBT ── packaged plugin
GitDependencies CDN ─┘
```

The mounted backend keeps generated files in a writable copy-on-write layer while immutable Engine files remain lazy. UECI records a learned per-commit working set so future CI jobs can avoid re-indexing the complete Engine namespace.

## Requirements

Development and unit tests require:

- .NET SDK 8.x
- Git 2.x (2.49+ is recommended for `git backfill`)
- Linux, macOS, or Windows

Mounted builds additionally require:

- Linux: FUSE3, `pkg-config`, and a C compiler
- macOS: macFUSE and Xcode command-line tools

Real Epic builds require a GitHub account authorized for `EpicGames/UnrealEngine` and a read-only token supplied through `UECI_EPIC_GITHUB_TOKEN`.

## Quick start

```bash
git clone https://github.com/DrapNard/UECI.git
cd UECI
./scripts/test.sh
```

The offline suite needs neither an Unreal installation nor Epic credentials.

To build a code plugin from an Epic ref:

```bash
export UECI_EPIC_GITHUB_TOKEN='github_pat_...'

dotnet run --project src/Ueci.Cli -- \
  build-plugin ./MyPlugin/MyPlugin.uplugin \
  --engine-dir .ueci/engine \
  --out .ueci/package \
  --ref 5.8.2-release \
  --platform Linux
```

Use `--backend fuse` to require the mounted backend or `--backend materialized` for the portable fallback. See [plugin-build.md](docs/plugin-build.md) for options, host behavior, diagnostics, and packaging details.

## CI/CD

Store the Epic token as the repository secret `UECI_EPIC_GITHUB_TOKEN`; never put it in a command line, remote URL, or committed configuration file.

The repository’s scheduled compatibility workflow runs the minimal fixture against supported Linux release lines. Run it locally with:

```bash
export UECI_EPIC_GITHUB_TOKEN='github_pat_...'
./scripts/test-unreal-matrix-local.sh 5.8
```

The macOS mounted smoke is available on a macOS host through:

```bash
./scripts/smoke-plugin-vfs-macos.sh
```

## Useful commands

Inspect or plan GitDependencies data:

```bash
dotnet run --project src/Ueci.Cli -- gitdeps inspect /path/to/Commit.gitdeps.xml
dotnet run --project src/Ueci.Cli -- gitdeps plan /path/to/Commit.gitdeps.xml --prefix Engine/Source/Runtime/Core
```

Create a blobless Epic metadata store:

```bash
dotnet run --project src/Ueci.Cli -- \
  epic bootstrap --dir .ueci/engine --manifest-out .ueci/Commit.gitdeps.xml --ref 5.8.2-release
```

For the virtual-engine design and cache model, see [docs/vfs.md](docs/vfs.md). The GitDependencies format is documented in [docs/gitdependencies-format.md](docs/gitdependencies-format.md).

## Security

UECI configures Epic authorization per process. It does not persist the token to Git config, remote URLs, `.ueci.yml`, or normal logs. Dependency blobs are SHA-1 verified before entering the cache.

## License

See [LICENSE](LICENSE).
