# Architecture
* **Core** – models, `ICloudStorageProvider`, capabilities, path safety, settings, redaction, error translation.
* **Cloud** – `LocalFileSystemProvider` (test), `TeraBoxProvider` (blocked placeholder).
* **Cache** – `CacheManager`: objects/ + tmp/ + `index.json`; temp→verify (size, SHA-256)→atomic move; LRU eviction skipping pinned and leased entries; startup recovery; shared in-flight fetches. JSON index instead of SQLite to keep zero dependencies.
* **Transfers** – `TransferManager` (bounded channels, separate worker pools, retry with equal-jitter exponential backoff, restart-based pause/resume), `CachedFileService`.
* **VirtualDrive** – WinFsp detection + honest unavailable service. Planned: a WinFsp filesystem adapter mapping directory listing/metadata/read to `ICloudStorageProvider` + `CachedFileService`, with read-only first; requires the WinFsp runtime (https://winfsp.dev), installed separately with elevation. Not built.
* **Media / Games** – video extension filter + playback-position store; launch-shortcut library.
* **App** – WPF MVVM (`MainViewModel`), dark/light theme dictionaries, no third-party UI packages.
