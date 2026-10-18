# Changelog

## 0.6.0-beta.4 — active-rope responsiveness (not released)

- Keep named-pipe exchange and JSON processing off the UI dispatcher, with one admitted exchange per client and cancellation for queued work.
- Send incremental catalog metadata after the initial bounded snapshot: changed entries, removed IDs and exact ordering. Missed replies or a fresh session trigger a full resynchronization; unchanged summaries keep their existing objects.
- Retain unchanged labelled note controls and decoded sources across catalog refreshes. Compare label values after IPC instead of array identity, while keeping actions bound to the latest summary.
- Add large-reply/UI-context, queued-cancellation, malformed/reordered/missed catalog update and JSON-cloned catalog regression checks. CI also exercises fifty refreshes of a 1,000-note synthetic catalog and retains memory/allocation evidence.
- Reject the software bitmap-cache experiment after slower measured scroll callbacks. Default rendering and motion remain unchanged; active WPF/native graphics memory is still an acceptance concern.

## 0.6.0-beta.3 — memory follow-up (not released)

- Share memory-aware image admission between thumbnails and expanded originals before reading payloads. Rapid expansion and cancellation cannot create unlimited simultaneous native decoders.
- Size expanded image output for the overlay's maximum physical display bounds, including DPI. Resizing stays sharp within the overlay without keeping an oversized fixed-resolution decode on smaller screens.
- Add mixed thumbnail/detail cancellation and corrupt-image recovery checks, plus an explicit decode diagnostic in CI. No animation, renderer-default, storage-format or dependency change.
- Record owner acceptance of beta.2's interaction fixes; broader device and Store release checks remain open.

## 0.6.0-beta.2 — validation candidate (not released)

- Fill the history slider across its navigation pill, including the next-note button at the right edge.
- Dismiss on presses in transparent scroll areas and between notes. A fresh shortcut can reverse outside dismissal while stale visibility replies cannot revive it.
- Renew Windows foreground permission on reopening, and treat the shortcut as open when the rope is behind another app.
- Remove unused sidebar margin reservation from the full-width rendering surface; keep panel dimensions bounded without resizing the native surface for each panel.
- Learn runtime/control preparation costs separately. Retain the smaller idle runtime when empty UI controls cannot fit the memory budget, instead of rejecting all preparation using the listener's larger footprint.
- Close the application when its message-only listener receives a close request, preserving orderly tray/hotkey cleanup. Native CI now installs the real setup at its default location and tests prepared shortcuts, animated outside-dismissal reversals and graceful close.

- Search full text and file paths; filter by date, kind or stars; organize with labels, combine selected text and save editable snippets.
- Preview text transformations, preserve bounded HTML/RTF, use file-location/path and PNG-save actions, and extract editable text with on-demand Windows OCR.
- Password-encrypted backup export, inspection and recoverable merge restore; explicit review/copy of diagnostics; app exclusions, timed pause and optional clear-on-lock.
- Direct native shortcut signaling and reversible close/open motion; session-bound restore cancellation and drained pipe shutdown.
- Detach image decodes directly into native bitmap memory, avoiding a full managed pixel copy. Adapt image admission, preview retention and worker counts to physical memory and process commit headroom.
- Preserve grapheme boundaries in new note previews. Service the self-contained runtime to .NET 10.0.13 and distribute verified third-party notices.
- Separate normal, bundled and Store NuGet lock graphs; split CI into bounded compile, motion, native session, installer upgrade and Store verification stages. Candidate artifacts require all stages to pass.

The public 0.5.1 alpha is unchanged. Windows 11 x64 remains the supported target. This candidate still needs the [beta acceptance checks](docs/beta-checklist.md), installed-device shortcut/trackpad measurements and fresh Store review. Software rendering trades lower sampled graphics-process memory for higher CPU and slower frame callbacks on the measured host; it remains optional. Fresh WPF surface initialization still has measurable startup cost. GitHub EXEs remain unsigned; the ingestion MSIX is for Store certification only.

## 0.5.1 — public alpha · October 13, 2026

- One rendering-driven scroll controller preserves velocity across trackpad updates and reversals. Gesture response follows input/render cadence; accessible offsets commit when physically settled. No per-input animation clocks or delayed completion tasks.
- Recycle viewport note controls during scrolling, retaining correct restore/star/expand/delete actions and releasing old image sources. Visibility reconciliation runs only when the viewport range changes.
- Preview retention follows viewport demand, current physical memory availability and the user's upper limit. Cache reductions evict immediately; image worker concurrency follows memory headroom and CPU availability.
- The tray clipboard listener uses a message-only native window without creating a WPF compositor surface or loading overlay control styles. Existing entrance, hover and panel animations are preserved.
- Size the overlay from measured content and the current monitor before creating its native surface; reuse that surface for panels instead of allocating repeated resize buffers.

Published on GitHub with installer, portable ZIP, checksums, provenance and licence notices. The 0.5.1.0 Microsoft Store package passed fresh certification and its public listing is live on October 13 under ShardCoders. Store-signed installation remains a separate check. GitHub EXEs remain unsigned. Synthetic profiles and isolated native Windows checks do not replace physical trackpad or broad hardware testing.

## 0.5.0 — alpha development build

- Horizontal two-finger trackpad and tilt-wheel scrolling, including fractional deltas, Windows scroll settings and signed multi-monitor coordinates.
- Render-transform scrolling, continuous interrupted motion, layout-free note reordering and immediate slider sizing previews. Keyboard, drag and reduced-motion paths remain available.
- Reuse unchanged note visuals; cancel offscreen thumbnail work and animation clocks. Late image loads wipe encoded payloads and cannot repopulate a cleared cache.
- A transparent rope surface capped at 720 device-independent pixels, reusing it for settings and full notes without extra resize buffers. Background clicks dismiss through window deactivation.
- Optional lower-RAM software rendering under Storage settings, applied after Save on the next opening. Hardware rendering remains the default for smoother motion.
- Five-minute native capture soak with rapid copy bursts, two 2048×2048 images, file references, bounded encrypted history and restart validation on isolated CI.

This build does not replace the 0.4.0.0 package already under Microsoft Store review. Physical trackpads, mixed-DPI devices, third-party destinations and Store-signed installation remain validation gates.

## 0.4.0 — alpha release candidate

- Continuous damped springs preserve position and velocity when motion changes direction. Notes follow the rope with render transforms; moving blur surfaces are removed.
- The rope runs in a disposable overlay process, releasing graphics allocations when dismissed. The tray/listener keeps history metadata and loads encrypted content on demand through a bounded, current-user-only pipe.
- Clipboard reads and file checks have three-second response deadlines and cannot accumulate blocked workers. Image decodes detach from encoded buffers.
- Six themes, live appearance previews that commit on Save, note expansion, preview search, type filters, polished controls, accessible tooltip colors and feedback links.
- Narrow-screen wrapping, system high-contrast colors and live reduced-motion updates.
- Assigned Microsoft Store identity, opt-in packaged startup, self-contained Windows 11 x64 MSIX, provenance/checksums and isolated native paste/restart tests.

The candidate has not been published as a stable release. Store certification, signed installation and the remaining device/application matrix are tracked in [the release checklist](docs/release-checklist.md). GitHub EXE artifacts remain unsigned.
