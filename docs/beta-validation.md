# Beta candidate evidence

## 0.6.0-beta.5 retained-note renderer

Balanced rendering is the new default for this evaluation candidate. Existing WPF controls retain hit testing, keyboard and automation behavior; cached native note surfaces avoid the hardware-renderer's larger memory baseline on the development host. Settings/detail panels restore the live WPF note view beneath them. Hardware and software compatibility modes apply after saving and reopening; the old explicit software preference is preserved.

The local comparison and limitations are recorded in [performance](performance.md). Regression coverage includes actual opaque cached pixels, accessible invoke and bounds, transparent-control hit testing, scroll without per-frame text rasterization, theme/selection changes, resizing, hover, reversible entrance/retraction, frame unsubscription and destruction of owned native windows. Synthetic zero-headroom tests exercise initial and active-session fallback. Isolated installed CI also clicks a note through the native surfaces and records combined listener/overlay active samples. Fresh complete CI and artifact verification are required before delivery; this paragraph does not claim they have passed. Public release and the remaining physical-device/Store checks stay held.

The initial [run 39134644215](https://github.com/shardapps/clippboard/actions/runs/39134644215) passed compile/regressions, motion/lifecycles, upgrade/uninstall and Store-package checks, but failed installed input because the rope retired before the second destination check. That run produced no release candidate. The follow-up shows native surfaces explicitly without activation, distinguishes internal activation from an external app switch before retracting, and preserves controlled failure-stage/focus diagnostics. Fresh installed validation is required; the failed setup is held rather than delivered.

## 0.6.0-beta.4 active-use follow-up

This candidate moves pipe/JSON exchange off the UI dispatcher, sends revision-checked incremental metadata after the initial catalog, and retains unchanged labelled note controls across refreshes. Unit checks cover concurrent real-pipe catalog callers, lost-reply resynchronization, exact order, removals and malformed-update atomicity. No persistent storage migration is introduced.

The local synthetic fifty-update allocation/wire comparison and fifty-refresh WPF check are documented in [performance](performance.md). Their results concern metadata allocation/control reuse; active WPF/native graphics RAM remained an acceptance item. The software bitmap-cache trial was rejected after poorer scrolling results. This historical candidate preserved the default renderer. All six stages passed in [run 39112363097](https://github.com/shardapps/clippboard/actions/runs/39112363097), attempt 2; the unchanged installer rerun passed on a fresh runner after its first runner timed out. PR #52 head `60915c05b6fda1e7a0747d7c61316d7f3c432e05` and merged main share tree `4dbad5883295fdffb53c74a92bc4b7f2e1557b48`. The verified beta.4 setup is an unsigned evaluation asset; public beta release remains held.

## October 18 owner acceptance and next memory follow-up

The owner reports the installed beta.2 is working after the shortcut, rail and outside-click fixes. A read-only process-version check confirmed `0.6.0-beta.2+d1fa598ac010ab577c30cdc5a6a9c9e5c84f8f4b`; one idle listener/prepared-child sample totaled 43.8 MiB private memory. This is a single snapshot, not an active renderer, leak, startup distribution or all-device result. No clipboard history or content was read.

All six stages passed in [run 39024213813](https://github.com/shardapps/clippboard/actions/runs/39024213813), attempt 2. The first installer job exceeded its timeout without retained logs; an unchanged rerun passed on a fresh runner. Three real installed prepared shortcut activations took 101–205 ms to a visible foreground window, with six gap/background dismissal reopens retaining their HWND/process. Five-minute combined idle private samples were approximately 57–60 MiB. Installed synthetic first-content events took 133–148 ms prepared and 455–536 ms cold; these are not physical presentation latency. The verified candidate and merged source tree are `22687311d57ddd5eea856826c92af21eb6b6401a` (PR #48).

Beta.3 puts expanded-image reads/decodes behind the same adaptive admission as thumbnails, before requesting the original, and sizes the output for the overlay's physical bounds. WIC cannot interrupt an in-flight decode; cancellation retains its slot until completion and suppresses the result. The original encoded image is wiped on success, failure and cancellation. This bounds concurrent image work; it does not eliminate WPF's native rendering baseline. Further device and Store acceptance gates remain open.

## 0.6.0-beta.2 follow-up

The next evaluation build fixes the incomplete history rail, transparent-gap dismissal, renewed foreground activation and explicit shortcut reversal during outside dismissal. WPF regressions cover layout, input routing and stale/fresh opens; isolated native CI additionally exercises six alternating gap/background dismissals and shortcut reopens before the existing capture/paste and idle soak. Publication remains held for the owner’s next installed-build review.

The native surface no longer reserves closed sidebar margins across the monitor width. Panels keep the same bounded surface while open, avoiding repeated native resizes. This reduces the reserved pixel area; it is not a fixed total-process RAM reduction or an all-device memory guarantee.

The opening investigation now includes the owner's exact installed beta.1 EXE, using only synthetic notes and its existing self-contained runtime cache. Four cold first-content events took **856–937 ms**, while four prepared events took **189–311 ms**. These are neither real clipboard-history timings nor physical frame presentation. The follow-up learns runtime/control preparation costs separately and can retain a cheaper runtime tier when controls exceed the headroom budget. The native CI job installs the setup at its default per-user location, verifies its executable against the portable input, waits for background preparation, and tests actual shortcuts/animated outside-dismissal reversals and graceful listener close. Cold/prepared lifecycle reporting also runs from that installed self-contained EXE. The CI-only animation helper explicitly separates reduced-motion regressions from the animated path.

## 0.6.0-beta.1 historical baseline

The public 0.5.1 alpha is unchanged. This is a validation candidate for Windows 11 x64, not a stable release or an all-hardware guarantee.

## Tested source and automation

[Windows candidate run 38922906824](https://github.com/shardapps/clippboard/actions/runs/38922906824) passed all six required jobs on October 16, 2026. PR head: `d3c684c16d707d6a75c561aec0e21574e39dd85d`. Actions tested merge commit `b06a2d373a2d3f2683b7ec3e1758a0870e80752f`; its tree and the PR head's tree both equal `5df282ba199ab18504136d3fe8afec67dab8035a`. Candidate merge `12edda26104aa74ec4fe5c1dd89387f2de021c1a` preserved that tree.

| Check | Evidence |
| --- | --- |
| Compile, locked restore, NuGet graph audit | Passed; warnings are errors. No reported vulnerable graph entries; framework/download servicing is separate. |
| Regressions | 499 storage/format/cache/WPF/DPAPI assertions on CI, 478 locally; CI includes isolated native image/clipboard checks. |
| Packaging guards | Signing rejection, altered/missing notices and prerelease/numeric version checks passed. |
| Installer | Published 0.5.1 installer hash pinned to `a1cc196d5eac7443f47f5b4ff98ab83f703aad1d187cede930be9521546960aa`. Upgrade preserves encrypted fixture bytes, preferences, stars and file references; reinstall, installed preview and uninstall passed. Job: 1m40s. |
| Store ingestion | Numeric identity/file version 0.6.0.0, expected publisher, opt-in startup, required contents/notices and SHA-256 passed. This unsigned ingestion package has not passed beta Store certification. |
| Native runtime | Three Unicode/restore/paste cycles, rapid shortcut reversals, formatted text, 100-copy burst, images, file reference, 30 idle samples over five minutes and restart passed. |
| Motion/resource lifecycle | Eight cycles per configured renderer, disposal/standby-release assertions, sustained precision-scroll/reversal and metadata reports passed. Hosted default rendering does not establish physical GPU behavior. |

The pipeline now assembles `clippboard-beta-release-candidate` only after all prerequisites pass. It contains the portable ZIP with notices, installer, SHA-256 manifest, build metadata and source/run provenance. The assembler re-verifies input packages and ZIP contents. These evaluation artifacts expire after 14 days; retain the chosen verified files before that deadline. They are not a public release, a signed attestation or Store-signed downloads. GitHub EXEs remain unsigned.

## Performance limits that remain open

The hosted native test measured shortcut-to-visible-window samples of **713, 705 and 815 ms**. This is not physical frame presentation and does **not** establish instant opening. Prepared 1,000-note diagnostic processes reached first content in roughly 411–449 ms with default rendering, versus 744–840 ms without preparation. Further installed-device latency work remains an acceptance gate; do not mark it solved from passing functional tests.

On this runner, the listener's private bytes were about **39.5 MiB** after the capture workload and **19.9 MiB** after restart; sampled one-core idle CPU was **0.068%**. These are listener counters, not total app/GPU RAM. The lifecycle diagnostic parent returned to roughly 16–17 MiB private after child disposal. Hosted results differ from the development machine; see [the performance report](performance.md) for renderer tradeoffs and measurement definitions.

The separate local .NET 10.0.13 synthetic decode experiment allocated **126,848 managed bytes** over eight 2048×2048 decodes, without forced GC or working-set trimming. The earlier managed-copy implementation allocated about 128 MiB cumulatively in the same workload. This is allocation reduction, not a 128 MiB reduction in total retained app RAM.

The [beta checklist](beta-checklist.md) retains the physical trackpad/refresh-rate, mixed-DPI, accessibility, destination-app, low-memory, longer-session and Store-installed acceptance gates. Use synthetic data and record the tested build hash. Publication remains held until the required results are reviewed.
