# Redpoint VFS spike

Redpoint's historical packages are directly relevant to UECI's mounted-engine design:

- `Redpoint.Vfs.Layer.Git`
- `Redpoint.Vfs.Layer.GitDependencies`
- `Redpoint.Vfs.Driver.WinFsp`

The GitDependencies layer describes essentially the same lower-layer model UECI wants: serve Unreal GitDependencies on top of a Git commit without running Epic's `GitDependencies.exe`.

## Why this is not a hard dependency yet

The Git/GitDependencies layer packages found during the prototype are from 2024, while Unreal's manifests continue to evolve. UECI v0.1 first validates the current manifest independently. This gives us three options later:

1. use Redpoint packages directly when compatible;
2. write a small adapter/fork under their MIT terms;
3. keep the UECI-native layer and use Redpoint as a reference implementation.

The eventual VFS API must not expose Redpoint-specific types so Linux FUSE, WinFsp, and macOS backends remain replaceable.
