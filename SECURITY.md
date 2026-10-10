# Security policy

This is an early alpha with no stable release. Security fixes target `main` and, once releases exist, the latest supported release. Do not treat clipboard history as a secure password vault.

Use [private vulnerability reporting](https://github.com/shardapps/clippboard/security/advisories/new). Include affected version/commit, Windows version, impact, and a synthetic reproduction. Do not publish exploit details, actual history, passwords, or private paths in Issues. Maintainers aim to acknowledge within seven days but cannot guarantee an SLA. We will coordinate investigation and disclosure with reporters.

## Boundaries

- History is local and encrypted with Windows DPAPI for the current user; malware running as you may still read/decrypt it.
- Pause capture for secrets. Supported exclusion markers help but are not comprehensive secret detection.
- File paths are encrypted; restoring files requests a copy and never executes them or replays cut operations.
- The app runs without elevation and does not bypass Windows input restrictions.
- No telemetry/cloud sync is included. Opening the project link launches your browser.
- Unsigned builds may trigger SmartScreen. Signing/provenance remain release prerequisites.

See [privacy details](docs/privacy.md). Ordinary bugs belong in Issues.
