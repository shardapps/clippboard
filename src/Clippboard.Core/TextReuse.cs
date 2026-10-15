using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Clippboard.Core;

public enum TextTransform { Original, TrimEdges, Uppercase, Lowercase, CollapseWhitespace, SortLines, RemoveDuplicateLines }

public static class TextReuse
{
    public const int MaxTextLength = 512 * 1024;
    public static string Transform(string text, TextTransform transform)
    {
        if (text.Length > MaxTextLength) throw new ArgumentException("Text exceeds the capture limit.");
        return transform switch
        {
            TextTransform.Original => text,
            TextTransform.TrimEdges => text.Trim(),
            TextTransform.Uppercase => text.ToUpperInvariant(),
            TextTransform.Lowercase => text.ToLowerInvariant(),
            TextTransform.CollapseWhitespace => Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim(),
            TextTransform.SortLines => string.Join('\n', Lines(text).OrderBy(line => line, StringComparer.InvariantCulture)),
            TextTransform.RemoveDuplicateLines => string.Join('\n', Lines(text).Distinct(StringComparer.Ordinal)),
            _ => throw new ArgumentException("Unknown transformation.")
        };
    }
    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    public static string[] Labels(string text)
    {
        var labels = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (labels.Length > 8 || labels.Any(label => label.Length > 24 || label.Any(char.IsControl)))
            throw new ArgumentException("Use up to eight comma-separated labels, each at most 24 characters.");
        return labels;
    }
    public static void ValidateSnippet(string text, string title, string[] labels)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength || title.Length > 64 || title.Any(char.IsControl)
            || labels.Length > 8 || labels.Any(label => string.IsNullOrWhiteSpace(label) || label.Length > 24 || label.Any(char.IsControl)))
            throw new ArgumentException("Use nonempty text, a title up to 64 characters and up to eight labels of 24 characters.");
    }
}
