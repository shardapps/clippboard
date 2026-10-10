# Validation record

The executable regression suite covers Unicode/binary persistence, encrypted-at-rest bytes with real Windows DPAPI, file references, dedup/pins, retention, count/disk caps, oversize/full-pin rejection, restart persistence, corruption recovery, clear including pins, and concurrent writes. Synthetic WPF data objects also check format precedence, text/file limits, PNG conversion, sensitive exclusion markers, and the native input ABI. These checks do not touch the system clipboard. Run `scripts/dev.ps1 test`. CI uses the same suite.

Release builds validate core and WPF compilation. The app-rendered sample preview exercises XAML loading without accessing the clipboard. Packaging generates the self-contained executable and checksums.

Actual capture/paste into Notepad, Paint, Explorer, browsers, and elevated apps still needs integration validation. Mixed DPI, remote sessions, slow clipboard owners, network shares, accessibility, startup, installer lifecycle, memory/CPU, and cold start are not certified by storage tests or previews.

## Initial local verification — October 10, 2026

- .NET SDK 10.0.401, Windows x64, project stored on Google Drive J:.
- Release core/app/test build passed with zero warnings/errors using `UseAppHost=false`.
- All 29 storage/format assertions passed; tests left the system clipboard untouched.
- Synthetic WPF preview rendered and was visually inspected; saved in `docs/assets/rope-preview.png`.
- Git whitespace, local documentation links, and five YAML files checked successfully.
- Native app-host resource updates fail on this virtual drive. Native executable/setup packaging is delegated to GitHub's Windows runner.

## Hosted build and native smoke check

[Windows build 38043157500](https://github.com/shardapps/clippboard/actions/runs/38043157500) passed restore, compilation, all 29 regression assertions, self-contained publishing, Inno Setup packaging, and artifact upload in 1m39s.

Both downloaded binaries matched the pipeline's SHA-256 checksums. The self-contained executable ran on this Windows machine in synthetic preview mode and exited with code 0; its output was visually inspected. Runtime extraction and temporary files were directed to project `.cache/`. This confirms native startup/rendering, not clipboard capture or destination-app integration. Setup compilation passed; installation/upgrade/uninstall have not yet been tested.

Initial file sizes: portable executable 173,061,775 bytes (165.0 MiB); compressed setup 51,698,881 bytes (49.3 MiB). These include the .NET runtime. Resident memory and CPU are still unmeasured; package size alone does not establish lightweight runtime behavior.

## 0.2.0 interface and memory iteration — October 10, 2026

- Release builds pass with warnings as errors. All 73 storage, format, cache and WPF assertions pass locally, using synthetic data and real DPAPI without touching the system clipboard.
- WPF checks exercise every settings page, retention bounds, behaviour edits, full text beyond the 320-character preview, enlargement within the window, all-history scrolling with 1,000 notes, exact byte-budget LRU eviction, and three fresh-overlay open/close cycles.
- The close-time queued-scroll regression is covered. Closing releases cards, expanded content, previews, pending loads and the native transparent window surface. The next opening constructs a fresh overlay.
- White, cream, dark and settings/detail previews were rendered and inspected. Windows reduced motion disables rope motion; both spring and critical-damping curves have bounded endpoint checks. Subjective smoothness, mixed-DPI and screen-reader use still need device testing.

Reproducible diagnostic: `./scripts/dev.ps1 preview -Measure`. This sample uses 1,000 synthetic metadata notes and one small synthetic image, no clipboard listener, a rendered PNG, forced collection after closing, a two-second settle and a five-second idle CPU sample. It is not representative of real capture or peak image workloads.

| Diagnostic | Local result |
| --- | --- |
| Rendered cards / saved metadata notes | 7 / 1,000 |
| Cards / cached image bytes after close | 0 / 0 |
| Managed heap after close | 2,352,416 bytes (2.24 MiB) |
| Process working set shown / closed | 228.5 / 238.3 MiB |
| Process private bytes shown / closed | 167.7 / 187.6 MiB |
| Idle CPU time during five-second sample | 0 seconds |

These numbers show bounded content retention but do **not** establish a low-RAM production app. WPF/CLR, native rendering allocations and screenshot diagnostics contribute to total process memory. Five-minute idle sampling, representative large-image captures, long-running open/close profiling and cold start remain required before a stable release. No working-set trimming tricks or forced GC run in normal application operation.

## Appearance refinement — October 10, 2026

96 local assertions pass. Added checks cover all six palettes, actual tooltip templates and foreground/background contrast (at least 4.5:1), date metadata contrast, live size previews without committing settings, cancellation, Save, reduced-motion final state, and hover settling. The visual harness loads the production control dictionary into a plain WPF Application, preventing queued production startup from running capture/tray integration. Test windows stay off-screen and do not activate.

These are synthetic regression checks, not a guarantee against every crash or a substitute for destination-app and installer lifecycle testing.

## Finding clips and feedback — October 10, 2026

105 local assertions pass after adding search/filter coverage: case-insensitive preview search, no-match state, each clipboard type, restored full history with bounded cards, all five settings pages, correct feedback form URLs, and absence of account/path details in support information. Feedback submissions, browser launch and copying diagnostics are user actions and are not invoked by the synthetic suite.

## 0.3.0 packaging validation

The local suite passes 105 assertions; five packaging guard checks additionally pass for out-of-project signing, incomplete signing configuration, rejecting unsigned artifacts when signing is required, and duplicate checksum metadata. New real-clipboard integration checks are gated to `GITHUB_ACTIONS=true` on isolated Windows runners. Local tests still leave the system clipboard untouched.

CI now exercises actual Windows notifications, Unicode text/image/file capture, pause, own-restore deduplication, exclusion markers, missing originals and clipboard restoration. Packaging checks actual SHA-256 hashes and Authenticode state. Installer checks use a fresh directory and refuse an existing installation: install, same-version reinstall, installed native synthetic preview, and uninstall. These steps do not establish cross-version migration, destination-app paste acceptance, interactive installer behavior, SmartScreen reputation, or signed-build correctness without a real certificate.

[Windows run 38051198647](https://github.com/shardapps/clippboard/actions/runs/38051198647) passed 114 synthetic/isolated-Windows assertions, five packaging guards, unsigned signature metadata, native packaging, installation, same-version reinstall, installed preview and uninstall in 2m8s. Publisher signing remains pending an owner-provided identity.

## Bounded file restoration

122 local assertions pass, including malformed null/blank clipboard paths and a controlled stalled file probe. A file-location check returns a recoverable error after three seconds, and subsequent attempts cannot start additional workers while the original Windows I/O remains outstanding. Windows file existence checks are not cancellable; at most one worker can remain blocked until the OS resolves the request. Text/image restore remains available. Tests verify existing/missing/empty references, timeout, ten rejected retries, and a maximum of one outstanding worker. Clipboard service disposal is idempotent and pending capture completion cannot restart its timer after disposal.

[Windows run 38051885965](https://github.com/shardapps/clippboard/actions/runs/38051885965) passed 133 assertions including real clipboard disposal/restore-after-disposal checks, the five packaging guards, native packaging and the installer lifecycle checks in 2m7s.

## Control feedback polish

129 local assertions pass. Added checks exercise actual button hover/leave feedback, non-sticky highlights with reduced motion, and initial/toggled switch positions after WPF Loaded events, bounded continuous-sway endpoints and an old dismiss animation that cannot remove reopened settings. Keyboard focus outlines render above the hover layer. Real users and device testing are still needed to evaluate subjective motion, touch and screen-reader accessibility.
