using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Clippboard.App.Services;

// A child owns every WPF surface. Exiting it frees native renderer pools as well as managed previews.
internal sealed class OverlayProcess : IDisposable
{
    private readonly OverlayServer _server;
    internal Process Process { get; }
    internal Task ServerTask { get; }
    internal OverlayProcess(Func<OverlayRequest, Task<OverlayReply>> handler, bool software = false)
    {
        _server = new(handler); ServerTask = _server.RunAsync();
        var launch = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) launch.ArgumentList.Add(typeof(OverlayProcess).Assembly.Location);
        launch.ArgumentList.Add("--overlay"); launch.ArgumentList.Add(_server.Name);
        if (software) launch.ArgumentList.Add("--software");
        try { Process = Process.Start(launch) ?? throw new IOException("Could not start the rope."); }
        catch { _server.Dispose(); throw; }
    }
    public void Dispose()
    {
        _server.Dispose();
        // Only terminate the exact child created by this session, never another user's app instance.
        if (!Process.HasExited) Process.Kill();
        Process.Dispose();
    }
}
