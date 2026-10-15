using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.Core;

public static class PortableArchive
{
    public const int MaxEntries = 1000;
    private const int MaxMetadata = 24 * 1024 * 1024, MaxImage = 16 * 1024 * 1024;
    private sealed class NoInlineImage : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new JsonException("Backup images require binary records.");
        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) => throw new JsonException("Backup images require binary records.");
    }
    private static readonly JsonSerializerOptions MetadataJson = new() { Converters = { new NoInlineImage() } };
    public static async Task ExportAsync(IHistoryAccess history, Stream output, ReadOnlyMemory<byte> password,
        CancellationToken cancellation = default, Action<int, int>? progress = null)
    {
        var entries = await history.ListAsync();
        if (entries.Count > MaxEntries) throw new InvalidDataException("Too many notes for this backup format.");
        using var writer = await ArchiveCipherStream.OpenAsync(output, password, true, cancellation);
        await WriteIntAsync(writer, entries.Count, cancellation);
        var done = 0;
        foreach (var entry in entries)
        {
            cancellation.ThrowIfCancellationRequested(); Clip? clip = null;
            try
            {
                clip = await history.GetAsync(entry.Id); cancellation.ThrowIfCancellationRequested();
                if (clip is null || !SameSnapshot(clip.Summary(entry.StoredBytes), entry))
                    throw new InvalidOperationException("History changed during export. Pause capture and try again.");
                ValidateClip(clip);
                var metadata = JsonSerializer.SerializeToUtf8Bytes(clip with { ImagePng = null }, MetadataJson);
                try
                {
                    if (metadata.Length > MaxMetadata || metadata.Length + (long)(clip.ImagePng?.Length ?? 0) > MaxMetadata)
                        throw new InvalidDataException("Backup note exceeds its record limit.");
                    await WriteIntAsync(writer, metadata.Length, cancellation); await writer.WriteAsync(metadata, cancellation);
                    await WriteIntAsync(writer, clip.ImagePng?.Length ?? 0, cancellation);
                    if (clip.ImagePng is not null) await writer.WriteAsync(clip.ImagePng, cancellation);
                }
                finally { CryptographicOperations.ZeroMemory(metadata); }
                progress?.Invoke(++done, entries.Count);
            }
            finally { if (clip?.ImagePng is byte[] png) CryptographicOperations.ZeroMemory(png); }
        }
        cancellation.ThrowIfCancellationRequested(); await writer.CompleteAsync(cancellation);
    }
    // Receive may inspect/stage one record. It must not retain its image array:
    // that owned array is wiped on callback completion, cancellation or failure.
    public static async Task ReadAsync(Stream input, ReadOnlyMemory<byte> password, Func<Clip, Task> receive,
        CancellationToken cancellation = default, Action<int, int>? progress = null)
    {
        using var reader = await ArchiveCipherStream.OpenAsync(input, password, false, cancellation);
        var count = await ReadIntAsync(reader, cancellation);
        if (count is < 0 or > MaxEntries) throw new InvalidDataException("Invalid backup note count.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var length = await ReadIntAsync(reader, cancellation);
            if (length is <= 0 or > MaxMetadata) throw new InvalidDataException("Invalid backup metadata bounds.");
            var metadata = new byte[length]; Clip clip;
            try { await reader.ReadExactlyAsync(metadata, cancellation); clip = JsonSerializer.Deserialize<Clip>(metadata, MetadataJson) ?? throw new InvalidDataException("Empty backup note."); }
            finally { CryptographicOperations.ZeroMemory(metadata); }
            if (!ids.Add(clip.Id)) throw new InvalidDataException("Duplicate backup note identity.");
            var imageLength = await ReadIntAsync(reader, cancellation);
            if (imageLength < 0 || imageLength > MaxImage || imageLength + (long)length > MaxMetadata
                || (imageLength > 0) != (clip.Kind == ClipKind.Image)) throw new InvalidDataException("Invalid backup image bounds.");
            byte[]? image = imageLength > 0 ? new byte[imageLength] : null;
            try
            {
                if (image is not null) await reader.ReadExactlyAsync(image, cancellation);
                clip = clip with { ImagePng = image }; ValidateClip(clip);
                cancellation.ThrowIfCancellationRequested(); await receive(clip); progress?.Invoke(index + 1, count);
            }
            finally { if (image is not null) CryptographicOperations.ZeroMemory(image); }
        }
        var extra = new byte[1];
        if (await reader.ReadAsync(extra, cancellation) != 0) throw new InvalidDataException("Unexpected records after backup contents.");
        // This read authenticates the required terminator and rejects trailing
        // bytes. Only then may a caller commit staged entries to live history.
    }
    internal static bool SameContent(ClipSummary left, ClipSummary right) => left.Kind == right.Kind && left.IsSnippet == right.IsSnippet
        && left.Fingerprint == right.Fingerprint && left.Title == right.Title && (left.Labels ?? []).SequenceEqual(right.Labels ?? [], StringComparer.Ordinal);
    private static bool SameSnapshot(ClipSummary left, ClipSummary right) => left.Id == right.Id && left.CopiedAt == right.CopiedAt
        && left.Pinned == right.Pinned && SameContent(left, right);
    private static void ValidateClip(Clip clip)
    {
        if (clip.Id is null || clip.Id.Length != 32 || !clip.Id.All(Uri.IsHexDigit) || !Enum.IsDefined(clip.Kind)
            || clip.CopiedAt > DateTimeOffset.UtcNow.AddMinutes(5) || clip.Title?.Length > 64 || clip.Title?.Any(char.IsControl) == true
            || clip.Labels is null || clip.Labels.Length > 8 || clip.Labels.Any(label => string.IsNullOrWhiteSpace(label) || label.Length > 24 || label.Any(char.IsControl)))
            throw new InvalidDataException("Invalid backup note metadata.");
        RichTextFormats.Validate(clip);
        if (clip.IsSnippet) TextReuse.ValidateSnippet(clip.Text ?? "", clip.Title ?? "", clip.Labels);
        if (clip.Kind == ClipKind.Text)
        {
            if (string.IsNullOrWhiteSpace(clip.Text) || clip.Text.Length > TextReuse.MaxTextLength || clip.ImagePng is not null || clip.FilePaths is not null)
                throw new InvalidDataException("Invalid backup text.");
        }
        else if (clip.Kind == ClipKind.Files)
        {
            if (clip.IsSnippet || clip.Text is not null || clip.ImagePng is not null || clip.FilePaths is not { Length: > 0 and <= 100 } paths
                || paths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Contains('\0') || !Path.IsPathFullyQualified(path)))
                throw new InvalidDataException("Invalid backup file references.");
        }
        else
        {
            if (clip.IsSnippet || clip.Text is not null || clip.FilePaths is not null || clip.ImagePng is not { Length: >= 24 and <= MaxImage } png
                || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                throw new InvalidDataException("Invalid backup PNG.");
            var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)); var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20));
            if (width == 0 || height == 0 || (ulong)width * height > 16_000_000) throw new InvalidDataException("Backup image exceeds its pixel limit.");
        }
    }
    private static async Task WriteIntAsync(Stream stream, int number, CancellationToken cancellation)
    { var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, number); await stream.WriteAsync(bytes, cancellation); }
    private static async Task<int> ReadIntAsync(Stream stream, CancellationToken cancellation)
    { var bytes = new byte[4]; await stream.ReadExactlyAsync(bytes, cancellation); return BinaryPrimitives.ReadInt32LittleEndian(bytes); }
}
