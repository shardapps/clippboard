# Windows validation pipeline

The repository uses C#/.NET 10, WPF, NuGet lockfiles, PowerShell, Inno Setup and the Windows SDK's MakeAppx. The CI/CD Pipeline Builder detector does not recognize `.csproj`/`.slnx`; its empty detection is not a reason to generate a different stack's template. Commands come from the project files and `scripts/dev.ps1`.

`Windows build` runs on isolated Windows 2025 runners:

1. **compile** restores the normal locked graph, audits it, builds with warnings as errors, runs regression/packaging guards, and verifies the bundled portable app and installer. It transfers exact binaries as an internal, three-day validation artifact.
2. **motion**, **native**, **installer** and **store** run independently. Motion runs the compiled assembly without rebuilding. Native validation includes real synthetic capture/paste and five minutes of idle. Installer validation separately downloads the published 0.5.1 installer and verifies its pinned SHA-256 and checks upgrade, reinstall, preview and uninstall. Store validation publishes the loose self-contained package and checks identity, versions, notices and hashes.
3. The stable required **build** check runs even when prerequisites fail or are skipped. Every prerequisite must report success before candidate artifacts are uploaded. The final job re-verifies the binaries/notices and creates a versioned portable ZIP, setup copy, checksum manifest and source/run provenance. ZIP entries are compared byte-for-byte by SHA-256 with the verified source files; intermediate PDB/build files are excluded. These are evaluation artifacts, not a public release or a Store submission.

Each job has a 5–10 minute deadline; expensive native and installer phases also have step deadlines. Installer process starts/completions and elapsed times are preserved with its native logs. Motion/native/installer reports are retained on failures when the runner remains responsive. A runner loss may prevent uploads; it is not a passing test. Use Actions job/step durations and report timestamps to distinguish slow builds, app failures and runner stalls. Retrying an infrastructure failure never substitutes for investigating reproducible failures.

Normal `packages.lock.json`, `packages.bundle.lock.json` and `packages.store.lock.json` are distinct: self-contained/single-file publishing introduces SDK packaging dependencies. Publish commands require their matching locked graph. When servicing the SDK/runtime, regenerate the two publish graphs explicitly, review their diffs and audit, then rerun CI:

```powershell
dotnet restore src/Clippboard.App -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:ClippboardPublishProfile=bundle
dotnet restore src/Clippboard.App -r win-x64 -p:SelfContained=true -p:PublishSingleFile=false -p:ClippboardPublishProfile=store
```

NuGet caches include all `packages*.lock.json` files. Actions are pinned to commits; artifact download uses the Node 24 action with digest mismatches failing by default; checkout does not persist credentials. CI requires no signing or publishing secrets. NuGet graph auditing does not cover the self-contained runtime or downloaded Windows SDK/Inno tools; their provenance and servicing remain separate checks. No runtime/hardware matrix is claimed: this pipeline covers Windows 11 x64 code on a hosted runner. Device/Store acceptance is tracked in the [beta checklist](beta-checklist.md).

The privacy-site workflow validates PRs separately and gates production deployment. App publication, signing and Store certification remain separate from this CI workflow. Do not weaken required checks to merge a failed run.

The compile/WPF job explicitly uses the Windows reduced-motion preference. Motion and native-session jobs explicitly enable client-area animations before starting test processes, verify the preference, and restore the original setting afterward. The helper refuses to modify a non-GitHub-Actions desktop. Native session reports include the animation preference and require that six outside-dismissal reopens reuse both the same HWND and process while animations are enabled. This keeps packaged foreground/animation coverage distinct from synthetic reduced-motion checks.
