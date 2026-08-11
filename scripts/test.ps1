$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root
dotnet build Ueci.sln -c Release
dotnet run --project tests/Ueci.Tests/Ueci.Tests.csproj -c Release --no-build -- @args
