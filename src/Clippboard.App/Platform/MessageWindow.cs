using System;
using System.Windows.Forms;

namespace Clippboard.App.Platform;

// The tray listener needs messages, not a WPF rendering surface. HWND_MESSAGE
// also keeps this window out of enumeration, activation and the task switcher.
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private readonly Func<int, nint, nint, bool> _receive;
    internal MessageWindow(Func<int, nint, nint, bool> receive)
    {
        _receive = receive;
        CreateHandle(new CreateParams { Caption = "Clippboard listener", Parent = new nint(-3) });
    }
    protected override void WndProc(ref Message message)
    {
        if (_receive(message.Msg, message.WParam, message.LParam)) message.Result = 0;
        else base.WndProc(ref message);
    }
    public void Dispose() { if (Handle != 0) DestroyHandle(); }
}
