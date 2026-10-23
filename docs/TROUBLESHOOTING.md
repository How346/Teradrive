# Troubleshooting
* **Build errors on first CI run:** this code has never been compiled; read the error, fix, re-run.
* **"TeraBox not connected":** expected; see TERABOX-INTEGRATION.md.
* **No virtual drive:** not implemented yet.
* **Video won't play:** MediaElement relies on Windows codecs; install the needed codec pack or use another container.
* **Cache full / "pinned or in use":** unpin files, raise the limit, or clear unpinned.
* **Settings reset:** a corrupt settings file is renamed to `settings.json.corrupt` and defaults are used.
* **Logs:** Diagnostics → Export redacted log.
