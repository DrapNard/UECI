#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
# Some distro SDK packages expose Android workload manifests without their corresponding
# auto-import locator SDK. The workload resolver then fails solution restore before it evaluates
# this workload-free project. UECI does not consume optional workloads, so keep restore portable
# by disabling only that resolver for the managed build.
dotnet build Ueci.sln -c Release -p:MSBuildEnableWorkloadResolver=false
dotnet run --project tests/Ueci.Tests/Ueci.Tests.csproj -c Release --no-build -- "$@"
