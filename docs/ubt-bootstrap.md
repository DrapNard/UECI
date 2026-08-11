# UnrealBuildTool bootstrap

Milestone 0.3 introduces the first Unreal-aware bootstrap. The goal is intentionally narrower than a plugin build: produce a runnable UnrealBuildTool from a blobless Epic Git repository plus only the GitDependencies payloads required by UBT and its managed runtime.

## Data flow

```text
EpicGames/UnrealEngine (blob:none)
        │
        ├── Engine/Binaries/DotNET/**          Git tracked seed
        ├── Engine/Build/Build.version         Git tracked seed
        └── Engine/Build/Commit.gitdeps.xml    Git tracked manifest
                         │
                         ▼
              UnrealBuildTool.runtimeconfig.json
                         │
                         ▼
              framework requirement(s)
                         │
                         ▼
               Commit.gitdeps.xml index
                         │
          ┌──────────────┴─────────────────┐
          │                                │
Engine/Binaries/DotNET/**       ThirdParty/DotNet/<host>/**
GitDependencies overlay         dotnet + host + shared runtime
          │                                │
          └──────────────┬─────────────────┘
                         ▼
              bundled Epic dotnet host
                         │
                         ▼
                 UnrealBuildTool.dll
```

UECI does not hard-code a particular .NET patch. It reads UnrealBuildTool's runtime config, finds a compatible host runtime in the selected commit's GitDependencies manifest, and chooses the highest available patch with the same major/minor framework version.

The initial runtime materialization intentionally includes only:

- the `dotnet` host executable for the current host RID;
- the host/fxr subtree;
- shared framework subtree(s) required by `UnrealBuildTool.runtimeconfig.json`;
- the `Engine/Binaries/DotNET` GitDependencies overlay.

It does not download the complete Epic .NET SDK.

## Command

```bash
export UECI_EPIC_GITHUB_TOKEN='...'

dotnet run --project src/Ueci.Cli -- \
  ubt bootstrap \
  --dir /tmp/ueci-engine \
  --ref release \
  --no-pack-cache
```

By default the command finishes by invoking UBT with `-help`. Use `--no-probe` when only the filesystem bootstrap is desired.

After a successful bootstrap, arbitrary UBT arguments can be forwarded without repeating the bootstrap:

```bash
dotnet run --project src/Ueci.Cli -- \
  ubt run \
  --dir /tmp/ueci-engine \
  -- -help
```

## Host RIDs

UECI currently maps normal desktop hosts to these Epic runtime identifiers:

- `linux-x64`, `linux-arm64`
- `win-x64`, `win-arm64`
- `mac-x64`, `mac-arm64`

The host RID can be overridden with `--host-rid` for diagnostics and resolver tests.

## Scope and next step

This milestone proves that UBT itself can be composed from the two providers. It does not yet claim that a complete plugin compile can succeed from this seed. A plugin build will cause UBT to inspect targets, modules, headers, toolchains, platform files, and third-party dependencies. Those accesses become the input for the next requirement-discovery/materialization loop rather than a reason to pre-install the full engine.
