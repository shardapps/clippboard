# Beta delivery and validation checklist

The owner authorized this work on October 13, 2026. The source is being prepared as **0.6.0-beta.4**, Windows package **0.6.0.0**. The public **0.5.1 alpha** downloads and Store release remain unchanged. Code delivery and release acceptance are separate below; no zero-bug or all-hardware claim is made.

## Implemented with automated checks

- [x] Adaptive empty-control preparation without a native surface or history reads, matching retraction timing, direct native shortcut signals and reversible rapid toggles (#24–26, #39).
- [x] Binary image IPC, bounded encoding scratch and binary original-image storage (#25–26, #32).
- [x] Encrypted derived thumbnails, detached native image copies, adaptive cache/worker/image admission using physical and commit headroom (#27, #40, #43).
- [x] Cached read-only metadata, unchanged-visual/search-result reuse and a bounded 1,000-summary allocation comparison (#32; performance report).
- [x] Incremental, revision-checked catalog updates; background pipe/JSON processing; unchanged labelled-note reuse and malformed/lost-update recovery (beta.4 follow-up).
- [x] Resource ownership audit, session-bound restores, late-result rejection and drained single-worker pipe shutdown (#42).
- [x] Full-text/file-path search, date/type/star filters, keyboard selection and canceled stale queries (#28).
- [x] Executable exclusions, timed pause, optional recoverable clear-on-lock and existing private-clipboard formats (#29).
- [x] Previewed text transformations, editable snippets and searchable labels within existing retention/storage limits (#30, #33).
- [x] File-location/path and explicit PNG-save actions with bounded original/destination checks (#31, #37).
- [x] Bounded HTML/RTF preservation, inert previews and plain-text fallback (#34).
- [x] Multi-select text combination with order/separator preview (#33).
- [x] On-demand Windows OCR, package/language checks, cancellation and editable results (#35).
- [x] Versioned password-encrypted backup, verified export and reviewed/recoverable merge restore (#36–37).
- [x] Explicit diagnostics preview/copy and user-submitted help/feedback (#38).
- [x] Unicode grapheme boundaries for newly generated text/file previews (#44).
- [x] Current servicing floor, NuGet graph audit and verified embedded/distributed third-party notices (#41).

Local follow-up checks cover 523 assertions, including UI-context-free pipe replies, queued cancellation, labelled-note reuse, shared thumbnail/detail admission and corrupt-image recovery. The historical beta.1 candidate passed 499 assertions on isolated Windows CI; the follow-up adds rail layout, transparent input routing, explicit reopen and surface-bound checks, plus six native gap/background dismissal reopens. See the [candidate evidence](beta-validation.md) for the exact tested commits and limits. The [performance report](performance.md), [resource audit](resource-ownership.md), [dependency review](dependency-audit.md) and feature guides explain what was measured and what was not. The metadata comparison is an in-memory allocation experiment, not an installed search/IPC benchmark or a paging implementation.

## Candidate automation gates

- [x] Final candidate CI: warnings-as-errors, locked restore/audit, full storage/format/WPF checks and corrupt/oversize/cancellation regressions.
- [x] Repeated hardware/default and software lifecycle plus precision scrolling, with private memory, CPU and callback-gap reports.
- [x] Real isolated capture/hotkey/reversal/restore/paste, image/HTML/file formats, burst, restart and sustained idle.
- [x] Published 0.5.1 installer baseline → candidate upgrade, encrypted bytes/preferences/stars/file-reference preservation, reinstall, preview and uninstall.
- [x] Candidate portable/setup/MSIX version/resource/identity checks, notices and SHA-256 manifests; tested binaries match the recorded source tree. Versioned ZIP/setup/checksum/provenance assembly is a required final CI step; no public beta release is made.

## Release acceptance still requiring device/Store evidence

- [x] Owner installed beta.2 and confirmed the shortcut/slider/background-dismissal fixes work on October 18. This closes that reported interaction regression, not the broader device matrix below.
- [ ] Store-signed beta acquisition, first launch, OCR/languages, backup transfer, startup opt-in/disable, update/restart and uninstall.
- [ ] Representative installed-build cold/prepared shortcut latency and combined private-memory trends. Fresh WPF surfaces still have startup cost; direct signaling removes polling delay from an existing surface, not that initialization cost.
- [ ] Real Notepad, Paint, Explorer and browser destinations; restricted/elevated manual-paste fallback; moved/deleted/network originals.
- [ ] Physical trackpad, mouse wheel, scrollbar, rapid reversals and incoming clips on 60/120+ Hz displays.
- [ ] Mixed DPI/multiple monitors, narrow screens, screen reader, high contrast, reduced motion and remote sessions.
- [ ] Real low-memory/slow-device, disk-full/unavailable-folder, sleep/resume and longer capture/open-close sessions. Synthetic policy tests do not replace these.
- [ ] Review all findings, update release screenshots/help/privacy and obtain fresh Store certification. Keep publication held while required validation is unresolved.

For each manual result record build SHA, Windows version, display scale/refresh, renderer, workload, observed behavior and issue link. Use synthetic data; do not send personal clipboard contents. Test unsigned EXE trust separately from Store trust and never sideload the unsigned ingestion MSIX or disable protections.

Windows 11 x64 remains the target; Windows 10 and ARM64 require separate validation. Cloud sync, resident AI models, arbitrary macro execution and a screenshot/video suite remain outside the agreed next-beta scope. The feature work is implemented; the unchecked acceptance gates remain open until evidence closes them.
