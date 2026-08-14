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
if [[ -f "$MARKER" ]] && [[ "$(cat "$MARKER")" == "$RELEASE_REF"* ]]; then
  echo "[matrix] UE$VERSION legacy release dependencies already prepared for $RELEASE_REF"
  exit 0
fi

select_assets_from_release_json() {
  python3 -c '
import json, sys
release=json.load(sys.stdin)
assets={str(a.get("name") or "").lower(): a for a in release.get("assets") or []}
missing=sorted(name for name in ("required_1of2.zip", "required_2of2.zip") if name not in assets)
if missing:
    print("MISSING\t" + ",".join(missing))
    raise SystemExit(0)
for name in ("required_1of2.zip", "required_2of2.zip", "optional.zip"):
    asset=assets.get(name)
    if asset:
        print(str(asset.get("name") or "") + "\t" + str(asset.get("url") or ""))
'
}

release_json="$(gh api "repos/EpicGames/UnrealEngine/releases/tags/$RELEASE_REF")"
selection="$(select_assets_from_release_json <<<"$release_json")"
asset_release_ref="$RELEASE_REF"

# Some transitional UE4.6 patch tags kept source releases separate from the dependency release
# carrying Required_1of2/Required_2of2. If the exact source tag has no complete set, first search
# sibling 4.6.x releases. A few GitHub histories expose no complete 4.6 archive set at all; in that
# case use a 4.5 dependency release only as a source for the two managed UBT support assemblies.
# The source commit always remains pinned to RELEASE_REF.
legacy_support_only=0
if [[ "$selection" == MISSING$'\t'* && "$VERSION" == "4.6" ]]; then
  for search_version in "4.6" "4.5"; do
    [[ "$selection" == MISSING$'\t'* ]] || break
    for page in $(seq 1 20); do
      page_json="$(gh api "repos/EpicGames/UnrealEngine/releases?per_page=100&page=$page")"
      count="$(python3 -c 'import json,sys; print(len(json.load(sys.stdin)))' <<<"$page_json")"
      [[ "$count" != "0" ]] || break

      candidate="$(python3 -c '
import json, sys
version=sys.argv[1]
for release in json.load(sys.stdin):
    tag=str(release.get("tag_name") or "")
    if not (tag == version or tag.startswith(version + ".") or tag.startswith(version + "-")):
        continue
    assets={str(a.get("name") or "").lower(): a for a in release.get("assets") or []}
    if not all(name in assets for name in ("required_1of2.zip", "required_2of2.zip")):
        continue
    print("SOURCE\t" + tag)
    for name in ("required_1of2.zip", "required_2of2.zip", "optional.zip"):
        asset=assets.get(name)
        if asset:
            print(str(asset.get("name") or "") + "\t" + str(asset.get("url") or ""))
    break
' "$search_version" <<<"$page_json")"
      if [[ "$candidate" == SOURCE$'\t'* ]]; then
        asset_release_ref="$(head -n1 <<<"$candidate" | cut -f2-)"
        selection="$(tail -n +2 <<<"$candidate")"
        if [[ "$search_version" == "4.5" ]]; then
          legacy_support_only=1
          echo "[matrix] UE4.6 managed support archives found on legacy release $asset_release_ref"
        else
          echo "[matrix] UE4.6 dependency archives found on sibling release $asset_release_ref"
        fi
        break
      fi
    done
  done
fi

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

extract_managed_support() {
  local archive="$1"
  local found=0
  local entry base destination
  while IFS= read -r entry; do
    [[ -n "$entry" ]] || continue
    base="${entry##*/}"
    case "${base,,}" in
      ionic.zip.reduced.dll|ionic.zip.dll|rpcutility.exe|rpcutility.dll) ;;
      *) continue ;;
    esac
    destination="$UPPER/Engine/Binaries/DotNET/$base"
    mkdir -p "$(dirname "$destination")"
    unzip -p "$archive" "$entry" > "$destination"
    found=1
  done < <(unzip -Z1 "$archive" | grep -Ei '(^|/)(Ionic\.Zip(\.Reduced)?\.dll|RPCUtility\.(exe|dll))$' || true)
  return $(( found == 1 ? 0 : 1 ))
}

while IFS=$'\t' read -r name url; do
  [[ -n "$name" && -n "$url" ]] || continue
  archive="$ARCHIVE_DIR/$name"
  echo "[matrix] downloading UE$VERSION legacy dependency asset: $name"
  gh api "$url" -H 'Accept: application/octet-stream' > "$archive"
  [[ -s "$archive" ]] || { echo "Downloaded asset is empty: $name" >&2; exit 1; }
  if [[ "$VERSION" == "4.6" && "$legacy_support_only" == "1" ]]; then
    echo "[matrix] extracting only UE4.6 managed UBT support from $name"
    extract_managed_support "$archive" || true
  else
    echo "[matrix] extracting $name into the FUSE upper overlay"
    unzip -q -o "$archive" -d "$UPPER"
  fi
done <<<"$selection"

if [[ "$VERSION" == "4.6" && "$legacy_support_only" == "1" ]]; then
  ionic="$UPPER/Engine/Binaries/DotNET/Ionic.Zip.Reduced.dll"
  [[ -f "$ionic" ]] || ionic="$UPPER/Engine/Binaries/DotNET/Ionic.Zip.dll"
  rpc="$UPPER/Engine/Binaries/DotNET/RPCUtility.exe"
  [[ -f "$rpc" ]] || rpc="$UPPER/Engine/Binaries/DotNET/RPCUtility.dll"
  if [[ ! -f "$ionic" || ! -f "$rpc" ]]; then
    echo "Legacy release $asset_release_ref did not contain both Ionic.Zip and RPCUtility support files." >&2
    exit 1
  fi
fi

printf '%s\n' "$RELEASE_REF|$asset_release_ref" > "$MARKER"
echo "[matrix] UE$VERSION legacy dependencies prepared in $UPPER (assets: $asset_release_ref)"
