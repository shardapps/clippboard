# Motion and process memory

## Next iteration — bounded image-encoding scratch

PNG capture now writes to a bounded owned memory stream. Logical writes, seek/length changes and reserve capacity cannot exceed the existing 16 MiB encoded-image ceiling. Valid header rewrites remain supported; a translated WIC limit error discards the item rather than retrying a known oversized encoding. Replaced, truncated and disposed scratch buffers are wiped. Span writes use the owned buffer directly, avoiding MemoryStream's subclass temporary-array fallback. The copied final PNG remains intact for encryption/storage.

This bounds encoding scratch growth, not memory already allocated by a clipboard owner/WIC source, decoded bitmap surfaces or the complete capture/encryption pipeline. Existing dimension checks, one bounded STA reader and timeout remain. Native CI captures actual images and verifies persistence; regressions cover exact limits, rewrites, resize/truncate wiping, overflow, cancellation and copied-payload ownership. No new dependency or plaintext temporary file.

## Next iteration — binary image transfer

Listener-to-overlay replies now keep bounded JSON metadata and send encoded PNG bytes as a separate binary attachment. This avoids Base64 expansion and a whole-image sender serialization array. Header version and combined lengths are checked before receiving buffers are allocated; inline Base64 is rejected before decoded-image allocation. The server wipes its source image after sending; failed/canceled receive buffers are wiped, and successful buffers transfer to the existing consumer cleanup. Requests retain their current framing, current-user pipe permissions and deadlines. History encryption/format is unchanged.

An eight-send local synthetic 2 MiB attachment test, after serializer warmup and using a counting sink, allocated **22,383,248 bytes with the former JSON sender / 7,232 bytes with the binary sender**, and wrote **22,372,208 / 16,780,120 bytes**. This isolates sender allocations and wire size. It excludes receiver, encryption, rendering, disk and total-process peaks; do not market it as a corresponding percentage reduction in app RAM. The regression checks exact byte preservation, error replies, cancellation, truncated/forged/version-mismatched frames, and current-user pipe operation. Local raw report: `artifacts/binary-transfer-profile.json`.

## Next iteration — shortcut latency and retraction

The visual process previously started on every invocation and waited for history before starting its entrance. The next development build can prepare only its runtime/resources while idle, without a Window, graphics surface, history payload or preview. One standby is allowed; measured private-byte cost and current available RAM govern whether it is kept. Separate start/stop thresholds prevent pressure-boundary churn. The storage settings' “Prepare faster opening” preference disables this extra process. Closing still exits the visual process; its replacement has no renderer pools. The listener starts the entrance before awaiting history, and retraction retargets its current position/velocity with the same response as entrance.

Local alternating synthetic 1,000-note runs measured **0.77–1.12 s cold / 0.19–0.26 s prepared** with a preconstructed control tree, but that experiment cost **41.5–41.6 MiB private / 79.2–79.4 MiB working set** while idle and was rejected. The lean runtime-only variant measured **0.81–1.02 s cold / 0.40–0.54 s prepared**, with **12.9–13.1 MiB private / 49.1–50.2 MiB working set** for standby. Working sets include shared mapped pages; do not describe private bytes as total RAM or omit the standby from combined memory accounting. The earlier eight-cold baseline ranged 0.81–2.15 s and had different warmup, so the alternating same-run comparison is preferable. First-content events include synthetic card work and are not physical presentation/hotkey latency or a universal speed guarantee.

`scripts/dev.ps1 lifecycle` now alternates cold/prepared starts, verifies preparation reports no window, and checks canceling an idle standby terminates its pipe worker. Native CI still validates actual capture/hotkey/paste/restart. Installed SSD/Store launch distributions, rapid physical hotkeys and pressure profiles remain required before beta. Raw local reports are in ignored `artifacts/overlay-launch-*.json`.

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

Decoded cache retention is the smallest of the user ceiling, two viewports of previews, and one percent of current headroom (the lesser of available physical memory and commit capacity). [GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex) supplies current physical and commit availability. Shrinking the budget evicts immediately; a background-priority check handles pressure while the overlay is stationary. Preview decoding uses one or two workers according to memory headroom and processor availability. Visible images and framework/native allocations are additional to the cache budget; this is not a total-process RAM guarantee.

