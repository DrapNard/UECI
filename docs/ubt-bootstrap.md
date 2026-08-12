# UnrealBuildTool bootstrap

Milestone 0.3 bootstraps a **source-built** UnrealBuildTool from a blobless Epic Git repository plus a selective GitDependencies overlay. A source checkout does not contain a ready-to-run `Engine/Binaries/DotNET/UnrealBuildTool/UnrealBuildTool.dll`; UECI therefore materializes the UBT C# source and compiles it with the .NET SDK shipped by the selected Unreal commit.

## Pipeline

```text
EpicGames/UnrealEngine
        |
        | git fetch --filter=blob:none --depth=1
        v
pinned source commit
        |
        +-- Engine/Source/Programs/UnrealBuildTool
        +-- Engine/Source/Programs/Shared
        +-- Engine/Build/Commit.gitdeps.xml
        |
        v
Commit.gitdeps.xml
        |
        +-- root Directory.Build.props / Directory.Build.targets
        +-- managed build support overlay
        +-- complete bundled .NET SDK for the current host RID
        |
        v
Epic bundled dotnet build
Engine/Source/Programs/UnrealBuildTool/UnrealBuildTool.csproj
        |
        v
Engine/Binaries/DotNET/UnrealBuildTool/UnrealBuildTool.dll
        |
        +-- generated UnrealBuildTool.runtimeconfig.json
        |
        v
runtime validation + `UnrealBuildTool -help`
```

## Why the complete bundled SDK?

The first alpha incorrectly expected `UnrealBuildTool.dll` to be present in the source seed. It is a build output. Compiling a C# project requires the SDK/MSBuild layer, not only the runtime host and shared framework.

UECI therefore resolves the newest SDK present under:

```text
Engine/Binaries/ThirdParty/DotNet/<bundle>/<host-rid>/sdk/<sdk-version>/
```

and materializes that host bundle from GitDependencies. This deliberately prefers correctness over micro-optimizing individual SDK files. The SDK is still tiny compared with a complete Unreal installation and remains content-addressed by the normal UECI cache.

After UBT is compiled, UECI reads the generated `UnrealBuildTool.runtimeconfig.json` and verifies that its required shared frameworks resolve inside the same Epic .NET bundle used for compilation.

## Source seed

The Git source seed is intentionally limited to:

- `Engine/Source/Programs/UnrealBuildTool`;
- `Engine/Source/Programs/Shared`;
- `Engine/Build/Build.version`;
- `Engine/Build/Commit.gitdeps.xml`.

Git remains a blobless promisor repository, so materializing these pathspecs fetches only the source blobs needed for the managed build rather than checking out the entire engine. UECI configures a cone-mode sparse checkout for those source directories and, when available, runs `git backfill --sparse` so the missing blobs are requested in batches before populating the selected worktree. This avoids the extremely slow failure mode where a partial clone lazily requests many blobs one at a time. Older Git versions fall back to the previous lazy checkout behavior.

The GitDependencies overlay additionally includes root managed build props/targets plus the small dependency files under the UBT/shared program trees and `Engine/Binaries/DotNET`.

## Local smoke test

```bash
export UECI_EPIC_GITHUB_TOKEN='...'
./scripts/smoke-ubt.sh
```

The expected end state is a successful UBT compilation followed by an `-help` probe. `--no-probe` skips only the final UBT execution; UBT is still compiled.

## Security

Unreal `.Build.cs` and `.Target.cs` files are C# code evaluated by UBT. Never combine an Epic credential with untrusted build rules (for example arbitrary fork pull-request code) in the same process or workflow job.
