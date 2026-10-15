using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Clippboard.Core;

public sealed partial class HistoryStore
{
    private static ReadOnlySpan<byte> ImageMagic => "CLIPIMG2"u8;
    private const int ImageHeaderBytes = 16;
    private const int ImageMetadataLimit = 64 * 1024;
    private const int ImageCipherLimit = 16 * 1024 * 1024 + ImageMetadataLimit;
    private sealed record StoredImage(Clip Metadata, string Fingerprint);
    private sealed record PreparedPayload(byte[] Metadata, byte[]? Image = null, bool Modern = false)
    {
        internal long Length => Metadata.LongLength + (Image?.LongLength ?? 0) + (Image is null ? 0 : ImageHeaderBytes);
    }
    private sealed class NoInlineBytes : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new JsonException("Binary image metadata cannot contain inline bytes.");
        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) => throw new JsonException("Binary image metadata cannot contain inline bytes.");
    }
    private static readonly JsonSerializerOptions ImageMetadataJson = new() { Converters = { new NoInlineBytes() } };
    private PreparedPayload PreparePayload(Clip clip, string? fingerprint = null)
    {
        if (clip.Kind != ClipKind.Image || clip.ImagePng is null)
            return new(Encrypt(clip), Modern: clip.IsSnippet || clip.Title is not null || (clip.Labels?.Length ?? 0) > 0);
        if (clip.ImagePng.Length > 16 * 1024 * 1024) throw new InvalidDataException("Image exceeds its encoded limit.");
        var metadata = Encrypt(new StoredImage(clip with { ImagePng = null }, fingerprint ?? clip.Fingerprint()), ImageMetadataJson);
        if (metadata.Length > ImageMetadataLimit) throw new InvalidDataException("Image metadata exceeds its limit.");
        // Protect the existing encoded PNG directly. No Base64 string, JSON
        // image array or aggregate encrypted-image send buffer is required.
        var image = protector.Protect(clip.ImagePng);
        if (image.Length > ImageCipherLimit) throw new InvalidDataException("Protected image exceeds its limit.");
        return new(metadata, image, true);
    }
    private async Task<Clip?> TryReadImageAsync(FileStream input)
    {
        var header = new byte[ImageHeaderBytes];
        var read = await input.ReadAtLeastAsync(header, ImageHeaderBytes, false);
        if (read < ImageMagic.Length || !header.AsSpan(0, ImageMagic.Length).SequenceEqual(ImageMagic)) { input.Position = 0; return null; }
        if (read != ImageHeaderBytes) throw new InvalidDataException("Truncated image header.");
        var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var imageLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        if (metadataLength is <= 0 or > ImageMetadataLimit || imageLength is <= 0 or > ImageCipherLimit
            || (long)metadataLength + imageLength + ImageHeaderBytes != input.Length || input.Length > _limits.MaxEntryBytes)
            throw new InvalidDataException("Invalid binary image bounds.");
        var metadata = new byte[metadataLength]; await input.ReadExactlyAsync(metadata);
        var plainMetadata = protector.Unprotect(metadata); StoredImage stored;
        try { stored = JsonSerializer.Deserialize<StoredImage>(plainMetadata, ImageMetadataJson) ?? throw new InvalidDataException("Empty image metadata."); }
        finally { CryptographicOperations.ZeroMemory(plainMetadata); }
        if (stored.Metadata?.Kind != ClipKind.Image || stored.Metadata.ImagePng is not null || stored.Metadata.Id != Path.GetFileNameWithoutExtension(input.Name))
            throw new InvalidDataException("Image metadata identity mismatch.");
        var encryptedImage = new byte[imageLength]; await input.ReadExactlyAsync(encryptedImage);
        byte[]? image = protector.Unprotect(encryptedImage);
        try
        {
            if (image.Length > 16 * 1024 * 1024) throw new InvalidDataException("Decoded image exceeds its encoded limit.");
            var clip = stored.Metadata with { ImagePng = image };
            if (!string.Equals(clip.Fingerprint(), stored.Fingerprint, StringComparison.Ordinal)) throw new InvalidDataException("Image content does not match its metadata.");
            image = null; return clip;
        }
        finally { if (image is not null) CryptographicOperations.ZeroMemory(image); }
    }
}
