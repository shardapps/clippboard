using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Clippboard.App.Services;

internal sealed class FileAvailabilityException(string message) : IOException(message) { }

// File.Exists on an offline share cannot be reliably cancelled. Bound both the
// caller's wait and outstanding workers, rather than spawning another for each retry.
internal sealed class FileAvailabilityProbe
{
    private readonly object _gate = new();
    private readonly Func<string[], bool> _check;
    private readonly TimeSpan _timeout;
    private Task<bool>? _running;
    internal FileAvailabilityProbe(Func<string[], bool>? check = null, TimeSpan? timeout = null)
    {
        _check = check ?? (paths => paths.All(path => File.Exists(path) || Directory.Exists(path)));
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }
    internal async Task<bool> CheckAsync(string[] paths)
    {
        if (paths.Length == 0 || paths.Length > 100 || paths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 32767)) return false;
        Task<bool> work;
        lock (_gate)
        {
            if (_running is { IsCompleted: false }) throw new FileAvailabilityException("A previous file location check is still waiting. Try again later; text and image notes remain available.");
            var snapshot = (string[])paths.Clone();
            work = _running = Task.Run(() => _check(snapshot));
            _ = work.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        try { return await work.WaitAsync(_timeout).ConfigureAwait(false); }
        catch (TimeoutException) { throw new FileAvailabilityException("File locations took too long to respond. Reconnect the drive or share, then try again."); }
    }
}
