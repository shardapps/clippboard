using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clippboard.App.Services;
using Clippboard.App.Platform;

internal static partial class Program
{
    private static byte[] SyntheticDecodePng(int size, out byte[] pixels)
    {
        pixels = new byte[size * size * 4]; new Random(173).NextBytes(pixels);
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = (byte)(index % 256);
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4); bitmap.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new BoundedImageBuffer(16 * 1024 * 1024); encoder.Save(output); return output.ToArray();
    }
    private static void CheckNativeImageCopy()
    {
        var png = SyntheticDecodePng(1024, out var pixels);
        var denied = false;
        try { _ = PreviewCache.Decode(png, 1024, 1024, availableBytes: 0); } catch (ImageMemoryException) { denied = true; }
        Check(denied && png.Any(value => value != 0), "known exhausted headroom rejects decode before full raster allocation without damaging the original");
        Check(MemoryAvailability.Headroom(1000, 200) == 200 && MemoryAvailability.Headroom(200, 1000) == 200
            && MemoryAvailability.Headroom(ulong.MaxValue, ulong.MaxValue) == long.MaxValue, "memory policy uses the smaller physical or commit headroom without overflow");
        Check(!MemoryAvailability.ImageFits(16_000_000, 16_000_000, 16 * 1024 * 1024, 512L * 1024 * 1024)
            && MemoryAvailability.ImageFits(16_000_000, 16_000_000, 16 * 1024 * 1024, 1024L * 1024 * 1024), "large-image admission adapts to current headroom instead of machine capacity labels");
        Check(!MemoryAvailability.ImageFits(long.MaxValue, 1, 1, long.MaxValue) && !MemoryAvailability.ImageFits(1, 0, 1, long.MaxValue)
            && MemoryAvailability.ImageFits(1024, 1024, 1024, -1), "image estimates reject invalid dimensions and retain bounded operation when the memory query is unavailable");
        _ = PreviewCache.Decode(png, 1024, 1024);
        var before = GC.GetTotalAllocatedBytes(true); BitmapSource? decoded = null;
        for (var index = 0; index < 8; index++) decoded = PreviewCache.Decode(png, 1024, 1024);
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Console.WriteLine($"Eight 1024x1024 detached decodes allocated {allocated} managed bytes");
        Check(allocated < pixels.LongLength * 2, "full-size decoding avoids a managed pixel-array copy for every image");
        CryptographicOperations.ZeroMemory(png);
        var actual = new byte[pixels.Length]; decoded!.CopyPixels(actual, 4096, 0);
        Check(decoded.IsFrozen && decoded.PixelWidth == 1024 && decoded.PixelHeight == 1024 && actual.SequenceEqual(pixels),
            "frozen native image copy preserves all BGRA pixels after the source PNG is wiped");
        CryptographicOperations.ZeroMemory(actual); CryptographicOperations.ZeroMemory(pixels);
    }
}
