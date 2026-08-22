# Unreal Engine release compatibility

UECI validates release compatibility with `.github/workflows/unreal-version-matrix.yml`.
The workflow intentionally uses stable Epic release branches rather than moving development branches.

## Linux/FUSE matrix targets

| Family | Release branches | Matrix contract |
| --- | --- | --- |
| UE4 | 4.5, 4.7–4.27 | Build the minimal code plugin and require a native Linux `.so` |
| UE5 | 5.0–5.8 | Build the minimal code plugin and require a native Linux `.so` |

There are 31 independent jobs in total. UE 4.6 is intentionally excluded on Linux because Epic no
longer serves the historical SDL archive required by that release. `fail-fast` is disabled, so a failure on one historical
release still produces results and diagnostics for the remaining releases.
The workflow prefers a matching Epic release `Commit.gitdeps.xml` asset when available and pins the release tag that owns that asset. If no release asset exists, it pins the stable branch head and uses the manifest tracked by that exact Engine commit.

The matrix is a test definition, not a blanket green-status claim. A release is considered verified
only after its private Epic job completes successfully. The workflow requires the repository secret
`UECI_EPIC_GITHUB_TOKEN` for access to `EpicGames/UnrealEngine`.

## Compatibility strategy

UECI resolves the pinned Engine version from `Engine/Build/Build.version` or, for older UE4 commits,
from the historical `ENGINE_*_VERSION` macros in `Launch/Resources/Version.h`, then inspects the exact
UnrealBuildTool source tree for capabilities. This lets release, custom, plus, chaos, or backported
branches use the APIs they actually contain instead of relying only on the branch name.

The current compatibility layer adapts:

- modern SDK-style .NET UBT versus legacy MSBuild/Mono UBT;
- ready-to-run legacy `Engine/Binaries/DotNET/UnrealBuildTool.exe` payloads;
- `ReadOnlyTargetRules` versus classic `TargetInfo` module rules;
- `ExtraModuleNames` versus classic `SetupBinaries` target population;
- `TargetLinkType.Modular` versus the legacy `ShouldCompileMonolithic()` override;
- optional modern TargetRules fields such as modular link type, unique build environment, include
  order, plugin support, global definitions, and runtime-symbol settings;
- optional UBT CLI switches and `BuildConfiguration.xml` fields;
- modern `Linux_SDK.json` toolchain descriptors, legacy setup-script descriptors, and the known UE4.20–4.27 native Linux toolchain identifiers as a conservative fallback;
- pre-UE4.20 native compiler families, preferring an explicitly supplied `UECI_LEGACY_CLANG` / `UECI_LEGACY_CLANG_ROOT` and otherwise caching a matching official LLVM portable release on Linux;
- bounded native-Linux SDK registration retries for legacy Mono UBT: plain `PATH`/`CC`/`CXX`, then AutoSDK, then legacy `LINUX_ROOT` / `LINUX_MULTIARCH_ROOT`, and finally both layouts. Source-token detection remains advisory rather than deciding the only environment attempted.

## Non-release refs

`release`, `master`, `ue5-main`, `ue6-main`, `dev-*`, `plus`, `chaos`, and other custom refs can still
be passed explicitly to UECI. They are not part of the release matrix because their contents can move
or intentionally diverge from a shipping release.

## Plugin-side compatibility

UECI can make its own bootstrap/host compatible with old Unreal releases, but it cannot rewrite a
plugin that uses APIs introduced after the requested Engine version. For cross-version testing, the
plugin's own `.Build.cs`, descriptor, C++ API usage, and third-party dependencies must support the
release under test.
