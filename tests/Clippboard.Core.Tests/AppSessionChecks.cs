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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Security.Cryptography;
using Clippboard.Core;
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
                    Task? listenerReady = null;
                    Process StartListener()
                    {
                        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        listenerReady = ready.Task;
                        var launch = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                        launch.ArgumentList.Add("--dev"); launch.ArgumentList.Add("--background");
                        launch.Environment["CLIPPBOARD_DEV_DATA"] = dataRoot;
                        launch.Environment["CLIPPBOARD_SESSION_DIAGNOSTICS"] = "1";
                        var process = Process.Start(launch) ?? throw new IOException("Native listener did not start.");
                        process.ErrorDataReceived += (_, line) =>
                        {
                            if (line.Data is null) return;
                            Console.Error.WriteLine("Listener: " + line.Data);
                            if (line.Data == "Clipboard listener ready") ready.TrySetResult();
                        };
                        process.BeginErrorReadLine();
                        return process;
                    }
                    listener = StartListener();
                    await Until(() => FindOwnedMessageWindow(listener.Id, "Clippboard listener") != 0, "listener startup");
                    await listenerReady!.WaitAsync(TimeSpan.FromSeconds(15));
                    await Task.Run(() => listener.WaitForInputIdle(5000));
                    var fixtureHandle = new WindowInteropHelper(fixture).Handle;
                    var historyDirectory = Path.Combine(dataRoot, "history");
                    var watch = Stopwatch.StartNew(); var visibleLatencies = new List<double>();
                    for (var cycle = 0; cycle < 3; cycle++)
                    {
                        var expected = $"Clippboard session {cycle + 1} — Unicode 🧵\nPakistan • synthetic fixture";
                        var formattedHtml = cycle == 0 ? Program.HtmlFixture("<pre>" + System.Net.WebUtility.HtmlEncode(expected) + "</pre>") : null;
                        if (formattedHtml is not null)
                        {
                            var formatted = new DataObject(); formatted.SetText(expected, TextDataFormat.UnicodeText);
                            formatted.SetText(formattedHtml, TextDataFormat.Html); Clipboard.SetDataObject(formatted, true);
                        }
                        else Clipboard.SetText(expected);
                        Console.WriteLine($"Fixture sequence {NativeMethods.GetClipboardSequenceNumber()}, Unicode format {Clipboard.ContainsText(TextDataFormat.UnicodeText)}");
                        await Until(() => Directory.GetFiles(Path.Combine(dataRoot, "history"), "*.meta").Length == cycle + 1, "real clipboard capture");
                        textbox.Clear(); fixture.Activate(); textbox.Focus(); NativeMethods.SetForegroundWindow(fixtureHandle);
                        if (NativeMethods.GetForegroundWindow() != fixtureHandle) throw new IOException("Fixture cannot acquire foreground; paste acceptance is untested.");
                        NativeMethods.AllowSetForegroundWindow((uint)listener.Id);
                        // Exercise the actual registered Windows hotkey, rather than calling ToggleRope.
                        var invocation = Stopwatch.StartNew(); InputChord();
                        nint overlay = 0;
                        await Until(() => (overlay = FindWindow(0, "Clippboard", visible: true)) != 0, "real overlay startup");
                        visibleLatencies.Add(invocation.Elapsed.TotalMilliseconds);
                        await Task.Delay(1500);
                        if (cycle == 0)
                        {
                            for (var reversal = 0; reversal < 3; reversal++)
                            {
                                InputChord(); await Task.Delay(120); InputChord(); await Task.Delay(180);
                            }
                            await Until(() => FindWindow(0, "Clippboard", visible: true) != 0, "rope visible after rapid shortcut reversals");
                            overlay = FindWindow(0, "Clippboard", visible: true); await Task.Delay(900);
                        }
                        await Task.Run(() =>
                        {
                            var surface = AutomationElement.FromHandle(overlay);
                            var note = surface.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Restore Text: " + expected));
                            if (note is null) throw new IOException("Captured note is missing from the real overlay.");
                            ((InvokePattern)note.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        }).WaitAsync(TimeSpan.FromSeconds(10));
                        await Until(() => textbox.Text.Replace("\r\n", "\n") == expected, "native restore, focus return and paste acceptance");
                        if (formattedHtml is not null && (Clipboard.GetDataObject() is not { } restoredFormats
                            || Clippboard.App.Services.FormattedClipboard.Read(restoredFormats, DataFormats.Html) != formattedHtml))
                            throw new IOException("Native restore did not preserve the original HTML alongside Unicode fallback.");
                        await Until(() => !NativeMethods.IsWindow(overlay), "overlay destruction");
                        if (FixturePayloads(Path.Combine(dataRoot, "history")).Count() != cycle + 1) throw new IOException("Own restore created a duplicate capture.");
                    }
                    // Fast changes are coalesced by Windows/the reader; require
                    // the final item, not an invented guarantee of every transient.
                    for (var i = 0; i < 100; i++) { var text = $"Synthetic burst {i} — 🧵"; await SetClipboardAsync(() => Clipboard.SetText(text)); await Task.Delay(10); }
                    var finalText = "Final synthetic burst item — 🧵";
                    await SetClipboardAsync(() => Clipboard.SetText(finalText));
                    await Until(() => ReadFixtureSummaries(historyDirectory).Any(x => x.Preview == finalText), "final rapid-copy item persisted");
                    for (var image = 0; image < 2; image++)
                    {
                        var pixels = new byte[2048 * 2048 * 4];
                        for (var p = 0; p < pixels.Length; p += 4) { pixels[p] = (byte)(p / 4 % 251); pixels[p + 1] = (byte)(image * 90); pixels[p + 2] = (byte)(p / 8192 % 251); pixels[p + 3] = 255; }
                        var bitmap = BitmapSource.Create(2048, 2048, 96, 96, PixelFormats.Bgra32, null, pixels, 8192); bitmap.Freeze();
                        await SetClipboardAsync(() => Clipboard.SetImage(bitmap));
                        var count = image + 1;
                        await Until(() => ReadFixtureSummaries(historyDirectory).Count(x => x.Kind == ClipKind.Image) == count, "large native image persisted");
                    }
                    var originalFile = Path.Combine(dataRoot, "synthetic-文件.txt"); await File.WriteAllTextAsync(originalFile, "Synthetic original");
                    await SetClipboardAsync(() => Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { originalFile }));
                    await Until(() => ReadFixtureSummaries(historyDirectory).Any(x => x.Kind == ClipKind.Files), "native file reference persisted");
                    // Release the OS clipboard image before sampling idle capture.
                    await SetClipboardAsync(() => Clipboard.SetText(finalText)); await Task.Delay(2000);
                    var summaries = ReadFixtureSummaries(historyDirectory);
                    var persisted = summaries.Count;
                    var diskBytes = FixturePayloads(historyDirectory).Sum(path => new FileInfo(path).Length);
                    if (persisted > 500 || diskBytes > 256L * 1024 * 1024 || summaries.Count(x => x.Kind == ClipKind.Image) != 2) throw new IOException("Native burst violated history bounds or lost an image.");
                    var samples = new List<object>(); var cpu = listener.TotalProcessorTime; var idle = Stopwatch.StartNew();
                    for (var sample = 0; sample < 30; sample++)
                    {
                        await Task.Delay(10000);
                        if (listener.HasExited) throw new IOException("Listener crashed during the five-minute soak.");
                        listener.Refresh(); samples.Add(new { Seconds = idle.Elapsed.TotalSeconds, WorkingSetBytes = listener.WorkingSet64, PrivateBytes = listener.PrivateMemorySize64, Handles = listener.HandleCount, CombinedRuntime = SampleRuntime(executable) });
                    }
                    var idleCpu = (listener.TotalProcessorTime - cpu).TotalSeconds / idle.Elapsed.TotalSeconds * 100;
                    var warmWorkingSet = listener.WorkingSet64; var warmPrivate = listener.PrivateMemorySize64;
                    listener.Kill(entireProcessTree: true); await listener.WaitForExitAsync(); listener.Dispose();
                    listener = StartListener();
                    await Until(() => FindOwnedMessageWindow(listener.Id, "Clippboard listener") != 0, "listener restart");
                    await listenerReady!.WaitAsync(TimeSpan.FromSeconds(15));
                    if (FixturePayloads(historyDirectory).Count() != persisted) throw new IOException("History did not persist through restart.");
                    await Task.Delay(10000); listener.Refresh();
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { Scenario = "Native self-contained runtime: three Unicode capture/hotkey/overlay/restore/paste cycles; 100-copy burst and final-item persistence; two 2048x2048 images; file reference; five-minute warm idle and restart", Cycles = 3, RapidShortcutReversals = 3, HotkeyToVisibleWindowMs = visibleLatencies, Seconds = watch.Elapsed.TotalSeconds, PersistedEntries = persisted, HistoryBytes = diskBytes, WarmWorkingSetBytes = warmWorkingSet, WarmPrivateBytes = warmPrivate, IdleSamples = samples, WorkingSetBytes = listener.WorkingSet64, PrivateBytes = listener.PrivateMemorySize64, IdleCpuPercentOneCore = idleCpu, Limitations = "Isolated Windows runner. Visible-window timings are not physical frame presentation. Combined runtime includes matching test executable processes; working sets include shared mapped pages. Does not certify physical trackpads, Notepad, Paint, Explorer, browsers, elevated apps, other hardware/OS or Store-installed runtime" }, new JsonSerializerOptions { WriteIndented = true }));
                    Console.WriteLine("PASS: native capture/paste, bursts, large images, file reference, bounded history, five-minute idle without crash, and restart persistence.");
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
    private sealed record RuntimeMemory(long WorkingSetBytes, long PrivateBytes, int Processes);
    private static RuntimeMemory SampleRuntime(string executable)
    {
        long working = 0, committed = 0; var count = 0;
        // Only the exact isolated executable, never the owner's installed app.
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        using (process)
        {
            try
            {
                if (!string.Equals(process.MainModule?.FileName, Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) continue;
                process.Refresh(); working += process.WorkingSet64; committed += process.PrivateMemorySize64; count++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return new(working, committed, count);
    }
    private static async Task Until(Func<bool> condition, string stage)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new IOException("Session deadline: " + stage); await Task.Delay(100); }
    }
    private static async Task SetClipboardAsync(Action set)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try { set(); return; }
            catch (ExternalException) when (deadline.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(30); }
        }
    }
    private static IEnumerable<string> FixturePayloads(string directory) => Directory.EnumerateFiles(directory)
        .Where(path => Path.GetExtension(path) is ".clip" or ".clip2");
    private static IReadOnlyList<ClipSummary> ReadFixtureSummaries(string directory)
    {
        var results = new List<ClipSummary>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.meta"))
        {
            byte[]? plaintext = null;
            try
            {
                plaintext = new WindowsProtector().Unprotect(File.ReadAllBytes(path));
                using var json = JsonDocument.Parse(plaintext);
                if (json.RootElement.GetProperty("Summary").Deserialize<ClipSummary>() is { } summary) results.Add(summary);
            }
            catch (IOException) { /* Atomic metadata replacement can race this read-only fixture snapshot. */ }
            finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
        }
        return results;
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
    internal static nint FindOwnedMessageWindow(int process, string title)
    {
        nint window = 0;
        while ((window = FindWindowEx(new nint(-3), window, null, title)) != 0)
        {
            NativeMethods.GetWindowThreadProcessId(window, out var pid);
            if (pid == process) return window;
        }
        return 0;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? className, string title);
    private delegate bool EnumWindowProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
