using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Clippboard.Core;

namespace Clippboard.App.Services;

// Called on the UI dispatcher. Loading/decoding runs off it; no plaintext temp images are created.
internal sealed class PreviewCache(IHistoryAccess store)
{
    private sealed record Entry(BitmapSource Image, long Bytes, LinkedListNode<string> Node);
    private readonly Dictionary<string, Entry> _images = new();
    private readonly LinkedList<string> _recency = new();
    private readonly SemaphoreSlim _decodeSlots = new(2, 2);
    private long _bytes;
    public long BudgetBytes { get; set; } = 16 * 1024 * 1024;
    public long RetainedBytes => _bytes;
    public int Count => _images.Count;

    public async Task<BitmapSource?> GetAsync(string id, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_images.TryGetValue(id, out var cached)) { _recency.Remove(cached.Node); _recency.AddFirst(cached.Node); return cached.Image; }
        await _decodeSlots.WaitAsync(cancellation);
        BitmapSource? image;
        try { image = await Task.Run(async () =>
        {
            var clip = await store.GetAsync(id);
            cancellation.ThrowIfCancellationRequested();
            if (clip?.ImagePng is null) return null;
            try { return Decode(clip.ImagePng, 360, 230); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(clip.ImagePng); }
        }, cancellation); }
        finally { _decodeSlots.Release(); }
        cancellation.ThrowIfCancellationRequested();
        if (image is null) return null;
        var bytes = (long)image.PixelWidth * image.PixelHeight * 4;
        // Concurrent requests may finish for the same item; replace rather than double count.
        Remove(id);
        while (_bytes + bytes > BudgetBytes && _recency.Last is not null) Remove(_recency.Last.Value);
        if (bytes <= BudgetBytes)
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
        var stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)]; source.CopyPixels(pixels, stride, 0);
        var detached = BitmapSource.Create(source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        detached.Freeze(); return detached;
    }

    public void Remove(string id)
    {
        if (!_images.Remove(id, out var entry)) return;
        _recency.Remove(entry.Node); _bytes -= entry.Bytes;
    }
    public void Clear() { _images.Clear(); _recency.Clear(); _bytes = 0; }
}
