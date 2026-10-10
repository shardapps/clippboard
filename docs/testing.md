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
