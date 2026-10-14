# Retrieval in the next beta source

These improvements are being developed after the published 0.5.1 alpha. The alpha downloads still search short previews.

Search finds saved full text and every original file name/path, including content beyond the card's short preview. Type, Starred and date controls can be combined. Date filters cycle between all retained history, the last 24 hours and the last seven days. Ctrl+F focuses search; Down moves from search into the first result.

Arrows, Home/End and Page Up/Down select notes. Enter restores the selected note using the saved paste preference; Shift+Enter restores to the clipboard without requesting a paste. Tab still reaches individual actions and toolbar controls. Selection follows clip identity through refreshes and clears if the note is filtered out or deleted. Offscreen selection moves the existing scroll spring and focuses the note at rest.

The listener scans small batches of up to eight encrypted payloads, one at a time. The overlay cancels obsolete requests and checks their identity before publishing results. No plaintext search index, file-content scan, network request or resident full-text corpus is created. Images are not searched. Large histories and slower disks can take longer; partial results appear while searching. A canceled page already accepted by the listener may finish its bounded work before the next request.

Text matching uses .NET's invariant linguistic comparison with case ignored. This supports Unicode equivalents without changing stored text. See Microsoft's [comparison options](https://learn.microsoft.com/en-us/dotnet/api/system.globalization.compareoptions?view=net-10.0). Status updates use a polite [WPF live region](https://devblogs.microsoft.com/dotnet/net-framework-4-7-1-accessibility-and-wpf-improvements/); physical screen-reader validation remains a beta gate.
