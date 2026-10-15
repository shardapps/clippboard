# Combining text in the next beta source

Select text notes using their + action, Ctrl-click or Ctrl+Space on a keyboard-selected note. **Combine (n)** opens a preview. Selections follow clip identity through recycled cards and filters; deleted/expired entries are removed when history refreshes. Closing the rope clears the selection.

Choose selection order, newest first or oldest first. Separators include blank line, line break, comma, space, tab and a custom literal string up to 32 characters. Review the plain-text output before copying it or opening the editor to change/save it as a snippet. Original notes and their formatting are preserved. **Clear selection** removes the group.

The operation supports the product's maximum 1,000 history entries and the existing 524,288-code-unit output limit. Payloads load sequentially, cancellation is checked around each read, and no result is emitted for a partially missing group. Images/files cannot be included as text implicitly. Background preview builds are serialized; obsolete configurations cannot overwrite a newer choice. Closing cancels outstanding preview work.

Label filtering now opens a searchable picker instead of cycling through every label. Its item controls recycle even with thousands of available labels. Choose a result by clicking or pressing Enter; **All labels** removes the filter. This does not read full clipboard payloads.

Synthetic checks cover Unicode/newlines, chosen order/separators, deleted sources, duplicate identity, aggregate bounds, cancellation, unexpected-image cleanup, explicit copy, recycled selection state, 8,000-label virtualization/search and keyboard apply. Physical keyboard/screen-reader and device presentation remain beta gates.

Reproduce the synthetic UI without accessing the system clipboard with `./scripts/dev.ps1 preview -Combine`.
