using System;
using System.IO;
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
    public byte[]? ImagePng { get; init; }
    public string[]? FilePaths { get; init; }

    public string Fingerprint()
    {
        var content = Kind switch
        {
            ClipKind.Text => Encoding.UTF8.GetBytes(Text ?? ""),
            ClipKind.Image => ImagePng ?? [],
            ClipKind.Files => Encoding.UTF8.GetBytes(string.Join('\0', FilePaths ?? [])),
            _ => throw new ArgumentOutOfRangeException()
        };
        return $"{Kind}:{Convert.ToHexString(SHA256.HashData(content))}";
    }

    public ClipSummary Summary(long storedBytes) => new(
        Id, Kind, CopiedAt, Pinned, Kind switch
        {
            ClipKind.Text => (Text ?? "")[..Math.Min(Text?.Length ?? 0, 320)],
            ClipKind.Image => "Copied image",
            ClipKind.Files => string.Join("\n", (FilePaths ?? []).Take(5).Select(Path.GetFileName)),
            _ => ""
        }, Fingerprint(), storedBytes);
}

public sealed record ClipSummary(string Id, ClipKind Kind, DateTimeOffset CopiedAt,
    bool Pinned, string Preview, string Fingerprint, long StoredBytes);

public sealed record HistoryLimits(int MaxEntries = 500, long MaxBytes = 256L * 1024 * 1024,
    int RetentionDays = 30, int MaxEntryBytes = 24 * 1024 * 1024);

public interface IContentProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}
