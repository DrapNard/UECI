#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${1:?usage: prepare-compat-fixture.sh VERSION DESTINATION}"
DEST="${2:?usage: prepare-compat-fixture.sh VERSION DESTINATION}"

rm -rf -- "$DEST"
mkdir -p "$DEST"
cp -a "$ROOT/fixtures/MinimalPlugin/." "$DEST/"

major="${VERSION%%.*}"
minor="${VERSION#*.}"
minor="${minor%%.*}"

# ModuleRules switched from TargetInfo to ReadOnlyTargetRules in the UE4.16 generation.
# Keep the fixture itself compatible with the branch under test; UECI separately feature-detects
# the synthetic host rules from the exact UBT source tree.
if (( major == 4 && minor < 16 )); then
  cat > "$DEST/Source/UECIMinimal/UECIMinimal.Build.cs" <<'EOF'
using UnrealBuildTool;

public class UECIMinimal : ModuleRules
{
    public UECIMinimal(TargetInfo Target)
    {
        PrivateDependencyModuleNames.Add("Core");
    }
}
EOF
fi

printf '%s\n' "$DEST/UECIMinimal.uplugin"
