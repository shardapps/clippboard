# Clippboard Store presentation

Positioning: **Copied it? Keep it close.** Lead with a familiar job—finding and reusing something copied—then show the rope, search, snippets, themes, backup and optional OCR.

`listing.json` is the reviewed source for English description, short description, eight feature bullets, seven keywords and screenshot captions. Use natural, relevant search phrases and direct answers about identity, Windows support, privacy, files and shortcuts. This improves clarity for people and machine readers; no Store-search or generative-answer ranking is promised. The registered product name stays Clippboard. No competitor names, invented awards, fixed-RAM claims or unsupported platform promises are used.

## Reproduce the screenshots

Run `./scripts/dev.ps1 store-assets` on Windows. The tool references the actual WPF app views and existing synthetic preview methods. It adds an original wallpaper and a clearly labelled sample workspace behind those views, loads an in-memory fixture, then renders six 1600×900 opaque PNGs under `artifacts/store-listing/`. No clipboard listener, personal history, installed app instance, startup setting, cloud service or restore action is used. The separate tooling is not included in the application or installer.

Review every image at full size before copying selected outputs into `screenshots/` and uploading. The tool checks dimensions, PNG decoding, full opacity and the Store's 50 MB size ceiling. It fails if a required preview method disappears rather than silently inventing a screen. OCR language choices in its preview are illustrative; captions retain the installed-language requirement. The backup view illustrates the real screen, not a completed transfer.

Screenshots use the same layout and actual controls as beta.5, with synthetic content. They are staged application renders, not evidence of a real destination-app paste, performance, or the device matrix. Marketing phrases belong in the listing and website, not over the screenshot UI.

## Store rules and release handling

- [MSIX screenshot guidance](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images): actual UI, no added marketing messages, PNG desktop images at least 1366×768, under 50 MB, captions up to 200 characters. Keep key information above the lower third.
- [Listing information](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info): natural keywords and clear benefits; feature bullets stay within 200 characters.
- [Keyword limits](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-additional-information): seven terms, 40 characters each, at most 21 words in total. Preserve the previously corrected privacy URL and do not reintroduce the rejected “Windows utility” term.

Listing-only changes still need Microsoft review. Inspect the actual Partner Center state before editing; if the current certification must be cancelled to update metadata, preserve the already validated 0.6.0.0 package and resubmit with publication after approval. Never represent uploaded/draft/in-review copy as live.
