# TeraDrive
*Your cloud. Your drive. Your speed.*

A Windows (WPF, .NET 10) cloud-drive client with a smart local cache, transfer queue, video library and game launcher.

> **Read this first — honest status.** This repository was written in an environment with **no .NET SDK and no Windows**,
> so **nothing has been compiled, run or tested yet**. The first GitHub Actions run is the first real compile.
> Expect to fix a few compile errors. Two headline goals are **blocked / not implemented** (below).

## Implementation status

| Area | Status |
|---|---|
| Provider abstraction (`ICloudStorageProvider`) + capability model | Written, **untested** |
| Local-folder test provider | Written, unit tests written (**not run**) |
| **TeraBox provider** | **Blocked.** No officially documented personal-drive API could be verified; every integration found is reverse-engineered and needs browser cookies/tokens, which this project refuses to use. The provider reports *no capabilities* and every call fails with a clear message. See `docs/TERABOX-INTEGRATION.md`. |
| Smart cache (limit, LRU, pin, leases, SHA-256 verify, recovery, duplicate-fetch prevention) | Written, tests written (**not run**) |
| Transfer manager (queues, retry+jitter, pause/resume*, cancel, speed/ETA) | Written, tests written (**not run**) |
| **Virtual drive (WinFsp)** | **Not implemented.** Only WinFsp-runtime detection and an honest "not mounted" status exist. |
| WPF UI: Overview, Files, Transfers, Smart Cache, Videos, Games, Settings, Diagnostics | Written, **never launched** |
| Video playback (WPF `MediaElement`) | Written, **untested**. Files are fully cached before playing (no range streaming). No subtitle support. Codec support = whatever Windows has. |
| Game library (launch shortcuts) | Written, **untested**. No auto-detection, no artwork. |
| Not implemented | Recent Files / Favorites pages, persisted transfer history, SQLite index (JSON used), Microsoft.Extensions.Logging/DI host (small in-house log used), drag-and-drop, game-save backup, notifications/startup options, installer |

\* Pause/resume and retry **restart the transfer from byte 0**; partial-file resume is intentionally not attempted.

## Requirements
Windows 10/11 x64. To build: .NET SDK 10.0.x (`global.json`). No other runtime dependencies (zero third-party packages in the app; xUnit in tests).

## Build
```powershell
./scripts/build.ps1          # restore, build Release, test
./scripts/publish.ps1        # self-contained single-file win-x64 + portable ZIP
```
Or push to GitHub: `.github/workflows/build.yml` restores, builds, tests, publishes and uploads
**TeraDrive-win-x64-portable** as a workflow artifact (Actions → run → Artifacts). Artifacts expire and are *not* a permanent public
download. Push a tag like `v0.1.0` to run `release.yml`, which creates a GitHub Release with the ZIP after tests pass.

## Using it
* **Provider:** a local folder (default `Documents\TeraDrive-LocalProvider`) stands in for a cloud so the app is usable and testable. It is always labelled "Local folder (test provider)" — it is not TeraBox.
* **Cache:** Smart Cache page (5/10/25/50/100 GB presets, custom in Settings, custom folder in Settings). *Keep offline (pin)* in Files protects a file from eviction. Clearing only removes cached copies.
* **Transfers:** Import/Download in Files; manage on Transfers. Existing files are never silently overwritten.
* **Videos:** double-click a video; it is cached, then played. Position is remembered.
* **Games:** add an installed game's executable; cloud storage is for installers/backups, not for running games.
* **Virtual drive:** not available yet. Diagnostics → "Try to mount" explains why.

## Security notes
No passwords/cookies/tokens are stored, requested or logged; logs are redacted (`Redactor`). Paths are validated against traversal. No TLS validation is altered. The app runs as a normal user (`asInvoker`).

## Troubleshooting / uninstall
See `docs/TROUBLESHOOTING.md`. Uninstall = delete the extracted folder and `%LOCALAPPDATA%\TeraDrive` (settings, cache, library).
