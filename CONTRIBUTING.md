# Contributing

Thanks for helping make Unreal plugin CI less wasteful.

## Development flow

1. Branch from `main`.
2. Keep changes focused and add a deterministic fixture test.
3. Run `./scripts/test.sh` (or `scripts/test.ps1`) locally.
4. Open a pull request using the template.

Suggested branch names:

- `feat/gitdeps-cache`
- `feat/fuse-driver`
- `fix/manifest-parser`
- `docs/security-model`

## Commit style

Conventional Commits are preferred:

```text
feat(epic): add blobless source initialization
fix(gitdeps): deduplicate shared pack plans
docs(vfs): document Linux mount fallback
```

## Unreal/Epic content

Do **not** commit:

- Unreal Engine source copied from Epic's private repository;
- GitDependencies pack payloads;
- proprietary SDK payloads;
- GitHub/Epic access tokens.

Tiny synthetic fixtures written specifically for UECI are preferred.

## Compatibility

The default test path must continue to work on an ordinary Linux/macOS/Windows workstation with .NET 8 and Git. Docker, GitHub Actions, FUSE, WinFsp, Unreal Engine, and Epic credentials cannot be mandatory for unit tests.
