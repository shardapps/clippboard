# Contributing

Bugs, design feedback, documentation, and code are welcome. For a small fix, open a pull request. Discuss large features or dependencies in an issue first. Read [the architecture](docs/architecture.md) and [roadmap](docs/roadmap.md).

Use Windows 11 and .NET 10 SDK. `scripts/bootstrap.ps1` installs a project-local SDK. Run `scripts/dev.ps1 build` and `scripts/dev.ps1 test` before submitting. Never include real clipboard contents, private paths, or credentials in fixtures or reports.

## Pull requests

- Keep each change focused and explain its user-visible effect.
- Place UI/Windows code in `src/Clippboard.App`, storage in `src/Clippboard.Core`, and checks in `tests/`.
- Add meaningful regression checks for behavior changes. Tiny copy/style edits do not need artificial tests.
- Describe validation and limitations; provide sanitized screenshots for UI changes.
- Justify new dependencies and preserve bounded memory/disk usage.
- Respect reduced motion, keyboard navigation, and accessibility.

Use clear commit messages. Maintainers review before merging; green CI is required for release preparation. Contributions use Apache 2.0; no CLA is currently required. Follow [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Sensitive findings belong in the channel listed in [SECURITY.md](SECURITY.md).
