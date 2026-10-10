using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Clippboard.App.Platform;

// Runs only on disposable CI desktops. Never inject input on a developer's desktop.
internal static class AppSessionChecks
{
    internal static Task<int> RunAsync(string executable, string output)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") throw new InvalidOperationException("Native session checks require an isolated GitHub Actions runner.");
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async (_, _) =>
            {
                Process? listener = null;
                var textbox = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 20 };
                var fixture = new Window { Title = "Clippboard synthetic paste destination", Width = 700, Height = 500, Left = 0, Top = 0, Content = textbox };
                var dataRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "session-data");
                try
                {
                    if (Directory.Exists(dataRoot)) throw new IOException("Session data must be fresh.");
                    Directory.CreateDirectory(dataRoot);
                    fixture.Show(); fixture.Activate(); textbox.Focus();
                    Process StartListener()
                    {
                        var launch = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true };
                        launch.ArgumentList.Add("--dev"); launch.ArgumentList.Add("--background");
                        launch.Environment["CLIPPBOARD_DEV_DATA"] = dataRoot;
                        return Process.Start(launch) ?? throw new IOException("Native listener did not start.");
                    }
                    listener = StartListener();
                    await Until(() => FindWindow(listener.Id, "Clippboard listener", visible: false) != 0, "listener startup");
                    var fixtureHandle = new WindowInteropHelper(fixture).Handle;
                    var watch = Stopwatch.StartNew();
                    for (var cycle = 0; cycle < 3; cycle++)
                    {
                        var expected = $"Clippboard session {cycle + 1} — Unicode 🧵\nPakistan • synthetic fixture";
                        Clipboard.SetText(expected);
                        await Until(() => Directory.GetFiles(Path.Combine(dataRoot, "history"), "*.meta").Length == cycle + 1, "real clipboard capture");
                        textbox.Clear(); fixture.Activate(); textbox.Focus(); NativeMethods.SetForegroundWindow(fixtureHandle);
                        if (NativeMethods.GetForegroundWindow() != fixtureHandle) throw new IOException("Fixture cannot acquire foreground; paste acceptance is untested.");
                        NativeMethods.AllowSetForegroundWindow((uint)listener.Id);
                        // Exercise the actual registered Windows hotkey, rather than calling ToggleRope.
                        InputChord();
                        nint overlay = 0;
                        await Until(() => (overlay = FindWindow(0, "Clippboard", visible: true)) != 0, "real overlay startup");
                        await Task.Delay(1500);
                        await Task.Run(() =>
                        {
                            var surface = AutomationElement.FromHandle(overlay);
                            var note = surface.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Restore Text: " + expected));
                            if (note is null) throw new IOException("Captured note is missing from the real overlay.");
                            ((InvokePattern)note.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        }).WaitAsync(TimeSpan.FromSeconds(10));
                        await Until(() => textbox.Text.Replace("\r\n", "\n") == expected, "native restore, focus return and paste acceptance");
                        await Until(() => !NativeMethods.IsWindow(overlay), "overlay destruction");
                        if (Directory.GetFiles(Path.Combine(dataRoot, "history"), "*.clip").Length != cycle + 1) throw new IOException("Own restore created a duplicate capture.");
                    }
                    listener.Kill(entireProcessTree: true); await listener.WaitForExitAsync(); listener.Dispose();
                    listener = StartListener();
                    await Until(() => FindWindow(listener.Id, "Clippboard listener", visible: false) != 0, "listener restart");
                    if (Directory.GetFiles(Path.Combine(dataRoot, "history"), "*.clip").Length != 3) throw new IOException("History did not persist through restart.");
                    var cpu = listener.TotalProcessorTime; var idle = Stopwatch.StartNew();
                    await Task.Delay(30000); listener.Refresh();
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Scenario = "Native self-contained runtime: three Unicode clipboard capture/hotkey/overlay/restore/paste cycles into a controlled WPF destination, restart, 30-second idle", Cycles = 3, Seconds = watch.Elapsed.TotalSeconds, WorkingSetBytes = listener.WorkingSet64, PrivateBytes = listener.PrivateMemorySize64, IdleCpuPercentOneCore = (listener.TotalProcessorTime - cpu).TotalSeconds / idle.Elapsed.TotalSeconds * 100, Limitations = "Does not certify Notepad, Paint, Explorer, browsers, elevated apps or Store-installed runtime" }, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine("PASS: native clipboard capture, hotkey, overlay, restore, foreground paste acceptance, repeat close and restart persistence.");
                    result.TrySetResult(0);
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); result.TrySetResult(1); }
                finally
                {
                    if (listener is not null) { if (!listener.HasExited) listener.Kill(entireProcessTree: true); listener.Dispose(); }
                    fixture.Close(); app.Shutdown();
                }
            };
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
    private static async Task Until(Func<bool> condition, string stage)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new IOException("Session deadline: " + stage); await Task.Delay(100); }
    }
    private static void InputChord()
    {
        NativeMethods.Input Key(ushort key, uint flags) => new() { Type = 1, Data = new NativeMethods.InputUnion { Keyboard = new NativeMethods.KeyboardInput { Key = key, Flags = flags } } };
        NativeMethods.Input[] inputs = [Key(0x11,0),Key(0x10,0),Key(0x56,0),Key(0x56,2),Key(0x10,2),Key(0x11,2)];
        if (NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>()) != inputs.Length) throw new IOException("Windows rejected the fixture hotkey.");
    }
    private static nint FindWindow(int process, string title, bool visible)
    {
        nint found = 0;
        EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var pid);
            var text = new StringBuilder(256); GetWindowText(window, text, text.Capacity);
            if ((process == 0 || pid == process) && text.ToString() == title && (!visible || IsWindowVisible(window))) { found = window; return false; }
            return true;
        }, 0);
        return found;
    }
    private delegate bool EnumWindowProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
