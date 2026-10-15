using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Clippboard.Core;

public static class CapturePolicy
{
    public static string? ValidateExclusions(IReadOnlyList<string>? names)
    {
        if (names is null) return null;
        if (names.Count > 64) return "Use at most 64 excluded application names.";
        if (names.Sum(name => name?.Length ?? 0) > 1024) return "Keep the exclusion list within 1,024 characters.";
        return names.Any(name => !ValidName(name)) ? "Enter executable names such as chrome.exe, one per line. Paths and wildcards are not supported." : null;
    }
    private static bool ValidName(string? name)
    {
        var trimmed = name?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length <= 128 && trimmed is not "." and not ".."
            && ProcessName(trimmed).Length > 0
            && trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
    private static string ProcessName(string name) => name.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Trim()[..^4] : name.Trim();
    public static string[] NormalizeExclusions(IEnumerable<string>? names)
    {
        var result = new List<string>(); var total = 0;
        foreach (var name in (names ?? []).Where(ValidName).Select(ProcessName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= 64 || total + name.Length > 1024) break;
            result.Add(name); total += name.Length;
        }
        return result.ToArray();
    }
    public static bool AllowsSource(IReadOnlyList<string> excluded, string? processName)
        => excluded.Count == 0 || (!string.IsNullOrWhiteSpace(processName)
            && !excluded.Any(name => string.Equals(ProcessName(name), ProcessName(processName), StringComparison.OrdinalIgnoreCase)));
}
