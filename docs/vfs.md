# VFS design notes

VFS remains a first-class target even though v0.1 materializes files explicitly.

## Backend matrix

| Host | Preferred mounted backend | Fallback |
|---|---|---|
| Linux | FUSE 3 | materialized directory |
| Windows | WinFsp | materialized directory |
| macOS | to be validated (macFUSE/FSKit constraints) | materialized directory |

The resolver is intentionally independent of these drivers through `IEngineReadLayer` / `IEngineWritableOverlay`.

## Why not require mounts?

Hosted CI can restrict mount privileges and every extra kernel/filesystem dependency makes the GitHub Action less portable. Materialized mode is therefore the correctness baseline; mounted mode should reduce I/O and storage on self-hosted runners without changing build semantics.
