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
