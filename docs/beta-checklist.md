# Next beta checklist

The owner authorized implementation on October 13, 2026. Work proceeds in focused, tested PRs. This is a delivery checklist, not a claim that beta is ready. The published 0.5.1 alpha remains unchanged until a reviewed replacement release is prepared.

Implementation progress: PRs #24–26 add runtime preparation for faster opening, smoother retraction, binary image replies and bounded PNG encoding scratch. PR #27 adds encrypted derived previews; PR #28 adds full-text/file-path retrieval, date filters, canceled stale results and keyboard note selection/paste. PR #29 adds app exclusions, timed pauses and recoverable clear-on-lock. The next change adds previewed text actions and editable snippets/labels. Rich formats, file/image actions, multi-select, OCR, portable backups and the remaining profiling work are still in development. The unchecked acceptance gates below include integration and device evidence, not just the presence of code.

## Responsiveness and resource use

- [ ] Validate faster shortcut response, smooth retraction, rapid toggles, listener/overlay failures and cold-start fallback.
- [ ] Measure installed-build latency and combined listener/visual-process memory on representative devices.
- [ ] Reduce image IPC copies and bound capture/encoding scratch allocations.
- [ ] Add encrypted derived thumbnails so small cards do not fetch originals.
- [ ] Benchmark incremental/paged metadata and eliminate unnecessary refresh allocations.
- [ ] Audit graphics surfaces, static resources, events and retained objects.
- [ ] Verify pressure adaptation, cancellation, smooth scrolling and bounded background work together.
- [ ] Run longer capture and repeated open/close sessions with stable retained-memory trends.

## Retrieval, privacy and reuse

- [ ] Search full text and file names beyond short previews, with date/type/star filters and canceled stale searches.
- [ ] Keyboard selection/paste, visible focus, shortcut conflicts and screen-reader announcements.
- [ ] App exclusions, timed pause and optional clear-on-lock; preserve existing opt-out clipboard formats.
- [ ] Previewed text transformations that preserve originals.
- [ ] File-location/path and explicit image-save actions, with unavailable-original handling.
- [ ] Editable reusable snippets and labels within existing storage/30-day retention rules.
- [ ] Bounded HTML/RTF preservation and plain-text fallback with inert previews.
- [ ] Multi-select text combination with chosen order/separator and preview.
- [ ] On-demand local OCR with language/packaging checks, cancellation and editable results.
- [ ] Versioned, password-encrypted export/import with recoverable restore and clear file-reference semantics.
- [ ] User-previewed diagnostics and useful help/feedback without automatic clipboard-content uploads.

## Beta release gates

- [ ] Migration/upgrade preserves encrypted history, stars, settings and file references; interrupted work is recoverable.
- [ ] Store-installed first launch, startup on/off, update, restart and uninstall.
- [ ] Notepad, Paint, Explorer and browser destination tests plus restricted/elevated manual-paste fallback.
- [ ] Physical trackpad/wheel/scrollbar, rapid reversals, incoming clips and 60/120+ Hz presentation tests.
- [ ] Mixed DPI, multiple monitors, narrow screens, screen reader, high contrast, reduced motion and remote sessions.
- [ ] Low-memory/slow-device tests; disk-full, corruption, oversize input, unavailable shares and sleep/resume.
- [ ] Dependency/security/license review and verified release assets; standalone signing remains separate from Store trust.
- [ ] Reviewed green PRs, updated screenshots/help/privacy where behavior changes, honest compatibility/performance claims and fresh Store review.

Windows 11 x64 remains the advertised target. Windows 10/ARM64 require separate validation. Cloud sync, resident AI models, arbitrary macro execution and a screenshot/video suite are outside the agreed next-beta checklist. Physical-device results and human verification are required where automation cannot establish behavior; no zero-bug or all-hardware claim substitutes for those checks.
