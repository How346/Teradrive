# Building
Install .NET SDK 10.0.x (see `global.json`). On Windows: `./scripts/build.ps1`, then `./scripts/publish.ps1`.
Library and test projects target `net10.0` and can be built/tested on any OS; `TeraDrive.App` needs Windows (`net10.0-windows`, WPF).
CI: `.github/workflows/build.yml` (all pushes/PRs) and `release.yml` (tags `v*`). Single-file publish uses `IncludeNativeLibrariesForSelfExtract`;
no WinFsp or other external component is required by the current build.
