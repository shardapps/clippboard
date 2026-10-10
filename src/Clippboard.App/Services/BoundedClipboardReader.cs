using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Clippboard.Core;

namespace Clippboard.App.Services;

internal sealed class BoundedClipboardReader(Func<AppSettings, Clip?> read, TimeSpan? timeout = null) : IDisposable
{
    private readonly object _gate = new();
    private Task<Clip?>? _pending;
    private bool _disposed;
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(3);
    internal bool HasOutstandingRead { get { lock (_gate) return _pending is not null; } }
    internal async Task<Clip?> ReadAsync(AppSettings settings)
    {
        var completion = new TaskCompletionSource<Clip?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = 0;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending is not null) throw new IOException("A clipboard owner is still responding. Copy again once it responds.");
            _pending = completion.Task;
        }
        var worker = new Thread(() =>
        {
            try
            {
                var clip = read(settings);
                bool discard; lock (_gate) discard = _disposed;
                if (discard || Volatile.Read(ref abandoned) != 0) { if (clip?.ImagePng is byte[] png) CryptographicOperations.ZeroMemory(png); clip = null; }
                completion.TrySetResult(clip);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { lock (_gate) if (ReferenceEquals(_pending, completion.Task)) _pending = null; }
        }) { IsBackground = true, Name = "Clippboard bounded STA reader" };
        worker.SetApartmentState(ApartmentState.STA);
        try { worker.Start(); }
        catch { lock (_gate) _pending = null; throw; }
        try { return await completion.Task.WaitAsync(_timeout); }
        catch (TimeoutException)
        {
            Interlocked.Exchange(ref abandoned, 1);
            if (completion.Task.IsCompletedSuccessfully && completion.Task.Result?.ImagePng is byte[] png) CryptographicOperations.ZeroMemory(png);
            // Observe late failures. A stuck COM call cannot be aborted safely; never spawn a replacement until it returns.
            _ = completion.Task.ContinueWith(task => { if (task.IsCompletedSuccessfully && task.Result?.ImagePng is byte[] image) CryptographicOperations.ZeroMemory(image); _ = task.Exception; }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new IOException("The clipboard owner took too long to respond. Copy that item again.");
        }
    }
    public void Dispose() { lock (_gate) _disposed = true; }
}
