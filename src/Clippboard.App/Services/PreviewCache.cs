using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Clippboard.Core;
using Clippboard.App.Platform;

namespace Clippboard.App.Services;

// Called on the UI dispatcher. Loading/decoding runs off it; no plaintext temp images are created.
internal sealed class PreviewCache(IHistoryAccess store)
{
    private sealed record Entry(BitmapSource Image, long Bytes, LinkedListNode<string> Node);
    private readonly Dictionary<string, Entry> _images = new();
    private readonly LinkedList<string> _recency = new();
    private readonly SemaphoreSlim _decodeSlots = new(MemoryAvailability.DecodeConcurrency(MemoryAvailability.AvailableBytes(), Environment.ProcessorCount, new AppSettings().HistoryLimits().MaxEntryBytes), 2);
    private long _bytes;
    private long _generation;
    private long _budgetBytes = 16 * 1024 * 1024;
    public long BudgetBytes
    {
        get => _budgetBytes;
        set { _budgetBytes = Math.Max(0, value); while (_bytes > _budgetBytes && _recency.Last is not null) Remove(_recency.Last.Value); }
    }
    public long RetainedBytes => _bytes;
    public int Count => _images.Count;

    public async Task<BitmapSource?> GetAsync(string id, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_images.TryGetValue(id, out var cached)) { _recency.Remove(cached.Node); _recency.AddFirst(cached.Node); return cached.Image; }
        var generation = _generation;
        await _decodeSlots.WaitAsync(cancellation);
        // A request queued behind an identical load can reuse its result.
        if (_images.TryGetValue(id, out cached)) { _decodeSlots.Release(); cancellation.ThrowIfCancellationRequested(); _recency.Remove(cached.Node); _recency.AddFirst(cached.Node); return cached.Image; }
        BitmapSource? image;
        try { image = await Task.Run(async () =>
        {
            var png = await store.GetPreviewAsync(id);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                return png is null ? null : Decode(png, 360, 230);
            }
            finally { if (png is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(png); }
        }, cancellation); }
        finally { _decodeSlots.Release(); }
        cancellation.ThrowIfCancellationRequested();
        if (image is null) return null;
        if (generation != _generation) return image;
        var bytes = (long)image.PixelWidth * image.PixelHeight * 4;
        // Concurrent requests may finish for the same item; replace rather than double count.
        Remove(id);
        while (_bytes + bytes > BudgetBytes && _recency.Last is not null) Remove(_recency.Last.Value);
        if (generation == _generation && bytes <= BudgetBytes)
        {
            var node = _recency.AddFirst(id); _images[id] = new(image, bytes, node); _bytes += bytes;
        }
        return image;
    }

    internal static BitmapSource Decode(byte[] png, int width, int height)
    {
        using var stream = new MemoryStream(png, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 16_000_000) throw new InvalidDataException("Image dimensions exceed the capture limit.");
        var scale = Math.Min(1, Math.Min((double)width / Math.Max(1, frame.PixelWidth), (double)height / Math.Max(1, frame.PixelHeight)));
        stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
        bitmap.DecodePixelHeight = Math.Max(1, (int)(frame.PixelHeight * scale));
        bitmap.StreamSource = stream; bitmap.EndInit();
        // BitmapImage retains StreamSource even with OnLoad. Copy only decoded
        // pixels into an independent source so LRU entries cannot retain full PNGs.
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var detached = new WriteableBitmap(source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY, PixelFormats.Bgra32, null);
        detached.Lock();
        try
        {
            // Copy directly between native image buffers. A full-size managed
            // pixel array would duplicate up to 64 MB for a single allowed image.
            source.CopyPixels(Int32Rect.Empty, detached.BackBuffer, checked(detached.BackBufferStride * source.PixelHeight), detached.BackBufferStride);
            detached.AddDirtyRect(new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }
        finally { detached.Unlock(); }
        detached.Freeze(); return detached;
    }

    internal static byte[] EncodeThumbnail(byte[] original)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Decode(original, 360, 230)));
        using var output = new BoundedImageBuffer(HistoryStore.MaxPreviewBytes);
        encoder.Save(output); return output.ToArray();
    }

    public void Remove(string id)
    {
        if (!_images.Remove(id, out var entry)) return;
        _recency.Remove(entry.Node); _bytes -= entry.Bytes;
    }
    public void Clear() { ++_generation; _images.Clear(); _recency.Clear(); _bytes = 0; }
}
