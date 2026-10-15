# Local image text extraction

In the next beta, expand an image and choose **Extract text…**, select an installed OCR language and explicitly start extraction. Recognized lines open in the ordinary editable text preview. Nothing is copied or saved until requested. Recognition can make mistakes; review the result.

This uses Windows' built-in OCR and requires package identity (the Microsoft Store installation) and an installed supported recognition language. Unpackaged EXE builds explain this requirement. Missing languages link to Windows Language settings; Clippboard does not install language components or download models automatically. See Microsoft's [OCR desktop requirements](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr) and [OCR engine documentation](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine).

There is no resident recognition model or automatic OCR indexing. The image loads only after the request. One native job can run at a time, including while a canceled operation finishes. Closing/canceling prevents publication of its result. The existing encoded PNG is adapted as a stream; the decoded Gray8 image is capped by the 16-million-pixel capture limit and scaled to Windows' maximum OCR dimension. Native bitmap and stream resources are disposed, and the owned PNG is wiped after the operation. Text is bounded by the existing 512-KiB character limit.

Tests use a controlled backend for cancellation, worker bounds, missing prerequisites, cleanup and editor handoff. The isolated unpackaged CI path verifies the package requirement. Actual Windows OCR recognition in a fresh Store-installed beta with installed languages remains a release gate; simulated recognition is not evidence for recognition accuracy or every Windows language.

For a synthetic UI screenshot, run `./scripts/dev.ps1 preview -Ocr -PreviewPath artifacts/ocr-preview.png`. This uses demo language choices and does not invoke recognition.
