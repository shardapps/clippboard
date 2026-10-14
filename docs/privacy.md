# Privacy and storage

Public policy for users and the Microsoft Store: [Clippboard Privacy Policy](https://shardapps.github.io/clippboard/privacy/), published by ShardCoders. This document provides additional implementation details. The static policy site adds no analytics, scripts, forms or cookies; GitHub Pages hosting follows [GitHub's Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

Capture runs only while the app is running and enabled. The app does not read historical Windows clipboard records, capture keyboard input, take screenshots, or upload clipboard content.

Regular builds use `%LOCALAPPDATA%\ShardApps\Clippboard\history`. `--dev` uses ignored repository `.data/history`. SDK, caches, outputs, and temporary development files stay in the project. Runtime history is never committed.

Each entry is encrypted with DPAPI `CurrentUser` before atomic file replacement. There is no plaintext history index or content log. Short summaries stay in process memory; restore necessarily decrypts payloads and places them on the system clipboard.

| Limit | Default |
| --- | --- |
| Lifetime, including stars | Configurable 1–30 days |
| Entries, including pins | 500 |
| Encrypted history payloads | 256 MiB |
| One encrypted entry | 24 MiB |
| Text | 524,288 UTF-16 code units |
| Image | 16 million pixels / 16 MiB encoded PNG |
| File references per entry | 100 |
| Rendered notes | Viewport + overscan |
| Image preview cache | Configurable 4–64 MiB; default 16 MiB |

Expiry runs at startup, before additions/reads, and every 15 minutes. Stars survive normal capacity eviction but still expire after the configured period (maximum 30 days). New clips are rejected when stars leave no room. Duplicate content refreshes the note and preserves its star. Lower budgets are rejected if unexpired stars alone exceed them; delete or unstar items before retrying.

Pins are not backups. Clear/delete, disk failure, account loss, and damaged records can remove them. Startup removes unreadable/oversized records and reports the count without logging contents. File contents are not stored, only original paths. Unsupported/oversized clipboard items are skipped.

## Sensitive data and deletion

The app checks `ExcludeClipboardContentFromMonitorProcessing` and supported zero-valued `CanIncludeInClipboardHistory` markers. Apps without markers can expose secrets to history. Pause before copying private data; capture preferences, including pause, persist across restarts.

Next-beta source adds exact executable-name exclusions and saved timed pauses. Exclusions match the [Windows clipboard owner](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardowner), which can be absent or differ from the foreground app. When exclusions are active, unknown/unavailable owners are skipped. This cannot detect private browser tabs or secrets inside an allowed app. Rules are limited to 64 names and 1,024 total characters; paths and wildcards are rejected.

The optional clear-on-lock preference deletes saved history including stars, while the system clipboard remains separate. Capture is suspended while locked; queued writes and late reader results are invalidated. Unlock waits for deletion, and failed deletion keeps capture suspended with a retry action. A persistent `clear.pending` marker contains no clipboard content and allows startup to finish interrupted deletion. If [session notifications](https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.systemevents.sessionswitch?view=windowsdesktop-10.0) are unavailable, clear-on-lock cannot be enabled; an already enabled preference suspends capture until disabled. Physical lock/unlock and remote-session testing remain beta gates.

× deletes an entry; Clear history includes pins. Deletion removes history files but does not clear the system clipboard or guarantee forensic erasure on SSDs/backups. The EXE uninstaller preserves history for upgrades; clear history inside the app before uninstalling if you want it removed. Store-installed uninstall behavior still requires validation. Google Drive or other backup software may independently sync development `.data/`; use synthetic data in this synced workspace.

No app network service, updater, telemetry, or crash upload is included. The project menu link opens GitHub in your browser. Development and CI use package/build services.

Encrypted `.meta` files hold short summaries and payload size/timestamp references, so normal startup does not decrypt image payloads. Missing/stale metadata is rebuilt from the authoritative encrypted payload. Both payloads and metadata are removed on deletion/expiry. Temporary atomic-write files are removed after use and at startup. Settings contain only configuration and are stored separately as JSON; no clipboard payload is written to the settings file.

Image previews decode only when visible, fit within 360 × 230 pixels, and use byte-bounded LRU eviction. At most two thumbnail loads/decode operations run at once. Expanded images fit within 1,600 × 1,000 pixels. Closing cancels pending loads and removes image, detail and card references. No decrypted image temp files are created; encrypted history remains on disk until deletion, expiry or eviction.
