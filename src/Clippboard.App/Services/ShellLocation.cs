using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.App.Services;

// Shell extensions/shares can stall. One owned STA may remain outstanding;
// repeated clicks cannot accumulate blocked workers or stop the UI dispatcher.
internal sealed class ShellLocation(Func<string, bool>? select = null, TimeSpan? timeout = null)
{
    private readonly object _gate = new();
    private Task<bool>? _pending;
    internal bool HasOutstandingOperation { get { lock (_gate) return _pending is { IsCompleted: false }; } }
    internal async Task<bool> SelectAsync(string path)
    {
        FileActions.ValidateLocation(path);
        Task<bool> work;
        lock (_gate)
        {
            if (_pending is { IsCompleted: false }) throw new FileAvailabilityException("A previous Explorer request is still waiting. Try again later.");
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            work = _pending = result.Task;
            _ = work.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            var thread = new Thread(() =>
            {
                try { result.TrySetResult((select ?? SelectNative)(path)); }
                catch (Exception ex) { result.TrySetException(ex); }
            }) { IsBackground = true, Name = "Clippboard original location" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        try { return await work.WaitAsync(timeout ?? TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { throw new FileAvailabilityException("Explorer took too long to respond. Reconnect the drive/share and try again later."); }
    }
    private static bool SelectNative(string path)
    {
        if (CoInitializeEx(0, 2) < 0) return false;
        nint item = 0;
        try
        {
            if (SHParseDisplayName(path, 0, out item, 0, out _) < 0 || item == 0) return false;
            // Zero children means the absolute item itself is selected in its parent.
            return SHOpenFolderAndSelectItems(item, 0, 0, 0) >= 0;
        }
        finally { if (item != 0) Marshal.FreeCoTaskMem(item); CoUninitialize(); }
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int SHParseDisplayName(string name, nint bind, out nint item, uint attributes, out uint result);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint children, uint flags);
}
