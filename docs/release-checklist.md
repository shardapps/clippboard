# Publication checklist — 0.4.0 Store submission / 0.5.0 development

The current target is Windows 11 x64. A green build is necessary, but does not replace Microsoft certification or testing the installed Store package. Keep public publication on manual hold until the unresolved gates below pass.

| Gate | Evidence / state |
| --- | --- |
| Compile, storage, UI, clipboard formats | 166 assertions passed in [run 38070084969](https://github.com/shardapps/clippboard/actions/runs/38070084969). |
| Native text capture, hotkey, accepted paste, restart | Three controlled WPF destination cycles passed on the isolated runner; not a third-party application matrix. |
| Overlay lifetime | Eight hardware and eight software process cycles passed. Local idle/active measurements and their limits are in [performance](performance.md). |
| EXE installer lifecycle | Fresh CI directory: install, same-version reinstall, installed preview, uninstall passed. Interactive clean-device and cross-version upgrade checks remain. |
| Store package | Assigned ShardCoders identity, x64, disabled startup, contents and SHA-256 verified. Unsigned MSIX is for Store submission only. |
| Store listing and review | Initial 0.4.0.0 review rejected the “Windows utility” keyword and an inaccessible privacy URL. The keyword was removed and the dedicated [policy page](https://shardapps.github.io/clippboard/privacy/) verified without authentication. The corrected 0.4.0.0 submission is under certification with publication held manually; development builds do not replace it. Approval and signed-package review remain external gates. |
| Installed Store behavior | Pending signed package: first install, launch, hotkey/tray, history restart, startup opt-in/disable, update and uninstall. |
| Destination application matrix | Pending Notepad Unicode text, Paint bitmap, Explorer files, browser text and restricted/elevated manual-paste fallback. Include moved/deleted/network originals. |
| Device/accessibility matrix | Pending mixed DPI, multiple monitors, small screens, screen reader, remote session and slower hardware. Synthetic high-contrast/reduced-motion checks passed. |
| Sustained performance | 0.5.0 adds an isolated native fixture covering 100 rapid clipboard changes, final-item persistence, two 2048×2048 images, file references, encrypted disk bounds, five-minute warm idle and restart. Record its CI results before claiming it passes. Physical-device capture/overlay profiles, cold-start distributions and longer real workloads remain pending. |
| Trust and public release | Microsoft signs the Store distribution after certification. The separate EXE needs a trusted CA identity and reputation; it remains unsigned. No zero-bug/all-hardware claim. |

## Reproducing the automated checks

Use `scripts/dev.ps1 test`, `scripts/dev.ps1 lifecycle` and `scripts/dev.ps1 lifecycle -Software`. CI additionally runs packaging failure guards, the native session fixture, installer lifecycle and MSIX verification. Native input tests are deliberately restricted to isolated GitHub runners. Never test with personal clipboard history or bypass security protections to install an unsigned submission MSIX.

Record each outstanding device/application result with build hash, Windows version, display scale, renderer, destination, workload, observed outcome and any issue link. Publication can proceed after those results and the signed-package gate are reviewed; unsupported Windows 10 and ARM64 should remain unadvertised.
