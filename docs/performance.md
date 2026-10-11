# Motion and process memory

Run `./scripts/dev.ps1 diagnostics` (or add `-Software`) for eight synthetic 1,000-note entrance, rapid scroll, settings and close cycles. Reports are local, under ignored `artifacts/`. This diagnostic does not access the clipboard, create screenshots, force GC or trim working sets. Frame intervals are WPF rendering-event timings, not proof of physical display presentation or a guarantee of refresh-rate performance.

## October 10, 2026 — local WPF rendering tier 2

| Eight-cycle diagnostic | Previous animation | Continuous spring / transform attachments |
| --- | --- | --- |
| Median frame gap, range across cycles | 20.3–38.3 ms | 13.3–16.1 ms |
| 95th-percentile gap, range | 59.6–110.0 ms | 21.1–30.4 ms |
| Gaps above 33.4 ms, per cycle | 36–55 | 0–6 |

Each timed spring uses an exact damped solution. Retargeting carries both current position and velocity into the next clock, including the delay before WPF starts that clock. Note attachments use render transforms instead of changing Canvas.Top during every rope frame. Ambient borders replace moving pixel-shader blur surfaces. Appearance edits coalesce viewport work at rendering priority. Closing cancels animation clocks and releases note content.

These changes substantially improve this measured workload, but they do not solve process RAM: final idle working set was approximately 257 MiB in both runs. WPF/native allocation lifetime must be addressed separately. Software rendering, long-running capture and other devices remain separate validation requirements. Raw local reports: `artifacts/motion-baseline.json` and `artifacts/motion-optimized.json`.

## Disposable overlay process

The tray/listener now owns history and settings without constructing a rope window. Each open starts a temporary overlay process. Closing exits that process and releases its native graphics allocations. A random, current-user-only named pipe carries bounded requests and lazy payloads; the listener remains the single storage writer. Clipboard data never crosses the network or enters plaintext temporary files. Requests are serialized, framed, size-checked and time-limited; malformed connections cannot terminate capture.

Eight local synthetic child-process cycles (1,000 notes, scroll, settings, close) returned the parent to **57.0 MiB working set / 17.2 MiB private bytes**, with zero sampled CPU over ten idle seconds. No forced collection or working-set trimming was used. Combined active peak remained **325–330 MiB**; this improves idle RAM, not peak RAM. The diagnostic excludes clipboard capture and must be repeated with a native installed build and real capture. Cold child launch adds startup work on each open; it must be measured separately on SSD installations. Run `scripts/dev.ps1 lifecycle`; add `-Software` for the software rendering path.

The corresponding software-rendered run completed all eight child lifetimes and returned to 56.1 MiB working set, with combined active peaks of 198–204 MiB. Hardware and software results describe this machine and synthetic workload; they are not universal RAM ceilings.

## Native CI session — October 10, 2026

