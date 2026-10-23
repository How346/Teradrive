param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
dotnet restore TeraDrive.sln; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet build TeraDrive.sln -c $Configuration --no-restore; if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet test TeraDrive.sln -c $Configuration --no-build; if ($LASTEXITCODE) { exit $LASTEXITCODE }
