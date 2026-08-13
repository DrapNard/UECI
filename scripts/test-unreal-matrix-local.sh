#!/usr/bin/env bash
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

ALL_VERSIONS=(
  4.5 4.6 4.7 4.8 4.9
  4.10 4.11 4.12 4.13 4.14 4.15 4.16 4.17 4.18 4.19
  4.20 4.21 4.22 4.23 4.24 4.25 4.26 4.27
  5.0 5.1 5.2 5.3 5.4 5.5 5.6 5.7 5.8
)
VERSIONS=("$@")
if (( ${#VERSIONS[@]} == 0 )); then VERSIONS=("${ALL_VERSIONS[@]}"); fi

# Never default a 32-release Unreal matrix to /tmp. On many Linux desktops /tmp is tmpfs and can
# fill while the real SSD still has hundreds of GiB free. TMPDIR is redirected as well so Git,
# bash here-documents, .NET tests and UECI's short FUSE socket all stay on the selected filesystem.
MATRIX_ROOT="${UECI_MATRIX_ROOT:-$HOME/UECI-Matrix}"
CACHE_ROOT="${UECI_MATRIX_CACHE:-${UECI_MATRIX_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/ueci/matrix}}"
LOG_ROOT="$MATRIX_ROOT/logs"
TMP_ROOT="$MATRIX_ROOT/tmp"
RESULTS="$MATRIX_ROOT/results.tsv"
KEEP_WORKSPACES="${UECI_MATRIX_KEEP_WORKSPACES:-0}"
RUN_PREFLIGHT="${UECI_MATRIX_PREFLIGHT:-1}"

mkdir -p "$MATRIX_ROOT" "$LOG_ROOT" "$TMP_ROOT" "$CACHE_ROOT"
export TMPDIR="$TMP_ROOT"
printf 'version\tstatus\tref\tbinary\n' > "$RESULTS"

if [[ -z "${UECI_EPIC_GITHUB_TOKEN:-}" ]]; then
  echo "UECI_EPIC_GITHUB_TOKEN is not set." >&2
  exit 2
fi
export GH_TOKEN="${GH_TOKEN:-$UECI_EPIC_GITHUB_TOKEN}"

is_mounted() {
  local target="$1"
  if command -v mountpoint >/dev/null 2>&1; then
    mountpoint -q "$target" 2>/dev/null
    return $?
  fi
  grep -Fq " $target " /proc/self/mountinfo 2>/dev/null
}

cleanup_mount() {
  local target="$1"
  [[ -d "$target" ]] || return 0
  if is_mounted "$target"; then
    echo "[matrix] unmounting stale FUSE view: $target"
    fusermount3 -u "$target" 2>/dev/null \
      || fusermount3 -uz "$target" 2>/dev/null \
      || umount -l "$target" 2>/dev/null \
      || true
  fi
}

cleanup_work() {
  local work="$1"
  cleanup_mount "$work/engine/.ueci/mounted-build/engine-view"
  if [[ "$KEEP_WORKSPACES" != "1" && -e "$work" ]]; then
    rm -rf -- "$work" 2>/dev/null || {
      cleanup_mount "$work/engine/.ueci/mounted-build/engine-view"
      rm -rf -- "$work" 2>/dev/null || true
    }
  fi
}

cleanup_all_transient() {
  [[ "$KEEP_WORKSPACES" == "1" ]] && return 0
  for stale in "$MATRIX_ROOT"/work-* "$MATRIX_ROOT"/ue-*; do
    [[ -e "$stale" ]] || continue
    cleanup_work "$stale"
  done
}
trap cleanup_all_transient EXIT
trap 'cleanup_all_transient; exit 130' INT
trap 'cleanup_all_transient; exit 143' TERM
cleanup_all_transient

show_space() {
  local df_bin="df"
  [[ -x /usr/bin/df ]] && df_bin=/usr/bin/df
  "$df_bin" -h "$MATRIX_ROOT" 2>/dev/null | tail -n1 || true
}

printf '%s\n' "============================================================" "Building UECI" "============================================================"
dotnet build Ueci.sln -c Release --nologo || exit 1

if [[ "$RUN_PREFLIGHT" == "1" ]]; then
  printf '\n%s\n' "============================================================" "Running UECI preflight tests" "============================================================"
  if ! ./scripts/test.sh; then
    echo
    echo "WARNING: preflight tests failed. Continuing the Unreal matrix for diagnostics."
  fi
fi

passed=()
failed=()

for VERSION in "${VERSIONS[@]}"; do
  echo
  echo "################################################################"
  echo "# UE $VERSION"
  echo "################################################################"

  WORK="$MATRIX_ROOT/work-$VERSION"
  ENGINE="$WORK/engine"
  PLUGIN_DIR="$WORK/plugin"
  PACKAGE="$WORK/package"
  MANIFEST="$WORK/Commit.gitdeps.xml"
  REF_FILE="$WORK/release-ref"
  LOG="$LOG_ROOT/ue-$VERSION.log"
  REPORT="$LOG_ROOT/ue-$VERSION-build.json"

  cleanup_work "$WORK"
  mkdir -p "$WORK"
  rm -f -- "$LOG" "$REPORT"

  if ! PLUGIN="$(./scripts/prepare-compat-fixture.sh "$VERSION" "$PLUGIN_DIR")"; then
    echo "FAIL: fixture generation" | tee "$LOG"
    failed+=("$VERSION")
    printf '%s\tFAIL_FIXTURE\t-\t-\n' "$VERSION" >> "$RESULTS"
    cleanup_work "$WORK"
    continue
  fi

  rm -f "$MANIFEST" "$REF_FILE"
  RELEASE_MANIFEST="$(
    GH_TOKEN="$GH_TOKEN" ./scripts/resolve-release-gitdeps.sh \
      "$VERSION" "$MANIFEST" "$REF_FILE" || true
  )"

  ENGINE_REF="$VERSION"
  if [[ -s "$REF_FILE" ]]; then
    ENGINE_REF="$(cat "$REF_FILE")"
    echo "[matrix] pinned release tag: $ENGINE_REF"
  fi
  if [[ -n "$RELEASE_MANIFEST" ]]; then
    echo "[matrix] release manifest:   $RELEASE_MANIFEST"
  else
    echo "[matrix] using dependency metadata from the release source layout"
  fi

  # UE4.5 predates Commit.gitdeps.xml. Prepare Epic's historical Required/Optional release ZIPs in
  # the FUSE upper layer before UECI creates the virtual Engine view.
  if ! GH_TOKEN="$GH_TOKEN" ./scripts/prepare-legacy-release-deps.sh \
      "$VERSION" "$ENGINE_REF" "$ENGINE" 2>&1 | tee -a "$LOG"; then
    echo "❌ UE $VERSION: legacy dependency preparation failed"
    failed+=("$VERSION")
    printf '%s\tFAIL_DEPS\t%s\t-\n' "$VERSION" "$ENGINE_REF" >> "$RESULTS"
    cleanup_work "$WORK"
    continue
  fi

  echo "[matrix] resolving Epic ref $ENGINE_REF..."
  if ! ENGINE_COMMIT="$(
    UECI_EPIC_GITHUB_TOKEN="$UECI_EPIC_GITHUB_TOKEN" \
      dotnet run --project src/Ueci.Cli/Ueci.Cli.csproj -c Release --no-build -- \
      epic resolve --ref "$ENGINE_REF"
  )"; then
    echo "FAIL: cannot resolve Epic ref $ENGINE_REF" | tee -a "$LOG"
    failed+=("$VERSION")
    printf '%s\tFAIL_RESOLVE\t%s\t-\n' "$VERSION" "$ENGINE_REF" >> "$RESULTS"
    cleanup_work "$WORK"
    continue
  fi
  ENGINE_COMMIT="$(printf '%s' "$ENGINE_COMMIT" | tail -n1)"
  echo "[matrix] exact commit: $ENGINE_COMMIT"

  ARGS=(
    build-plugin "$PLUGIN"
    --engine-dir "$ENGINE"
    --cache-dir "$CACHE_ROOT"
    --out "$PACKAGE"
    --configuration Development
    --ref "$ENGINE_COMMIT"
    --backend fuse
    --platform Linux
    --no-pack-cache
  )
  if [[ -n "$RELEASE_MANIFEST" ]]; then ARGS+=(--manifest "$RELEASE_MANIFEST"); fi

  echo "[matrix] workspace:    $WORK"
  echo "[matrix] shared cache: $CACHE_ROOT"
  show_space
  echo
  echo "[matrix] build UE $VERSION"

  UECI_EPIC_GITHUB_TOKEN="$UECI_EPIC_GITHUB_TOKEN" \
    dotnet run --project src/Ueci.Cli/Ueci.Cli.csproj -c Release --no-build -- \
      "${ARGS[@]}" 2>&1 | tee -a "$LOG"
  BUILD_STATUS=${PIPESTATUS[0]}

  # Defensive cleanup for interrupted/failed FUSE children. UECI itself also performs this cleanup,
  # but the matrix runner must remain able to recover after SIGKILL/disk-full scenarios.
  cleanup_mount "$ENGINE/.ueci/mounted-build/engine-view"

  if (( BUILD_STATUS != 0 )); then
    echo "❌ UE $VERSION: BUILD FAILED"
    failed+=("$VERSION")
    printf '%s\tFAIL_BUILD\t%s\t-\n' "$VERSION" "$ENGINE_COMMIT" >> "$RESULTS"
    cleanup_work "$WORK"
    continue
  fi

  PACKAGE_PLUGIN="$PACKAGE/UECIMinimal"
  BINARY="$(find "$PACKAGE_PLUGIN/Binaries" -type f -name '*.so' -print -quit 2>/dev/null || true)"
  if [[ -z "$BINARY" ]]; then
    echo "❌ UE $VERSION: no native .so produced" | tee -a "$LOG"
    find "$PACKAGE_PLUGIN" -maxdepth 5 -type f -print 2>/dev/null | tee -a "$LOG" || true
    failed+=("$VERSION")
    printf '%s\tFAIL_BINARY\t%s\t-\n' "$VERSION" "$ENGINE_COMMIT" >> "$RESULTS"
    cleanup_work "$WORK"
    continue
  fi

  if [[ -f "$PACKAGE_PLUGIN/ueci-build.json" ]]; then
    cp -f "$PACKAGE_PLUGIN/ueci-build.json" "$REPORT"
  fi
  echo "✅ UE $VERSION: $BINARY"
  passed+=("$VERSION")
  printf '%s\tPASS\t%s\t%s\n' "$VERSION" "$ENGINE_COMMIT" "$(basename "$BINARY")" >> "$RESULTS"
  cleanup_work "$WORK"
done

printf '\n\n%s\n%s\n%s\n\n' \
  "================================================================" \
  "                     UECI LOCAL MATRIX" \
  "================================================================"
echo "PASS (${#passed[@]}):"
((${#passed[@]} == 0)) || printf '  %s\n' "${passed[@]}"
echo
echo "FAIL (${#failed[@]}):"
((${#failed[@]} == 0)) || printf '  %s\n' "${failed[@]}"
echo
echo "Detailed results:"
column -t -s $'\t' "$RESULTS" 2>/dev/null || cat "$RESULTS"
echo
echo "Logs:    $LOG_ROOT/ue-<version>.log"
echo "Results: $RESULTS"
echo "Cache:   $CACHE_ROOT"
echo "TMPDIR:  $TMPDIR"
[[ "$KEEP_WORKSPACES" == "1" ]] && echo "Workspaces kept under: $MATRIX_ROOT/work-<version>"

if (( ${#failed[@]} != 0 )); then exit 1; fi
