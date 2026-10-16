namespace Clippboard.Core;

// Persistent format/capture safety bounds, independent of per-device RAM policy.
public static class ImageLimits
{
    public const int MaxPixels = 16_000_000;
    public const int MaxEncodedBytes = 16 * 1024 * 1024;
}
