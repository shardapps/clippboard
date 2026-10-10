# Microsoft Store distribution

The Store account reserved **Clippboard**, Store ID `9PP8015JDJ45`, under publisher display name **ShardCoders**. This is a separate distribution path from the unsigned GitHub EXE. Uploading is not certification, public availability or a trusted signature on the standalone installer.

The manifest uses the assigned public package identity:

- Name: `ShardCoders.Clippboard`
- Publisher: `CN=D11F3529-FC83-43F5-B8D5-F94F40D30E7D`
- Package family: `ShardCoders.Clippboard_rrja3x7faz716`
- Initial target: Windows 11 x64. ARM64 compatibility is not advertised.

Run `./scripts/store-package.ps1` on a native Windows filesystem with .NET 10 and the Windows SDK installed. It publishes the runtime inside the package, generates original logo assets, validates the manifest with MakeAppx, and records the SHA-256 and unsigned submission status. CI uploads the resulting MSIX as an evaluation artifact. The Google Drive development volume cannot update app-host executable resources; use the hosted output for submissions.

Submit the unsigned MSIX to Partner Center. [Microsoft signs Store packages after certification](https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide). Do not sideload this unsigned submission artifact, add a self-signed trusted root on a user's computer or disable SmartScreen. Store signing does not sign the separate EXE and cannot guarantee that a newly CA-signed EXE will have SmartScreen reputation.

## Runtime and startup

The desktop app requires `runFullTrust` for the Windows clipboard listener, hotkey, notification-area icon, foreground-window tracking and guarded paste. It has no application cloud service or telemetry. Certification notes must explain that clipboard contents stay encrypted locally and can include personal information; the listing links to the privacy policy.

Packaged installations do not write an executable path into the registry Run key. The manifest declares a disabled-by-default Windows startup task. After opening the app once, users can enable it in Windows Settings → Apps → Startup using the tray command. Development runs do not change startup. Store and EXE data can be subject to different Windows package virtualization rules; cross-channel migration is not promised.

## Release gate

Before public availability, validate a Store-signed private flight on a clean Windows 11 device: first install, updates, hotkey and tray, startup opt-in/disable, encrypted history persistence and uninstall. Complete the Store listing, truthful content rating, capability review, and certification. Keep public availability on hold until compatibility and performance gates in [the roadmap](roadmap.md) have passed. MakeAppx validation is not a packaged installation test.
