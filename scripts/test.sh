#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
dotnet build Ueci.sln -c Release
dotnet run --project tests/Ueci.Tests/Ueci.Tests.csproj -c Release --no-build -- "$@"
