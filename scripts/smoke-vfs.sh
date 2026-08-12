#!/usr/bin/env bash
set -euo pipefail

if [[ -z "${UECI_EPIC_GITHUB_TOKEN:-}" ]]; then
  echo "UECI_EPIC_GITHUB_TOKEN is required for the real VFS smoke test." >&2
  exit 2
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MOUNT="${UECI_VFS_MOUNT:-/tmp/ueci-vfs-engine}"
META="${UECI_VFS_METADATA:-/tmp/ueci-vfs-metadata}"
STATE="${UECI_VFS_STATE:-/tmp/ueci-vfs-state}"
REF="${UECI_EPIC_REF:-release}"

mkdir -p "$MOUNT"
if mountpoint -q "$MOUNT" 2>/dev/null; then
  fusermount3 -u "$MOUNT"
fi
rm -rf "$MOUNT"
mkdir -p "$MOUNT"

cleanup() {
  set +e
  if mountpoint -q "$MOUNT" 2>/dev/null; then
    fusermount3 -u "$MOUNT"
  fi
  if [[ -n "${UECI_VFS_PID:-}" ]]; then
    kill "$UECI_VFS_PID" 2>/dev/null || true
    wait "$UECI_VFS_PID" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

cd "$ROOT"
dotnet build Ueci.sln -c Release

dotnet run --project src/Ueci.Cli/Ueci.Cli.csproj -c Release --no-build -- \
  mount "$MOUNT" \
  --metadata-dir "$META" \
  --state-dir "$STATE" \
  --ref "$REF" \
  --no-pack-cache &
UECI_VFS_PID=$!

for _ in {1..120}; do
  if mountpoint -q "$MOUNT" 2>/dev/null; then
    break
  fi
  if ! kill -0 "$UECI_VFS_PID" 2>/dev/null; then
    wait "$UECI_VFS_PID"
    exit 1
  fi
  sleep 0.25
done

mountpoint -q "$MOUNT"

echo "[smoke] metadata-only directory listing"
ls "$MOUNT/Engine/Source/Runtime/Core/Public" >/dev/null

echo "[smoke] lazy Git/GitDependencies content read"
test -s "$MOUNT/Engine/Source/Runtime/Core/Public/CoreMinimal.h"
head -c 64 "$MOUNT/Engine/Source/Runtime/Core/Public/CoreMinimal.h" >/dev/null

echo "[smoke] copy-on-write output"
mkdir -p "$MOUNT/Engine/Saved/UECI"
printf 'vfs-cow-ok\n' > "$MOUNT/Engine/Saved/UECI/smoke.txt"
grep -qx 'vfs-cow-ok' "$MOUNT/Engine/Saved/UECI/smoke.txt"

echo "VFS smoke OK: $MOUNT"
