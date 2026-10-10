# Privacy and storage

Capture runs only while the app is running and enabled. The app does not read historical Windows clipboard records, capture keyboard input, take screenshots, or upload clipboard content.

Regular builds use `%LOCALAPPDATA%\ShardApps\Clippboard\history`. `--dev` uses ignored repository `.data/history`. SDK, caches, outputs, and temporary development files stay in the project. Runtime history is never committed.

Each entry is encrypted with DPAPI `CurrentUser` before atomic file replacement. There is no plaintext history index or content log. Short summaries stay in process memory; restore necessarily decrypts payloads and places them on the system clipboard.

| Limit | Default |
| --- | --- |
| Unpinned lifetime | 30 days |
| Entries, including pins | 500 |
| Encrypted history payloads | 256 MiB |
| One encrypted entry | 24 MiB |
| Text | 524,288 UTF-16 code units |
| Image | 16 million pixels / 16 MiB encoded PNG |
| File references per entry | 100 |
| Rendered notes | 40 |

Expiry runs at startup, before additions, and every 15 minutes. Old unpinned entries are evicted at capacity. Pins survive expiry/normal eviction but consume the total budget; new clips are rejected when pins leave no room. Duplicate content refreshes the note and preserves its pin.

Pins are not backups. Clear/delete, disk failure, account loss, and damaged records can remove them. Startup removes unreadable/oversized records and reports the count without logging contents. File contents are not stored, only original paths. Unsupported/oversized clipboard items are skipped.

## Sensitive data and deletion

The app checks `ExcludeClipboardContentFromMonitorProcessing` and supported zero-valued `CanIncludeInClipboardHistory` markers. Apps without markers can expose secrets to history. Pause before copying private data; pause is a session control and does not yet survive restart.

× deletes an entry; Clear history includes pins. Deletion removes history files but does not clear the system clipboard or guarantee forensic erasure on SSDs/backups. Uninstall preserves history for upgrades; clear it first if desired. Google Drive or other backup software may independently sync development `.data/`; use synthetic data in this synced workspace.

No app network service, updater, telemetry, or crash upload is included. The project menu link opens GitHub in your browser. Development and CI use package/build services.
