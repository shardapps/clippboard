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