The listener uses a [message-only native window](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#message-only-windows), avoiding a WPF listener surface. The overlay measures content before native creation and reuses that surface for settings/details. No forced GC, working-set trimming or fixed per-machine memory target is used.

Run `./scripts/dev.ps1 diagnostics -ScrollOnly` for four sustained 360-update precision gestures and reversals over 1,000 synthetic notes. The report includes control allocations, process-wide allocated bytes, rendering callback gaps and process memory. Compare on the same machine; callback intervals are not display FPS and do not establish universal smoothness. Native CI additionally exercises real capture/hotkey/paste, encrypted storage, restart, five-minute idle and installer validation.

### Derived image previews — next beta

Image cards request encrypted, versioned thumbnail files instead of transferring original PNGs on each open. The first view generates a thumbnail outside the history lock; later views read the bounded derived file. Detail and restore continue to request the original bytes. Cache retention fits both one sixteenth of the configured history budget and unused payload space. Optional files are removed with their original entry and rebuilt on corruption. No plaintext images are written to temporary storage.

A deterministic 1024×1024 high-entropy fixture encoded to **3,660,603 bytes** as an original PNG and **147,228 bytes** as its card preview, a **96.0% reduction in that image attachment**. This is a transfer-size comparison, not a total-process RAM or display-latency measurement. First generation still requires the original, and WPF/native allocations remain additional. Tests exercise DPAPI persistence, restart cache hits, corruption recovery, deletion during generation, storage headroom, output bounds and original-byte preservation.

## Development: reversible shortcut motion

The next beta sends a small native visibility signal to the verified child-owned window rather than waiting for the 350 ms metadata polling timer. Revision numbers prevent an older state reply from undoing a newer shortcut request; regular polling remains a fallback. No clipboard payload travels in window messages.

A second shortcut can retarget an in-progress retraction from its current position and velocity. Its canceled delay cannot later destroy the reopened surface. Ordinary outside/Escape/selection dismissal stays terminal, acknowledges the listener, and a later open request can create a new child if retirement already finished. Lock also advances the visibility revision. Reduced motion still applies without a spring.

Local WPF checks cover repeated reversals, position/velocity continuity, stale close completion, actual native-message delivery and terminal dismissal. The isolated native session adds three real hotkey reversal pairs before restoring/pasting text. These checks do not prove physical frame latency, device smoothness or Store-installed behavior.

## Development: full-size image copy allocation

The decoder copies WIC pixels directly into a locked native bitmap and freezes the independent result. It no longer creates a full-size managed pixel array between two native buffers. PNG stream detachment, original dimensions, pixel bounds and BGRA data are preserved. Clipboard image decoding runs off the listener dispatcher; a changed clipboard or Windows lock during preparation rejects the stale restore.

On one Windows 11 x64 machine with .NET 10.0.12, `./scripts/dev.ps1 decode` decoded a synthetic 2048×2048, 14,345,210-byte PNG eight times after one warmup, without a window, forced GC or working-set trim:

| Measurement | Before | Native copy |
| --- | ---: | ---: |
| Process-wide managed allocations during eight decodes | 134,337,248 bytes | 128,184 bytes |
| Highest sampled private bytes after a decode | 147,783,680 | 139,583,488 |
| Mean decode time | 82.4 ms | 85.3 ms |

This removes roughly 128 MiB of cumulative managed allocation in that workload. The private-byte readings are post-decode samples, not native peak memory or total-app idle RAM. The small timing difference is a tradeoff, not a speedup claim. Native image buffers and rendering remain additional costs.

A separate preparation experiment eagerly resolved/sealed deferred UI styles. Four prepared-launch samples shifted from 623–692 ms to 610–689 ms while prepared private memory grew from roughly 13.3 to 14.8–14.9 MiB. The overlap and increased idle cost did not justify keeping it, so that experiment was removed. Runtime-only preparation remains adaptive and creates no hidden control tree.

## Development: memory-pressure admission

Current headroom uses the smaller available physical-memory and commit-capacity values from Windows. The same signal governs standby retention, preview cache retention and decode concurrency. A new image admission check estimates a source raster, two output buffers and encoded input, and allows that estimate to use at most one quarter of reported headroom. This is a conservative preflight estimate, not a reservation or a total-process RAM cap; concurrent apps and native allocations can still change conditions.

Capture can skip large images before encoding when headroom is low. Full preview/restore and on-demand OCR can decline an image with a retry message, without changing the stored original. The estimate runs before full raster/model work where those APIs permit it; obtaining a source clipboard bitmap and reading/decrypting encoded bytes have their own bounded costs. An unavailable memory query keeps existing safety bounds rather than inventing a per-machine capacity. Existing 16-million-pixel and 16-MiB encoded format limits remain unchanged and are now shared constants.

Deterministic checks simulate exhausted headroom, low commit despite free physical memory, changing image budgets, invalid/overflow-sized inputs and unavailable queries. These do not substitute for real low-memory/slow-device testing.

The Windows signal is documented in [MEMORYSTATUSEX](https://learn.microsoft.com/windows/win32/api/sysinfoapi/ns-sysinfoapi-memorystatusex); available commit can be smaller than free physical memory.

### Default versus software rendering on one development machine

After the .NET 10.0.13 update, eight disposable-process cycles per mode used 1,000 synthetic notes, scroll and settings. The default-renderer child's sampled peak private bytes ranged **192–210 MiB**, versus **72–87 MiB** with software rendering. The diagnostic parent returned to about **18 MiB private** and 0% one-core CPU over its ten-second idle sample after child disposal. These are development-process measurements, not installed-build or all-device results; initial cold timing outliers remain.

Four subsequent sustained precision-scroll/reversal cycles recorded default-mode P95 rendering-callback gaps of **18–24 ms** and **1.5–3.3 CPU seconds** per workload. Software mode used **24–42 ms** and **3.6–5.1 CPU seconds**, with more long gaps in one cycle. Callback intervals are not physical presentation FPS. The existing software-rendering preference remains available to trade memory against CPU/motion; these results do not justify switching every device to software by default.

`./scripts/dev.ps1 lifecycle` now records child/combined private peaks separately from working sets. `./scripts/dev.ps1 diagnostics -ScrollOnly -Software` now correctly applies and reports the requested renderer; the previous scroll-only diagnostic had ignored that flag. Ordinary software-mode app launches were unaffected by that diagnostic bug.