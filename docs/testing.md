# Testing

UECI's tests must be runnable on a normal development machine. A GitHub runner is never required to reproduce the unit suite.

## Standard suite

Linux/macOS:

```bash
./scripts/test.sh
```

Windows:

```powershell
./scripts/test.ps1
```

The test executable intentionally avoids xUnit/NUnit/MSTest packages so the test harness itself has no third-party NuGet dependency. v0.2 tests also construct gzip `UEPACK00` packs in memory and verify multi-blob extraction, SHA-1 failure behavior, cache reuse, pack-cache deletion, executable bits, and output path safety without any network access.

## Real-manifest smoke test

If you have an authorized Unreal checkout or a `Commit.gitdeps.xml` file:

```bash
UECI_REAL_MANIFEST=/path/to/Commit.gitdeps.xml ./scripts/test.sh
```

This verifies large-manifest streaming and the complete file→blob→pack graph without downloading any Epic payloads.

## Future network tests

Epic/GitHub/CDN tests must remain opt-in integration tests. The default suite must stay deterministic, offline, and credential-free.

## Optional real CDN smoke test

After obtaining an authorized `Commit.gitdeps.xml`, the CLI can exercise the real Epic CDN from any ordinary development machine:

```bash
dotnet run --project src/Ueci.Cli -- \
  gitdeps fetch /path/to/Commit.gitdeps.xml \
  Engine/Binaries/Linux/UnrealVersionSelector-Linux-Shipping \
  --out /tmp/UnrealVersionSelector-Linux-Shipping
```

This is deliberately not part of the default suite because development/tests must remain offline and deterministic.
