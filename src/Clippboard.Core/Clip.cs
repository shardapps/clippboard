using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Clippboard.Core;

public enum ClipKind { Text, Image, Files }

public sealed record Clip
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; init; }
    public DateTimeOffset CopiedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Pinned { get; init; }
    public string? Text { get; init; }
    public string? Html { get; init; }
    public string? Rtf { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasRichText => Kind == ClipKind.Text && (Html is not null || Rtf is not null);
    public byte[]? ImagePng { get; init; }
    public string[]? FilePaths { get; init; }
    public bool IsSnippet { get; init; }
    public string? Title { get; init; }
    public string[] Labels { get; init; } = [];

    public string Fingerprint()
    {
        if (HasRichText) return RichTextFormats.Fingerprint(Text, Html, Rtf);
        var content = Kind switch
        {
            ClipKind.Text => Encoding.UTF8.GetBytes(Text ?? ""),
            ClipKind.Image => ImagePng ?? [],
            ClipKind.Files => Encoding.UTF8.GetBytes(string.Join('\0', FilePaths ?? [])),
            _ => throw new ArgumentOutOfRangeException()
        };
        try { return $"{(IsSnippet ? "Snippet" : Kind.ToString())}:{Convert.ToHexString(SHA256.HashData(content))}"; }
        finally { if (Kind != ClipKind.Image) CryptographicOperations.ZeroMemory(content); }
    }

    public ClipSummary Summary(long storedBytes, string? fingerprint = null) => new(
        Id, Kind, CopiedAt, Pinned, Kind switch
        {
            ClipKind.Text => Abbreviate(Text ?? ""),
            ClipKind.Image => "Copied image",
            ClipKind.Files => FilePreview(),
            _ => ""
        }, fingerprint ?? Fingerprint(), storedBytes, IsSnippet, Title, (Labels ?? []).ToArray(), HasRichText);

    private static string Abbreviate(string value)
    {
        const int limit = 320;
        if (value.Length <= limit) return value;
        var end = 0;
        while (end < limit)
        {
            // Two units of lookahead detect a cluster crossing the boundary
            // without scanning an arbitrarily long combining sequence.
            var length = StringInfo.GetNextTextElementLength(value.AsSpan(end, Math.Min(value.Length - end, limit - end + 2)));
            if (length > limit - end) break;
            end += length;
        }
        // One pathological extended grapheme can exceed the entire preview.
        return end == 0 ? "…" : value[..end];
    }
    private string FilePreview()
    {
        var preview = string.Join("\n", (FilePaths ?? []).Take(5).Select(Path.GetFileName));
        return Abbreviate(preview);
    }
}

public sealed record ClipSummary(string Id, ClipKind Kind, DateTimeOffset CopiedAt,
    bool Pinned, string Preview, string Fingerprint, long StoredBytes, bool IsSnippet = false, string? Title = null, string[]? Labels = null, bool HasRichText = false);

public sealed record HistoryLimits(int MaxEntries = 500, long MaxBytes = 256L * 1024 * 1024,
    int RetentionDays = 30, int MaxEntryBytes = 24 * 1024 * 1024);

public interface IContentProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}
