param(
  [string]$PublishDir = "publish/app",
  [string]$Output = "artifacts/TeraDrive-win-x64-portable.zip"
)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
if (-not (Test-Path $PublishDir)) {
  dotnet publish src/TeraDrive.App/TeraDrive.App.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $PublishDir
  if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
$stage = Join-Path ([IO.Path]::GetTempPath()) ("teradrive-pkg-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item "$PublishDir/*" $stage -Recurse
Copy-Item README.md, LICENSE $stage
Copy-Item docs $stage -Recurse
@"
TeraDrive portable package
1. Extract anywhere and run TeraDrive.exe (no installer, no admin rights needed).
2. The virtual drive is NOT implemented in this version, so WinFsp is not required yet.
3. TeraBox is NOT connectable (no supported personal-drive API). Use the local test provider.
See README.md and docs/ for limitations.
"@ | Set-Content (Join-Path $stage "SETUP.txt")
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null
if (Test-Path $Output) { Remove-Item $Output }
Compress-Archive -Path "$stage/*" -DestinationPath $Output
Remove-Item $stage -Recurse -Force
Write-Host "Created $Output"
