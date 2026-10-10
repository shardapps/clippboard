using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Clippboard.App.Platform;

// Event-driven tracking preserves the destination when opening from the notification area.
internal sealed class ForegroundTracker : IDisposable
{
    private readonly WinEventDelegate _callback;
    private readonly nint _hook;
    public nint LastWindow { get; private set; }
    public ForegroundTracker()
    {
        _callback = (_, _, window, _, _, _, _) => Remember(window);
        _hook = SetWinEventHook(3, 3, 0, _callback, 0, 0, 2);
        Remember(NativeMethods.GetForegroundWindow());
    }
    private void Remember(nint window)
    {
        if (window == 0) return;
        var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "#32768") return;
        LastWindow = window;
    }
    public void Dispose() { if (_hook != 0) UnhookWinEvent(_hook); }
    private delegate void WinEventDelegate(nint hook, uint evt, nint window, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventDelegate callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int max);
}
