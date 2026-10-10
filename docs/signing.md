# Windows publisher signing and SmartScreen

The current alpha installer is unsigned. The **Unknown publisher** screen is Windows checking trust and reputation, not an installer exception. Renaming an installer, disabling protection or using a self-signed certificate does not establish a trusted public publisher.

Microsoft evaluates publisher and file reputation. Signing displays the certificate's verified publisher, but a new signed app can still receive an unrecognized-app warning. EV certificates no longer grant automatic reputation. Store distribution has a different trust path. See [Microsoft's SmartScreen guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).

## What the owner needs

Obtain an eligible public Windows code-signing identity from a trusted provider, with identity validation and secure key custody. Options include a CA-issued certificate or Microsoft's [Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/overview), subject to region and identity eligibility. The verified publisher comes from the certificate; a GitHub organization name alone does not establish that identity. No certificate, subscription or signing credentials are currently configured.

The included adapter supports a CA-issued certificate accessible through **CurrentUser → Personal** on a dedicated Windows runner, including a hardware-backed certificate whose provider is installed. It does not export private keys, accept PFX passwords on a command line or install self-signed roots. An Artifact Signing cloud adapter would require the owner's service configuration and is not implemented by this certificate-store adapter.

Install Windows SDK SignTool and Inno Setup 6 on the signing runner. Obtain the certificate's public thumbprint and SignTool path, then run:

```powershell
./scripts/dev.ps1 test
./scripts/dev.ps1 publish
./scripts/package.ps1 -SkipPublish -CertificateThumbprint '<40-hex public thumbprint>' -SignToolPath 'C:\Program Files (x86)\Windows Kits\10\bin\<SDK version>\x64\signtool.exe'
./scripts/verify-package.ps1 -RequireSigned
```

Packaging signs the app **before** compiling the installer. Inno signs setup and its embedded uninstaller. SignTool uses SHA-256 and RFC3161 timestamping, verifies Authenticode trust and timestamps, and fails if the selected certificate or timestamp is unavailable. Final checksums are generated after signing. `build-info.json` records actual signing state; evaluation builds remain explicitly unsigned. Do not modify signed binaries.

## Optional, disabled workflow

`signed-build.yml` is manual and main-only. It remains disabled until repository variable `CODE_SIGNING_ENABLED=true` is set. Before enabling it, configure:

- A **dedicated disposable** self-hosted Windows x64 runner labeled `clippboard-signing`, with the trusted certificate/key provider, SDK SignTool and Inno Setup. Do not attach a personal desktop or expose signing keys to PR jobs.
- A protected `signed-builds` environment with required reviewers and main-only deployment restrictions. The owner must configure these settings before enabling signing.
- Environment variables `CODE_SIGNING_THUMBPRINT` and `CODE_SIGNING_TOOL_PATH`. Neither contains a private key. Enable the repository flag only after configuration is complete.

The workflow builds, signs, verifies and tests installation, then uploads **evaluation artifacts**. It does not publish a release or guarantee SmartScreen approval. Ordinary PR CI uses disposable GitHub-hosted Windows runners with no signing-key access. The signed path cannot be validated end to end until a real signing identity is supplied.

[SignTool reference](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool), [Inno SignTool integration](https://jrsoftware.org/ishelp/topic_setup_signtool.htm), [signed uninstallers](https://jrsoftware.org/ishelp/topic_setup_signeduninstaller.htm).
