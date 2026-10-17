using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.App.Services;

// A child owns every WPF surface. Exiting it frees native renderer pools as well as managed previews.
internal sealed class OverlayProcess : IDisposable
{
    private readonly OverlayServer _server;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _activation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal const int VisibilityMessage = 0x8000 + 0x451;
    private nint _surface;
    private bool _disposed;
    internal bool OpenRequested { get; private set; }
    internal bool ControlsPrepared { get; set; }
    internal bool OwnsForeground
    {
        get
        {
            Platform.NativeMethods.GetWindowThreadProcessId(Platform.NativeMethods.GetForegroundWindow(), out var owner);
            return !_disposed && owner == (uint)Process.Id;
        }
    }
    internal void AllowActivation()
    {
        if (_disposed) return;
        Platform.NativeMethods.AllowSetForegroundWindow((uint)Process.Id);
        // The child activates its own window after the posted visibility signal.
        // A cross-process SetForegroundWindow here can synchronously wait on
        // the visual thread while that thread is awaiting the listener's IPC.
    }
    internal Process Process { get; }
    internal Task ServerTask { get; }
    internal CancellationToken Cancellation => _lifetime.Token;
    internal OverlayProcess(Func<OverlayRequest, Task<OverlayReply>> handler, bool software = false, bool standby = false, bool runtimeOnlyPreparation = false)
    {
        _server = new(handler); ServerTask = _server.RunAsync();
        var launch = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) launch.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Clippboard.dll"));
        launch.ArgumentList.Add("--overlay"); launch.ArgumentList.Add(_server.Name);
        if (standby) launch.ArgumentList.Add("--standby");
        if (runtimeOnlyPreparation) launch.ArgumentList.Add("--runtime-only-preparation");
        if (software) launch.ArgumentList.Add("--software");
        try { Process = Process.Start(launch) ?? throw new IOException("Could not start the rope."); }
        catch { _server.Dispose(); _lifetime.Dispose(); throw; }
        if (!standby) RequestOpen();
    }
    internal bool SetSurface(nint surface)
    {
        Platform.NativeMethods.GetWindowThreadProcessId(surface, out var owner);
        if (_disposed || surface == 0 || owner != (uint)Process.Id) return false;
        _surface = surface; return true;
    }
    internal void NotifyVisibility(bool close, long revision)
    {
        if (!_disposed && _surface != 0)
        {
            // A native handle can be recycled before the child exit event.
            Platform.NativeMethods.GetWindowThreadProcessId(_surface, out var owner);
            if (owner == (uint)Process.Id) Platform.NativeMethods.PostMessage(_surface, VisibilityMessage, (nint)revision, close ? 1 : 0);
        }
        // The regular state reply is the fallback if posting fails or the
        // native surface has not been created yet.
    }
    internal void RequestOpen() { OpenRequested = true; _activation.TrySetResult(); }
    internal async Task WaitForOpenAsync()
    {
        // Long-poll only before any window is shown, so it cannot hold up active
        // preview/paste requests. Quiet idle processes need no polling timer.
        try { await _activation.Task.WaitAsync(TimeSpan.FromSeconds(20), _lifetime.Token); }
        catch (TimeoutException) { }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel(); _server.Dispose();
        // Only terminate the exact child created by this session, never another user's app instance.
        if (!Process.HasExited) Process.Kill();
        Process.Dispose();
        _lifetime.Dispose();
    }
}
