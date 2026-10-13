# Roadmap

## Foundation — current alpha

- [x] Organized solution and project-local SDK/caches.
- [x] Standard text/image/file capture and encrypted bounded history.
- [x] Rope, rounded notes, pegs, show/retract animation.
- [x] Hotkey, tray, pin/delete/clear, pause, opt-in sign-in startup.
- [x] Restore and guarded paste request.
- [x] Storage checks, Windows CI, portable and installer packaging sources.
- [x] Contribution, conduct, privacy, and security documents.

## Before stable release

- [ ] Iterate rope geometry, peg detail, paper appearance, hover, and motion with user feedback.
- [x] Browse all saved notes with viewport rendering, all/starred filtering and resizable full-content views.
- [ ] Search by content and date.
- [x] Persistent themes, shortcut, limits, retention, capture types, pause, ordering, size and spacing.
- [ ] Per-application capture exclusions.
- [ ] HTML/RTF and broader image-format preservation.
- [x] Time-bounded clipboard reads and file-location validation, with at most one blocked OS worker per operation.
- [x] Native synthetic capture, hotkey, accepted text paste and restart persistence on an isolated Windows runner.
- [x] Isolated rapid-copy, image/file capture, storage bounds, five-minute idle and restart persistence.
- [ ] Destination app matrix: Notepad, Paint, Explorer, browser and restricted/elevated fallback.
- [x] Narrow-screen layout, system high-contrast palette and live reduced-motion changes covered by regressions.
- [ ] Mixed DPI, multiple monitors, accessibility, high contrast, reduced motion, and small screens.
- [x] Measure isolated idle CPU, listener memory, scrolling allocations and disk bounds.
- [ ] Cold-start distribution and broader physical-device workloads.
- [x] Isolated EXE installation, cross-version encrypted history/preferences preservation, same-version reinstall, native preview and uninstall in CI.
- [ ] Clean-machine interactive installation and Store-signed installation validation.
- [x] Assigned Store package identity, checksums, provenance metadata and alpha release notes.
- [x] 0.5.1 Store certification and public listing under ShardCoders; official acquisition controls verified.
- [x] Public GitHub 0.5.1 alpha with verified checksums, provenance, release notes and licences.
- [ ] Store-signed installed behavior and trusted signing for the separate EXE.

Later: validated ARM64 support, optional file snapshots, app exclusions/incognito capture, richer previews. No cloud/account/AI processing is planned for version one.
