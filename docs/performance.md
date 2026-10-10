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
