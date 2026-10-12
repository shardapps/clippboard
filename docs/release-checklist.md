# Publication checklist — 0.5.1 first-release candidate

The current target is Windows 11 x64. A green build is necessary, but does not replace Microsoft certification or testing the installed Store package. Keep public publication on manual hold until the unresolved gates below pass.

| Gate | Evidence / state |
| --- | --- |
| Compile, storage, UI, clipboard formats | 203 assertions passed in [run 38146161714](https://github.com/shardapps/clippboard/actions/runs/38146161714); 192 local assertions. |
| Native text capture, hotkey, accepted paste, restart | Three controlled WPF destination cycles passed on the isolated runner; not a third-party application matrix. |
| Overlay lifetime | Eight hardware and eight software process cycles passed. Local idle/active measurements and their limits are in [performance](performance.md). |
| EXE installer lifecycle | Fresh CI directory: install, same-version reinstall, installed preview, uninstall passed. A pinned 0.5.0-source rebuild now provides a reproducible cross-version installer baseline, with encrypted text/image/file fixtures, star and preferences checked before/after upgrade. Record the new CI result before marking this gate passed. Interactive clean-device coverage remains. |
| Store package | Assigned ShardCoders identity, x64, disabled startup, contents and SHA-256 verified. Unsigned MSIX is for Store submission only. |
| Store listing and review | Corrected 0.4.0.0 passed certification on October 12. The owner chose 0.5.1.0 for the first release. Its replacement package was validated, saved and explicitly submitted on October 12: **In certification / Pre-processing**, manual publication hold preserved. Updated reviewer notes and live HTTPS 200 privacy policy verified. Fresh review remains pending. |
| Installed Store behavior | Pending signed package: first install, launch, hotkey/tray, history restart, startup opt-in/disable, update and uninstall. |
| Destination application matrix | Pending Notepad Unicode text, Paint bitmap, Explorer files, browser text and restricted/elevated manual-paste fallback. Include moved/deleted/network originals. |
| Device/accessibility matrix | Pending mixed DPI, multiple monitors, small screens, screen reader, remote session and slower hardware. Synthetic high-contrast/reduced-motion checks passed. |
| Sustained performance | 0.5.1's isolated 100-copy burst, final-item persistence, two 2048×2048 images, file references, encrypted bounds, five-minute idle and restart passed. Restarted listener working set was 65.8 MiB; private bytes 16.7 MiB. The latest local scroll profile allocated 33.1% fewer bytes than its baseline; frame scheduling outliers remain. Physical-device profiles, cold-start distributions and longer workloads remain pending. |
| Trust and public release | Microsoft signs the Store distribution after certification. The separate EXE needs a trusted CA identity and reputation; it remains unsigned. No zero-bug/all-hardware claim. |

## Reproducing the automated checks

Use `scripts/dev.ps1 test`, `scripts/dev.ps1 lifecycle` and `scripts/dev.ps1 lifecycle -Software`. CI additionally runs packaging failure guards, the native session fixture, installer lifecycle and MSIX verification. Native input tests are deliberately restricted to isolated GitHub runners. Never test with personal clipboard history or bypass security protections to install an unsigned submission MSIX.

Record each outstanding device/application result with build hash, Windows version, display scale, renderer, destination, workload, observed outcome and any issue link. Publication can proceed after those results and the signed-package gate are reviewed; unsupported Windows 10 and ARM64 should remain unadvertised.
