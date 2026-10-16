using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clippboard.Core;

namespace Clippboard.App.Services;

internal sealed class FileActionException(string message) : IOException(message) { }

internal static class FileActions
{
    internal static readonly DestinationProbe Revisions = new();
    internal sealed record DestinationRevision(bool Exists, long Length, long ModifiedTicks);
    internal static DestinationRevision DestinationState(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new(true, info.Length, info.LastWriteTimeUtc.Ticks) : new(false, 0, 0);
    }
    internal static string PathsAsText(string[] paths)
    {
        if (paths.Length == 0 || paths.Length > 100 || paths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 32767)
            || paths.Sum(path => (long)path.Length + Environment.NewLine.Length) > TextReuse.MaxTextLength)
            throw new FileActionException("These paths exceed the text limit. Select a smaller group from the expanded note.");
        return string.Join(Environment.NewLine, paths);
    }
    internal static void ValidateLocation(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length > 32767 || path.Any(char.IsControl)
            || Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new FileActionException("Invalid original path.");
    }
    internal static async Task SavePngAsync(string destination, byte[] png, CancellationToken cancellation = default, DestinationRevision? expected = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (png.Length is < 8 or > 16 * 1024 * 1024 || !png.AsSpan(0, 8).SequenceEqual(new byte[] {137, 80, 78, 71, 13, 10, 26, 10}))
            throw new FileActionException("This image is unavailable or exceeds the PNG export limit.");
        var target = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(target), ".png", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(target).Contains(':'))
            throw new FileActionException("Choose a filename ending in .png.");
        // Plaintext is written only for this explicit export, in the chosen
        // destination directory. Atomic replacement preserves an existing file
        // if writing fails; staging is removed on cancellation/failure.
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".clippboard-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var revision = expected ?? DestinationState(target);
            if (revision != DestinationState(target)) throw new FileActionException("The destination changed while saving. Choose the file again.");
            var replacing = revision.Exists;
            // Some virtual filesystems allow rename-overwrite even when another
            // application denies sharing. Verify exclusive content access first.
            using var destinationGuard = replacing ? new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete, 1) : null;
            if (revision != DestinationState(target)) throw new FileActionException("The destination changed while saving. Choose the file again.");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            { await output.WriteAsync(png, cancellation); await output.FlushAsync(cancellation); }
            cancellation.ThrowIfCancellationRequested();
            if (revision != DestinationState(target)) throw new FileActionException("The destination changed while saving. Choose the file again.");
            File.Move(temporary, target, replacing);
        }
        finally { File.Delete(temporary); }
    }
}
