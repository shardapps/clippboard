# Repository working rules

- Keep this utility in `Utility_Apps/clippboard`; future utilities use sibling folders. Tooling, caches, generated files, previews, and temporary development history belong inside this repository.
- Follow the user's scope and choices: file references, configurable 1–30-day retention including stars, bounded storage, and reference-only idle memory.
- Preserve Apache 2.0 and honest alpha status. Do not claim untested compatibility/performance.
- Use `scripts/dev.ps1` for build/test/run/publish; never commit `.data/`, `.tools/`, `.cache/`, history, secrets, or build outputs.
- Keep UI in `Views`, Windows integration in `Platform`/`Services`, and storage policy in `Clippboard.Core`.
- Encrypt clipboard contents before writing. Bound retained payloads, metadata, and rendered previews. Dispose hooks/tray resources.
- Run meaningful storage checks for storage changes and WPF builds for UI changes. Use synthetic fixtures.
- Use focused feature branches and PRs, test and review each, then merge green PRs to `main`. Never push feature changes directly to `main`. PR creation and merging are authorized by the owner. Stable release still needs completed validation.