[Run 38070084969](https://github.com/shardapps/clippboard/actions/runs/38070084969) exercised the self-contained executable with three real Unicode capture/hotkey/overlay/accepted-paste cycles into a controlled WPF destination. After terminating and restarting its listener, persisted history contained all three entries. A 30-second idle sample then reported **78.0 MiB working set / 21.5 MiB private bytes** and **0.42% of one CPU core**. No forced GC or working-set trimming was used.

This measures the restarted listener after loading three persisted text entries, not five minutes of steady-state capture, large images, the overlay's active peak or a Store-installed package. The earlier 57 MiB synthetic parent value excludes clipboard integration. Keep these workloads separate; neither number is a universal memory ceiling. The artifact contains `session-checks/report.json` and hardware/software overlay lifecycle reports.

## October 11, 2026 — input and allocation work

Horizontal gestures now use the native [WM_MOUSEHWHEEL contract](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mousehwheel): signed coordinates, fractional ticks, Windows character/page settings and no internal message forwarding. Ctrl gestures and settings/detail panels are excluded. Trackpad motion uses a shorter spring than navigation-button movement.

The note canvas moves through a render transform during scrolling; ScrollViewer's accessible offset is committed at rest. This follows Microsoft's guidance to [avoid repeated layout and update existing transforms](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-layout-and-design). On this local tier-2 device, eight identical 1,000-note cycles had p95 WPF callback gaps of **21.1–31.7 ms before / 19.3–24.2 ms after** the scroll change, and **0–10 / 0–3** gaps above 33.4 ms per cycle. Median ranges were **12.9–15.7 / 14.2–16.2 ms**; median improvement is not established. Reports: `artifacts/motion-baseline-0.4.0.json` and `artifacts/motion-trackpad.json`. These are callback intervals, not physical display FPS.

Further changes reuse unchanged note visuals, reorder with transforms, apply slider width/font edits once per input change, cancel removed-card animation clocks and cancel offscreen thumbnail work. Decoding remains bounded to two background workers; cancelled late payloads are wiped before decoding, and a cache generation prevents old loads repopulating a cleared cache. The overlay surface is capped at 720 device-independent pixels and reused for panels; repeatedly resizing native surfaces was rejected after it increased peak memory. The rope remains full width, and outside clicks dismiss via deactivation.

The native CI fixture now performs a 100-change Unicode burst, checks final-item persistence, captures two distinct 2048×2048 images and file references, verifies entry/disk bounds, samples warm idle memory/CPU every ten seconds for five minutes, then verifies restart persistence. Windows may coalesce rapid transient clipboard changes; the test requires the final value rather than promising that every transient is captured. It remains isolated to CI, never a developer's personal clipboard. Physical trackpads, other machines, mixed DPI and Store-installed packages remain separate checks.

The supported release target remains Windows 11 x64. [.NET 10's supported OS table](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) includes specific Windows editions and architectures, but runtime support alone does not validate this app on them. Windows 10 and ARM64 are not advertised as tested.

### Renderer tradeoff

The bounded-surface lifecycle run returned the synthetic listener to **56.8 MiB** idle without collection/working-set tricks. Active combined peaks were **312.1–335.9 MiB** in hardware mode versus **189.9–195.7 MiB** when software rendering was forced. This reduces active RAM in software mode, but the eight-cycle animation profile had p95 callback gaps of **47.4–59.3 ms**, so software is not the default. A per-note bitmap-cache experiment made long gaps worse and was rejected. Storage settings now offer an explicit “Use less RAM while browsing” preference, applied after Save and reopening; its CPU/smoothness tradeoff is explained there. These values describe synthetic workloads on one machine, not a universal ceiling or the captured-image soak.

### Continuous scrolling and adaptive retention (0.5.1)

Precision input now updates one rendering-driven controller. It integrates the exact spring using elapsed frame time, preserves velocity on retargeting, ignores duplicate render callbacks and commits accessible offsets at physical-pixel rest. Response follows observed input/render cadence; Windows supplies gesture momentum. The rendering subscription stops at rest or close. Existing entrance, hover and panel springs remain unchanged.

Visibility reconciliation runs only when the viewport range changes. Notes are rebound and reused, including a small reserve bounded by viewport geometry for staggered entering/leaving boundaries. Their image sources, canceled work, hover state and action bindings are reset before reuse. This follows Microsoft's [control recycling guidance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/how-to-improve-the-scrolling-performance-of-a-listbox).

Decoded cache retention is the smallest of the user ceiling, two viewports of previews, and one percent of currently available physical memory. [GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex) supplies current availability. Shrinking the budget evicts immediately; a background-priority check handles pressure while the overlay is stationary. Preview decoding uses one or two workers according to memory headroom and processor availability. Visible images and framework/native allocations are additional to the cache budget; this is not a total-process RAM guarantee.

The listener uses a [message-only native window](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#message-only-windows), avoiding a WPF listener surface. The overlay measures content before native creation and reuses that surface for settings/details. No forced GC, working-set trimming or fixed per-machine memory target is used.

Run `./scripts/dev.ps1 diagnostics -ScrollOnly` for four sustained 360-update precision gestures and reversals over 1,000 synthetic notes. The report includes control allocations, process-wide allocated bytes, rendering callback gaps and process memory. Compare on the same machine; callback intervals are not display FPS and do not establish universal smoothness. Native CI additionally exercises real capture/hotkey/paste, encrypted storage, restart, five-minute idle and installer validation.
