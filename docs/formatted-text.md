# Formatted text in the next beta

Captured Unicode text can retain its original HTML and RTF formats, each bounded to 256 KiB of UTF-8. Capture settings can disable this for future copies. Invalid or oversized optional formats are omitted while valid plain text remains available. Rich-only clipboard objects without a Unicode fallback are skipped.

HTML follows Microsoft's [HTML clipboard format](https://learn.microsoft.com/en-us/windows/win32/dataxchg/html-clipboard-format): byte offsets must be ordered, within the captured UTF-8 data and on character boundaries. This is a format/bounds check, not an HTML sanitizer. Clippboard never renders the HTML, executes it or fetches linked resources. The destination application interprets original formatting when the user restores it. RTF receives bounded source-format checks and is likewise never rendered in Clippboard.

Cards and expanded notes show plain text. Formatted cards have a visible label; the expanded view offers **Copy plain text**. Edits, combinations and snippets deliberately use only the reviewed plain text. Identical words with different formats have separate history identities. Formats are encrypted with the payload and are not retained in summaries.

New formatted entries use `.clip2`, so older versions cannot silently rewrite them as plain text. Existing plain text stays readable without bulk migration. Mixed-version access to one history directory is unsupported.

Regression coverage uses synthetic Unicode HTML/RTF, byte-boundary and size failures, stream ownership, opt-out, encrypted restart, starring and format-aware deduplication. Real browser/office paste destinations remain a beta integration gate.
