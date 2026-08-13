#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:?usage: prepare-legacy-release-deps.sh VERSION RELEASE_REF ENGINE_DIR}"
RELEASE_REF="${2:?usage: prepare-legacy-release-deps.sh VERSION RELEASE_REF ENGINE_DIR}"
ENGINE_DIR="${3:?usage: prepare-legacy-release-deps.sh VERSION RELEASE_REF ENGINE_DIR}"

# Commit.gitdeps.xml replaced the large release ZIP workflow in UE4.6. UE4.5 therefore needs the
# historical Required archives, and Linux additionally needs Optional.zip. They are extracted into
# the mounted backend's writable upper layer so the Git tree remains blobless and disposable.
if [[ "$VERSION" != "4.5" ]]; then
  exit 0
fi
if [[ -z "$RELEASE_REF" ]]; then
  echo "UE4.5 legacy dependencies require an exact Epic release tag." >&2
  exit 2
fi
if ! command -v gh >/dev/null 2>&1; then
  echo "gh is required to fetch UE4.5 release dependency archives." >&2
  exit 2
fi
if ! command -v unzip >/dev/null 2>&1; then
  echo "unzip is required to prepare UE4.5 release dependency archives." >&2
  exit 2
fi

UPPER="$ENGINE_DIR/.ueci/mounted-build/state/upper"
STATE="$ENGINE_DIR/.ueci/mounted-build/state"
MARKER="$STATE/legacy-release-deps.tag"
mkdir -p "$UPPER" "$STATE"
if [[ -f "$MARKER" ]] && [[ "$(cat "$MARKER")" == "$RELEASE_REF" ]]; then
  echo "[matrix] UE4.5 legacy release dependencies already prepared for $RELEASE_REF"
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
  echo "Epic release $RELEASE_REF is missing required legacy assets: ${selection#*$'\t'}" >&2
  exit 1
fi
if [[ -z "$selection" ]]; then
  echo "Epic release $RELEASE_REF exposes no legacy dependency archives." >&2
  exit 1
fi

ARCHIVE_DIR="${TMPDIR:-${XDG_CACHE_HOME:-$HOME/.cache}}/ueci-legacy-assets-$VERSION-$$"
mkdir -p "$ARCHIVE_DIR"
trap 'rm -rf -- "$ARCHIVE_DIR"' EXIT

while IFS=$'\t' read -r name url; do
  [[ -n "$name" && -n "$url" ]] || continue
  archive="$ARCHIVE_DIR/$name"
  echo "[matrix] downloading UE4.5 legacy dependency asset: $name"
  gh api "$url" -H 'Accept: application/octet-stream' > "$archive"
  [[ -s "$archive" ]] || { echo "Downloaded asset is empty: $name" >&2; exit 1; }
  echo "[matrix] extracting $name into the FUSE upper overlay"
  unzip -q -o "$archive" -d "$UPPER"
done <<<"$selection"

printf '%s\n' "$RELEASE_REF" > "$MARKER"
echo "[matrix] UE4.5 legacy dependencies prepared in $UPPER"
