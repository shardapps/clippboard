using System;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.App.Services;

internal sealed class DestinationProbe(Func<string, FileActions.DestinationRevision>? probe = null, TimeSpan? timeout = null)
{
    private readonly SemaphoreSlim _slot = new(1, 1);
    internal async Task<FileActions.DestinationRevision> ReadAsync(string path, CancellationToken cancellation)
    {
        if (!await _slot.WaitAsync(0, cancellation)) throw new FileActionException("A previous location check is still finishing. Try again shortly.");
        var work = Task.Run(() => (probe ?? FileActions.DestinationState)(path));
        try { return await work.WaitAsync(timeout ?? TimeSpan.FromSeconds(3), cancellation); }
        catch (TimeoutException) { throw new FileActionException("This location is taking too long to respond. Choose an available local folder."); }
        finally
        {
            if (work.IsCompleted) { _ = work.Exception; _slot.Release(); }
            else _ = work.ContinueWith(finished => { _ = finished.Exception; _slot.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
