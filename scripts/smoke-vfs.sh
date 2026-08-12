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
START_TIMEOUT="${UECI_VFS_START_TIMEOUT:-600}"
VERBOSE="${UECI_VFS_VERBOSE:-1}"

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

args=(
  mount "$MOUNT"
  --metadata-dir "$META"
  --state-dir "$STATE"
  --ref "$REF"
  --no-pack-cache
)
if [[ "$VERBOSE" != "0" ]]; then
  args+=(--verbose)
fi

echo "[smoke] starting UECI VFS; startup timeout ${START_TIMEOUT}s; mount=$MOUNT" >&2
dotnet run --project src/Ueci.Cli/Ueci.Cli.csproj -c Release --no-build -- "${args[@]}" &
UECI_VFS_PID=$!

started_at=$SECONDS
next_heartbeat=5
while ! mountpoint -q "$MOUNT" 2>/dev/null; do
  if ! kill -0 "$UECI_VFS_PID" 2>/dev/null; then
    set +e
    wait "$UECI_VFS_PID"
    status=$?
    set -e
    echo "[smoke] UECI exited before the FUSE mount became ready (exit=$status)." >&2
    exit "$status"
  fi

  elapsed=$((SECONDS - started_at))
  if (( elapsed >= START_TIMEOUT )); then
    echo "[smoke] timed out after ${elapsed}s waiting for $MOUNT to become a mountpoint." >&2
    exit 124
  fi
  if (( elapsed >= next_heartbeat )); then
    meta_size="$(du -sh "$META" 2>/dev/null | awk '{print $1}' || true)"
    state_size="$(du -sh "$STATE" 2>/dev/null | awk '{print $1}' || true)"
    echo "[smoke] waiting for mount... ${elapsed}s elapsed; metadata=${meta_size:-?}; state=${state_size:-?}; pid=$UECI_VFS_PID" >&2
    next_heartbeat=$((next_heartbeat + 5))
  fi
  sleep 1
done

echo "[smoke] FUSE mount READY after $((SECONDS - started_at))s: $MOUNT" >&2

echo "[smoke] metadata-only directory listing"
ls "$MOUNT/Engine/Source/Runtime/Core/Public" >/dev/null

echo "[smoke] targeted Git stat hydrates exact POSIX size"
git_file="$MOUNT/Engine/Source/Runtime/Core/Public/CoreMinimal.h"
git_size="$(stat -c %s "$git_file")"
if (( git_size <= 0 )); then
  echo "[smoke] CoreMinimal.h reported an invalid size through FUSE: ${git_size}." >&2
  exit 1
fi
echo "[smoke] CoreMinimal.h stat size: ${git_size} bytes"

echo "[smoke] lazy Git content read"
read_bytes="$(head -c 64 "$git_file" | wc -c)"
if (( read_bytes <= 0 )); then
  echo "[smoke] CoreMinimal.h returned no bytes through FUSE." >&2
  exit 1
fi
echo "[smoke] lazy read returned ${read_bytes} bytes"

echo "[smoke] warm Git content read"
warm_bytes="$(head -c 64 "$git_file" | wc -c)"
if (( warm_bytes != read_bytes )); then
  echo "[smoke] warm Git read returned ${warm_bytes} bytes; expected ${read_bytes}." >&2
  exit 1
fi

echo "[smoke] copy-on-write output"
mkdir -p "$MOUNT/Engine/Saved/UECI"
printf 'vfs-cow-ok\n' > "$MOUNT/Engine/Saved/UECI/smoke.txt"
grep -qx 'vfs-cow-ok' "$MOUNT/Engine/Saved/UECI/smoke.txt"

echo "VFS smoke OK: $MOUNT"
