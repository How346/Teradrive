# Security
No credentials are collected or stored. `Redactor` strips password/token/cookie/bearer values from the diagnostics log and exports.
Provider paths reject `..`, backslashes and invalid characters; the local provider re-checks confinement to its root. Destructive provider
deletes require confirmation; cache clearing never touches originals. Temp files use random names. No TLS validation is disabled (no network code yet).
The app requests no administrator rights. Any future WinFsp install is a separate elevated step. Report issues via GitHub.
