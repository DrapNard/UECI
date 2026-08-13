#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:?usage: resolve-release-gitdeps.sh VERSION DESTINATION [REF_DESTINATION]}"
DEST="${2:?usage: resolve-release-gitdeps.sh VERSION DESTINATION [REF_DESTINATION]}"
REF_DEST="${3:-}"

# Historical Epic releases can carry a corrected Commit.gitdeps.xml as a release asset.
# This helper is intentionally best-effort: if no matching asset exists, print an empty line and
# let UECI use the manifest tracked by the requested Engine branch. When REF_DEST is supplied, the
# matching release tag is written there as well so callers never mix an asset with a different
# branch-tip commit.
if [[ -n "$REF_DEST" ]]; then rm -f -- "$REF_DEST"; fi
if ! command -v gh >/dev/null 2>&1 || [[ -z "${GH_TOKEN:-${GITHUB_TOKEN:-}}" ]]; then
  printf '\n'
  exit 0
fi

json="$(gh api --paginate 'repos/EpicGames/UnrealEngine/releases?per_page=100' 2>/dev/null || true)"
if [[ -z "$json" ]]; then
  printf '\n'
  exit 0
fi

selection="$(python3 -c '
import json, re, sys
version=sys.argv[1]
raw=sys.stdin.read().strip()
if not raw:
    raise SystemExit(0)
pages=[]
dec=json.JSONDecoder(); pos=0
while pos < len(raw):
    while pos < len(raw) and raw[pos].isspace(): pos += 1
    if pos >= len(raw): break
    obj, pos = dec.raw_decode(raw, pos)
    pages.extend(obj if isinstance(obj, list) else [obj])
def key(tag):
    nums=[int(x) for x in re.findall(r"\d+", tag)]
    return nums + [0] * (4-len(nums))
candidates=[]
for release in pages:
    tag=str(release.get("tag_name") or "")
    normalized=tag.lower().removesuffix("-release")
    if not (normalized == version or normalized.startswith(version + ".") or normalized.startswith(version + "-")):
        continue
    for asset in release.get("assets") or []:
        if str(asset.get("name") or "").lower() == "commit.gitdeps.xml":
            candidates.append((key(normalized), tag, str(asset.get("url") or "")))
if candidates:
    _, tag, url=sorted(candidates, reverse=True)[0]
    print(tag + "\t" + url)
' "$VERSION" <<<"$json")"

if [[ -z "$selection" ]]; then
  printf '\n'
  exit 0
fi
release_tag="${selection%%$'\t'*}"
asset_url="${selection#*$'\t'}"
if [[ -z "$release_tag" || -z "$asset_url" || "$asset_url" == "$selection" ]]; then
  printf '\n'
  exit 0
fi

mkdir -p "$(dirname "$DEST")"
tmp="$DEST.$$.tmp"
trap 'rm -f -- "$tmp"' EXIT
gh api "$asset_url" -H 'Accept: application/octet-stream' > "$tmp"
if [[ ! -s "$tmp" ]] || ! grep -q '<' "$tmp"; then
  printf '\n'
  exit 0
fi
mv -f -- "$tmp" "$DEST"
trap - EXIT
if [[ -n "$REF_DEST" ]]; then
  mkdir -p "$(dirname "$REF_DEST")"
  printf '%s\n' "$release_tag" > "$REF_DEST"
fi
printf '%s\n' "$DEST"
