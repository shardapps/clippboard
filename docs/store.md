# Microsoft Store distribution

The Store account reserved **Clippboard**, Store ID `9PP8015JDJ45`, under publisher display name **ShardCoders**. This is a separate distribution path from the unsigned GitHub EXE. Uploading is not certification, public availability or a trusted signature on the standalone installer.

The initial certification report for version **0.4.0.0** rejected two listing items: **10.1.3**, the search term “Windows utility”, and **10.5.1**, a privacy-policy URL that did not resolve for the reviewer. Those listing corrections passed review, but the owner selected **0.5.1.0** for the first release. Its replacement MSIX was validated, saved and submitted on October 12. On October 13, Partner Center explicitly reported **passed certification / Ready to publish**; Current packages confirmed `Clippboard-0.5.1-x64.msix`, version 0.5.1.0, x64 and minimum OS 10.0.22000.0. Following the owner's explicit initial-alpha publication request, **Publish now** was selected. Observed status: **In publishing**. The public Store page had not appeared in the Pakistan catalog at the first check. Catalog rollout and Store-signed installation remain separate verification steps. The older 0.4.0 approval was not substituted for the new review.

The 0.5.1.0 x64 MSIX comes from successful [run 38146161714](https://github.com/shardapps/clippboard/actions/runs/38146161714), tested source `e20a06a18bc309bc0311a57d1065077994544def` (the same source tree merged in PR #21). Its SHA-256 is `4e4993e420a5dc0a71c2737bdb71750f1ba2e82464865cf3c2f5dd3bf2a1490a`. The package remains unsigned for Store ingestion; record Partner Center's new review status separately from package validation and prior approval.

**Rollout completed:** the [public Microsoft Store listing](https://apps.microsoft.com/detail/9PP8015JDJ45) was verified on October 13, including the Pakistan catalog, with publisher **ShardCoders** and official **Download** / **View in Store** controls. The earlier In publishing and catalog-not-found observations describe the rollout before it completed. A public listing is not evidence of an installed package's signature or behavior; those checks remain tracked separately.

## Listing correction and policy publication

Use the dedicated public privacy URL: **https://shardapps.github.io/clippboard/privacy/**. Its source is `site/privacy/index.html`. The site contains no JavaScript, analytics, forms or external assets, and works without a GitHub account. Keep this page aligned with the technical [privacy documentation](privacy.md).

The six search terms are `clipboard history`, `copy paste`, `paper notes`, `offline`, `open source`, and `clipboard manager`. Do not re-add the rejected “Windows utility” term or another product's title. Check Microsoft's current keyword rules before changing listing terms.

Website changes go through a PR. Run `python scripts/check-site.py`; CI repeats the page, local-link and privacy checks. After green checks and merge, an authorized maintainer manually runs **Privacy website** on `main` to deploy only `site/` through the GitHub Pages environment. Before saving a Store privacy URL, verify an unauthenticated HTTPS request returns 200 with readable policy HTML and open it in a browser. Recheck the live URL immediately before every submission. A successful deployment is not a substitute for checking the public page.

The original listing-only resubmission retained 0.4.0.0 and passed review. The later 0.5.1.0 package replacement requires its own certification. Retain the corrected keyword list and public privacy URL, and record observed review status separately from expectations.

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

The initial alpha is being published at the owner's explicit request after fresh certification. Continue validating the Store-signed package on a clean Windows 11 device: first install, updates, hotkey and tray, startup opt-in/disable, encrypted history persistence and uninstall. Compatibility and performance checks in [the release checklist](release-checklist.md) remain requirements before declaring a stable release. MakeAppx validation and Microsoft certification are not substitutes for an installed behavior test.

[Package flights require an initially published submission](https://learn.microsoft.com/en-us/windows/apps/publish/package-flights). Once the public catalog is available, acquire the app through the official Store and verify its version, publisher signature and installed behavior. Do not sideload the unsigned ingestion MSIX to manufacture a passing installation result.
