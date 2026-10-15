using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Clippboard.Core;

// Preserve bounded source formats for an explicit paste. Never interpret them
// as UI markup, load linked resources, or try to reconstruct missing plaintext.
public static class RichTextFormats
{
    public const int MaxFormatBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static bool ValidRtf(string? value) => Bounded(value) && value!.Length > 7 && value.StartsWith("{\\rtf1", StringComparison.Ordinal)
        && (char.IsWhiteSpace(value[6]) || value[6] == '\\')
        && value.TrimEnd().EndsWith('}') && !value.Contains('\0');
    private static bool Bounded(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxFormatBytes) return false;
        try { return StrictUtf8.GetByteCount(value) <= MaxFormatBytes; }
        catch (EncoderFallbackException) { return false; }
    }
    public static bool ValidHtml(string? value)
    {
        if (!Bounded(value)) return false;
        var bytes = StrictUtf8.GetBytes(value!);
        try
        {
            var position = 0; var headerEnd = 0;
            int? start = null, end = null, fragmentStart = null, fragmentEnd = null;
            var version = false;
            while (position < Math.Min(bytes.Length, 4096))
            {
                var lineStart = position;
                while (position < bytes.Length && bytes[position] is not (10 or 13)) position++;
                if (position > 4096 || position == bytes.Length) break;
                var line = Encoding.ASCII.GetString(bytes, lineStart, position - lineStart);
                while (position < bytes.Length && bytes[position] is 10 or 13) position++;
                var colon = line.IndexOf(':'); if (colon <= 0) break;
                var name = line[..colon]; var field = line[(colon + 1)..].Trim();
                if (name == "Version") { if (version || field is not ("0.9" or "1.0")) return false; version = true; }
                else if (name is "StartHTML" or "EndHTML" or "StartFragment" or "EndFragment")
                {
                    if (!int.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) return false;
                    switch (name)
                    {
                        case "StartHTML": if (start is not null) return false; start = number; break;
                        case "EndHTML": if (end is not null) return false; end = number; break;
                        case "StartFragment": if (fragmentStart is not null) return false; fragmentStart = number; break;
                        case "EndFragment": if (fragmentEnd is not null) return false; fragmentEnd = number; break;
                    }
                }
                else if (name is not ("SourceURL" or "StartSelection" or "EndSelection")) break;
                headerEnd = position;
                if (start >= 0 && position >= start) break;
            }
            if (!version || start is null || end is null || fragmentStart is null || fragmentEnd is null) return false;
            if ((start == -1) != (end == -1)) return false;
            var contextStart = start == -1 ? headerEnd : start.Value;
            var contextEnd = end == -1 ? bytes.Length : end.Value;
            if (contextStart < headerEnd || contextEnd < contextStart || contextEnd > bytes.Length
                || fragmentStart < contextStart || fragmentEnd < fragmentStart || fragmentEnd > contextEnd) return false;
            // CF_HTML offsets count UTF-8 bytes. Reject offsets inside a code point.
            foreach (var offset in new[] { contextStart, contextEnd, fragmentStart.Value, fragmentEnd.Value })
                if (offset < bytes.Length && (bytes[offset] & 0xc0) == 0x80) return false;
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static string Fingerprint(string? text, string? html, string? rtf)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var part in new[] { text, html, rtf })
        {
            var data = part is null ? [] : Encoding.UTF8.GetBytes(part);
            try { BinaryPrimitives.WriteInt32LittleEndian(length, part is null ? -1 : data.Length); hash.AppendData(length); hash.AppendData(data); }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        return "TextRich:" + Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static void Validate(Clip clip)
    {
        if ((clip.Html is not null || clip.Rtf is not null) && (clip.Kind != ClipKind.Text || clip.IsSnippet
            || string.IsNullOrEmpty(clip.Text) || clip.Text.Length > TextReuse.MaxTextLength
            || (clip.Html is not null && !ValidHtml(clip.Html)) || (clip.Rtf is not null && !ValidRtf(clip.Rtf))))
            throw new ArgumentException("Formatted text requires bounded valid source formats and a plain-text fallback.");
    }
}
