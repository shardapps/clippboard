using System;
using System.IO;
using System.Linq;
using System.Text;

namespace Clippboard.App.Services;

internal static class BundledNotices
{
    internal static string Read()
    {
        const string prefix = "Clippboard.Licenses.";
        var assembly = typeof(BundledNotices).Assembly;
        var text = new StringBuilder();
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(name => name.EndsWith("README.md", StringComparison.Ordinal) ? 0 : 1).ThenBy(name => name, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("A bundled notice is missing.");
            if (stream.Length is <= 0 or > 1024 * 1024 || text.Length > 2 * 1024 * 1024) throw new InvalidDataException("Unexpected notice size.");
            using var reader = new StreamReader(stream);
            text.AppendLine(name[prefix.Length..]).AppendLine(new string('─', 40)).AppendLine(reader.ReadToEnd()).AppendLine();
            if (text.Length > 2 * 1024 * 1024) throw new InvalidDataException("Unexpected notice size.");
        }
        if (text.Length == 0) throw new InvalidDataException("No bundled notices.");
        return text.ToString();
    }
}
