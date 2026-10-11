# Changelog

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
