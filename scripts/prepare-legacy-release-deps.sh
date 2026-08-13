#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:?usage: prepare-legacy-release-deps.sh VERSION RELEASE_REF ENGINE_DIR}"
RELEASE_REF="${2:?usage: prepare-legacy-release-deps.sh VERSION RELEASE_REF ENGINE_DIR}"
ENGINE_DIR="${3:?usage: prepare-legacy-release-deps.sh VERSION RELEASE_REF ENGINE_DIR}"

# UE4.5 requires the historical Required/Optional archives. UE4.6 introduced Commit.gitdeps.xml,
# but some release tags still expose the old archives and their managed UBT support binaries are
# useful when the transitional manifest does not carry Ionic.Zip/RPCUtility. Probe those assets for
# 4.6 as an optional compatibility overlay; all later releases use GitDependencies only.
if [[ "$VERSION" != "4.5" && "$VERSION" != "4.6" ]]; then
  exit 0
fi
if [[ -z "$RELEASE_REF" ]]; then
  echo "UE$VERSION legacy dependencies require an exact Epic release tag." >&2
  exit 2
fi
if ! command -v gh >/dev/null 2>&1; then
  echo "gh is required to fetch UE$VERSION release dependency archives." >&2
  exit 2
fi
if ! command -v unzip >/dev/null 2>&1; then
  echo "unzip is required to prepare UE$VERSION release dependency archives." >&2
  exit 2
fi

UPPER="$ENGINE_DIR/.ueci/mounted-build/state/upper"
STATE="$ENGINE_DIR/.ueci/mounted-build/state"
MARKER="$STATE/legacy-release-deps.tag"
mkdir -p "$UPPER" "$STATE"
if [[ -f "$MARKER" ]] && [[ "$(cat "$MARKER")" == "$RELEASE_REF" ]]; then
  echo "[matrix] UE$VERSION legacy release dependencies already prepared for $RELEASE_REF"
  exit 0
fi

release_json="$(gh api "repos/EpicGames/UnrealEngine/releases/tags/$RELEASE_REF")"
selection="$(python3 -c '
import json, sys
release=json.load(sys.stdin)
wanted={"required_1of2.zip", "required_2of2.zip", "optional.zip"}
assets={str(a.get("name") or "").lower(): a for a in release.get("assets") or []}
missing=sorted(name for name in ("required_1of2.zip", "required_2of2.zip") if name not in assets)
if missing:
    print("MISSING\t" + ",".join(missing))
    raise SystemExit(0)
for name in ("required_1of2.zip", "required_2of2.zip", "optional.zip"):
    asset=assets.get(name)
    if asset:
        print(str(asset.get("name") or "") + "\t" + str(asset.get("url") or ""))
' <<<"$release_json")"

if [[ "$selection" == MISSING$'\t'* ]]; then
  if [[ "$VERSION" == "4.5" ]]; then
    echo "Epic release $RELEASE_REF is missing required legacy assets: ${selection#*$'\t'}" >&2
    exit 1
  fi
  echo "[matrix] UE$VERSION exposes no complete legacy Required archive set; using Commit.gitdeps.xml only"
  exit 0
fi
if [[ -z "$selection" ]]; then
  if [[ "$VERSION" == "4.5" ]]; then
    echo "Epic release $RELEASE_REF exposes no legacy dependency archives." >&2
    exit 1
  fi
  exit 0
fi

ARCHIVE_DIR="${TMPDIR:-${XDG_CACHE_HOME:-$HOME/.cache}}/ueci-legacy-assets-$VERSION-$$"
mkdir -p "$ARCHIVE_DIR"
trap 'rm -rf -- "$ARCHIVE_DIR"' EXIT

while IFS=$'\t' read -r name url; do
  [[ -n "$name" && -n "$url" ]] || continue
  archive="$ARCHIVE_DIR/$name"
  echo "[matrix] downloading UE$VERSION legacy dependency asset: $name"
  gh api "$url" -H 'Accept: application/octet-stream' > "$archive"
  [[ -s "$archive" ]] || { echo "Downloaded asset is empty: $name" >&2; exit 1; }
  echo "[matrix] extracting $name into the FUSE upper overlay"
  unzip -q -o "$archive" -d "$UPPER"
done <<<"$selection"

printf '%s\n' "$RELEASE_REF" > "$MARKER"
echo "[matrix] UE$VERSION legacy dependencies prepared in $UPPER"
