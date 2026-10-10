# Development and packaging

Use Windows 11, PowerShell, Git, and .NET 10 SDK. `scripts/bootstrap.ps1` installs SDK under `.tools/dotnet` without changing PATH. `scripts/dev.ps1` uses that SDK when present, with CLI home, NuGet packages, and temporary files under `.cache/`.

Run `scripts/dev.ps1` with `build`, `test`, `run`, `preview`, or `publish`. Local build/run/test/preview disable the native app host and run through .NET; this avoids executable resource-update failures on virtual/cloud drives. `publish` and CI still generate the native executable and require a filesystem that supports Windows resource updates. On this Google Drive workspace, use the CI-produced portable/setup artifacts for native executable testing.

`run` uses `--dev` and repository `.data/`, with startup controls disabled. Regular builds use LocalAppData. `--background` starts tray-only. Quit via the tray. `preview` renders synthetic notes to `artifacts/preview.png` without clipboard access.

## Packaging

Publish creates a self-contained x64 single-file executable in `artifacts/portable/win-x64`. Native libraries may be extracted by .NET at runtime. WPF trimming/NativeAOT are not enabled.

Install [Inno Setup 6](https://jrsoftware.org/isinfo.php), then run `scripts/package.ps1`. Use `-CompilerPath` for a custom compiler location. Output is `artifacts/installer/Clippboard-Setup-0.2.0.exe`. Setup is per-user under LocalAppData Programs and does not require elevation. Startup stays opt-in. Uninstall removes the startup registry entry but preserves history; clear history first if desired.

CI runs Release build and executable storage checks, then creates portable/setup artifacts with SHA-256 checksums. It does not publish a GitHub Release. Stable release is separate after compatibility, packaging, and signing checks. Unsigned builds may show SmartScreen prompts.

## CI skill adaptation

The invoked CI/CD skill detects Node/Python/Go but not `.csproj`/`.slnx`. Its detector was checked; .NET manifests supplemented detection. The custom Windows workflow applies actual restore/build/check/publish commands, NuGet caching, read-only permissions, and no automatic deployment.

## Manual acceptance

Use synthetic data. Check Notepad text paste, screenshot-to-Paint paste, and file copy to a temporary Explorer folder. Check restore-only, missing originals, pin/dedup/clear, pause, hotkey conflict, restart, and startup. Then elevated targets, two monitors/mixed DPI, Escape/outside retraction, reduced motion, Tab navigation, and repeated open/close.

Measure idle CPU for five minutes, working set after repeated image captures/reopen, and disk use at quota. Record evidence in `testing.md`; provisional targets are not measured claims.

Preview options: `./scripts/dev.ps1 preview -Theme Midnight`, `-Settings`, `-Detail`, and `-PreviewPath docs/assets/rope-preview.png`. `-Measure` uses 1,000 synthetic notes and writes `artifacts/performance.json`. This excludes clipboard capture, real large-image loads, and installation; it is a reproducible diagnostic, not a production benchmark. Force-collection is used only by this diagnostic mode.
