#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:?usage: resolve-release-gitdeps.sh VERSION DESTINATION [REF_DESTINATION]}"
DEST="${2:?usage: resolve-release-gitdeps.sh VERSION DESTINATION [REF_DESTINATION]}"
REF_DEST="${3:-}"

# Resolve the highest patch release belonging to VERSION. If that release carries a corrected
# Commit.gitdeps.xml asset, download it; otherwise return an empty manifest path but still write the
# exact release tag to REF_DEST. This keeps the matrix immutable even for releases that rely on the
# manifest tracked in Git (and for UE4.5, which predates Commit.gitdeps.xml entirely).
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
    return tuple(nums + [0] * (4-len(nums)))
candidates=[]
for release in pages:
    tag=str(release.get("tag_name") or "")
    normalized=tag.lower().removesuffix("-release")
    if not (normalized == version or normalized.startswith(version + ".") or normalized.startswith(version + "-")):
        continue
    commit_url=""
    for asset in release.get("assets") or []:
        if str(asset.get("name") or "").lower() == "commit.gitdeps.xml":
            commit_url=str(asset.get("url") or "")
            break
    candidates.append((key(normalized), tag, commit_url))
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

if [[ -n "$REF_DEST" && -n "$release_tag" ]]; then
  mkdir -p "$(dirname "$REF_DEST")"
  printf '%s\n' "$release_tag" > "$REF_DEST"
fi

if [[ -z "$asset_url" || "$asset_url" == "$selection" ]]; then
  # UE4.6 release pages do not attach the manifest, but the source tag tracks it beneath
  # Engine/Build. Prefer the release asset when available; otherwise materialize this authoritative
  # source copy so callers such as the legacy dependency overlay can validate every fallback file.
  mkdir -p "$(dirname "$DEST")"
  tmp="$DEST.$$.tmp"
  trap 'rm -f -- "$tmp"' EXIT
  for tracked_path in 'Engine/Build/Commit.gitdeps.xml' 'Commit.gitdeps.xml'; do
    if gh api -H 'Accept: application/vnd.github.raw+json' \
        "repos/EpicGames/UnrealEngine/contents/$tracked_path?ref=$release_tag" > "$tmp" 2>/dev/null \
        && [[ -s "$tmp" ]] && grep -q '<' "$tmp"; then
      mv -f -- "$tmp" "$DEST"
      trap - EXIT
      printf '%s\n' "$DEST"
      exit 0
    fi
  done
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
printf '%s\n' "$DEST"
