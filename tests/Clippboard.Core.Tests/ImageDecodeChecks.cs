using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clippboard.App.Services;

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
