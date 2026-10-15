using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clippboard.App.Platform;
using Clippboard.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Clippboard.App.Services;

internal sealed record OcrLanguage(string Tag, string Name);
internal sealed record OcrAvailability(IReadOnlyList<OcrLanguage> Languages, string? Message = null);
internal sealed class OcrUnavailableException(string message) : InvalidOperationException(message);
internal interface IOcrBackend
{
    IReadOnlyList<OcrLanguage> Languages();
    Task<string> RecognizeAsync(byte[] png, string language, CancellationToken cancellation);
}
internal sealed class LocalOcr(IOcrBackend? backend = null, Func<bool>? packaged = null)
{
    private readonly IOcrBackend _backend = backend ?? new WindowsOcrBackend();
    private readonly Func<bool> _packaged = packaged ?? (() => PackageIdentity.IsPackaged);
    // Canceling a view discards results. Keep this slot until native recognition
    // finishes, so repeated cancellations cannot create parallel native jobs.
    private static readonly SemaphoreSlim Worker = new(1, 1);
    internal OcrAvailability Availability()
    {
        if (!_packaged()) return new([], "Local text extraction requires the Microsoft Store version of Clippboard.");
        try
        {
            var languages = _backend.Languages();
            return languages.Count == 0 ? new([], "No Windows OCR language is installed. Add a supported language in Windows Language settings, then reopen this panel.") : new(languages);
        }
        catch (Exception ex) when (Unavailable(ex)) { return new([], "Windows text recognition is unavailable on this device. Check Windows language components and updates."); }
    }
    internal async Task<string> ExtractAsync(Func<Task<Clip?>> load, string language, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var availability = Availability();
        if (availability.Message is not null) throw new OcrUnavailableException(availability.Message);
        if (!availability.Languages.Any(item => item.Tag == language)) throw new OcrUnavailableException("Choose an installed recognition language.");
        if (!await Worker.WaitAsync(0, cancellation)) throw new OcrUnavailableException("A previous extraction is still finishing. Try again shortly.");
        Clip? clip = null;
        try
        {
            clip = await load(); cancellation.ThrowIfCancellationRequested();
            if (clip?.Kind != ClipKind.Image || clip.ImagePng is not { Length: >= 8 and <= 16 * 1024 * 1024 } png
                || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new OcrUnavailableException("This image expired, was deleted or is not a supported PNG.");
            var text = await _backend.RecognizeAsync(png, language, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (text.Length > TextReuse.MaxTextLength) throw new OcrUnavailableException("Recognized text exceeds the editable text limit. Use a smaller image.");
            if (string.IsNullOrWhiteSpace(text)) throw new OcrUnavailableException("No text was found. Try another language or a clearer image.");
            return text;
        }
        catch (Exception ex) when (ex is not OcrUnavailableException && Unavailable(ex))
        { throw new OcrUnavailableException("Windows could not read this image. Try a clearer image or another installed language."); }
        finally { if (clip?.ImagePng is byte[] bytes) CryptographicOperations.ZeroMemory(bytes); Worker.Release(); }
    }
    private static bool Unavailable(Exception ex) => ex is COMException or TypeInitializationException or TypeLoadException
        or DllNotFoundException or NotSupportedException or ArgumentException or InvalidOperationException;
}
internal sealed class WindowsOcrBackend : IOcrBackend
{
    public IReadOnlyList<OcrLanguage> Languages() => OcrEngine.AvailableRecognizerLanguages
        .Select(language => new OcrLanguage(language.LanguageTag, language.DisplayName)).ToArray();
    public async Task<string> RecognizeAsync(byte[] png, string language, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var engine = OcrEngine.TryCreateFromLanguage(new Language(language))
            ?? throw new OcrUnavailableException("This Windows OCR language is no longer available. Reopen the panel.");
        using var stream = new MemoryStream(png, false);
        using var random = stream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(random);
        cancellation.ThrowIfCancellationRequested();
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (ulong)decoder.PixelWidth * decoder.PixelHeight > 16_000_000)
            throw new OcrUnavailableException("This image exceeds the capture pixel limit. Use a smaller image.");
        var scale = Math.Min(1, OcrEngine.MaxImageDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform { ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)), ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)) };
        // Gray8 bounds decoded pixels to one byte per pixel. The encoded stream
        // adapts the existing owned PNG; no second encoded-image copy is made.
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Gray8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        cancellation.ThrowIfCancellationRequested();
        var result = await engine.RecognizeAsync(bitmap);
        cancellation.ThrowIfCancellationRequested();
        var text = new StringBuilder();
        foreach (var line in result.Lines)
        {
            if (text.Length + line.Text.Length + 1 > TextReuse.MaxTextLength) throw new OcrUnavailableException("Recognized text exceeds the editable text limit. Use a smaller image.");
            if (text.Length > 0) text.Append('\n'); text.Append(line.Text);
        }
        return text.ToString();
    }
}
