# TeraBox integration findings

**Result: blocked. No TeraBox connector is implemented.**

Searches (October 2026) for official TeraBox developer documentation covering personal-drive access (OAuth-style sign-in, list, read, upload,
quota) found **no officially documented API I could verify**. What exists publicly is third-party and reverse-engineered:
Python/JS clients that require the user to copy a `jsToken` and the `ndus` session cookie out of browser developer tools, and share-link
"resolver" proxies. Share-link resolution is also not access to a user's personal drive.

This project's rules forbid undocumented endpoints, cookie/token extraction and auth circumvention, so none of that is used.

**What exists:** `TeraBoxProvider` reports `ProviderCapabilities.None`; `ConnectAsync` and all operations throw `UnsupportedOperationException`.
The UI says TeraBox is not connected and why.

**To unblock:** if TeraBox publishes (or you obtain) an official API with a supported sign-in flow, implement `ICloudStorageProvider` in
`src/TeraDrive.Cloud`, load developer credentials from environment variables/Windows Credential Manager (never source or YAML), set `Capabilities`
to what the API truly offers, and add tests against a mock HTTP handler. Nothing else in the app needs to change.
Please verify the current official docs yourself; this finding reflects limited web searching, not an exhaustive review.
