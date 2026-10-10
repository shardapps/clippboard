# Changelog

## 0.4.0 — alpha release candidate

- Continuous damped springs preserve position and velocity when motion changes direction. Notes follow the rope with render transforms; moving blur surfaces are removed.
- The rope runs in a disposable overlay process, releasing graphics allocations when dismissed. The tray/listener keeps history metadata and loads encrypted content on demand through a bounded, current-user-only pipe.
- Clipboard reads and file checks have three-second response deadlines and cannot accumulate blocked workers. Image decodes detach from encoded buffers.
- Six themes, live appearance previews that commit on Save, note expansion, preview search, type filters, polished controls, accessible tooltip colors and feedback links.
- Narrow-screen wrapping, system high-contrast colors and live reduced-motion updates.
- Assigned Microsoft Store identity, opt-in packaged startup, self-contained Windows 11 x64 MSIX, provenance/checksums and isolated native paste/restart tests.

The candidate has not been published as a stable release. Store certification, signed installation and the remaining device/application matrix are tracked in [the release checklist](docs/release-checklist.md). GitHub EXE artifacts remain unsigned.
