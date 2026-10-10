using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clippboard.App.Platform;
using Clippboard.App.Services;
using Clippboard.App.Views;
using Clippboard.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Clippboard.App;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsMutex;
    private ClipboardService? _clipboard;
    private HistoryStore? _store;
    private RopeWindow? _rope;
    private Forms.NotifyIcon? _tray;
    private Icon? _trayIcon;
    private DispatcherTimer? _maintenance;
    private ForegroundTracker? _foreground;
    private nint _target;
    private bool _selecting;
    private bool _toggling;
    private bool _development;
    private AppSettings _settings = new();
    private SettingsStore? _settingsStore;
    private bool _savingSettings;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var lifecycleIndex = Array.IndexOf(e.Args, "--lifecycle-diagnostics");
            if (lifecycleIndex >= 0) { if (lifecycleIndex + 1 >= e.Args.Length) throw new ArgumentException("Diagnostic output required."); await OverlayDiagnostics.RunAsync(e.Args[lifecycleIndex + 1], e.Args.Contains("--software")); Shutdown(); return; }
            var overlayIndex = Array.IndexOf(e.Args, "--overlay");
            if (overlayIndex >= 0) { if (overlayIndex + 1 >= e.Args.Length) throw new ArgumentException("Overlay session required."); await RunOverlayAsync(e.Args[overlayIndex + 1], e.Args.Contains("--software")); return; }
            _development = e.Args.Contains("--dev");
            var preview = Array.IndexOf(e.Args, "--preview");
            var diagnostic = Array.IndexOf(e.Args, "--diagnostics");
            var root = _development || preview >= 0 || diagnostic >= 0
                ? Environment.GetEnvironmentVariable("CLIPPBOARD_DEV_DATA") ?? Path.Combine(AppContext.BaseDirectory, ".data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShardApps", "Clippboard");
            _store = new HistoryStore(Path.Combine(root, "history"), new WindowsProtector());
            if (diagnostic >= 0)
            {
                if (diagnostic + 1 >= e.Args.Length) throw new ArgumentException("Diagnostic report path required.");
                await PerformanceDiagnostics.RunAsync(_store, e.Args[diagnostic + 1], e.Args.Contains("--software"));
                Shutdown(); return;
            }
            if (preview >= 0)
            {
                await PreviewAsync(preview + 1 < e.Args.Length ? e.Args[preview + 1] : throw new ArgumentException("Preview output path required."), e.Args);
                Shutdown(); return;
            }
            _instance = new Mutex(true, @"Local\ShardApps.Clippboard." + Environment.UserName + (_development ? ".dev" : ""), out _ownsMutex);
            if (!_ownsMutex) { MessageBox.Show("Clippboard is already running. Use Ctrl + Shift + V or its tray icon.", "Clippboard"); Shutdown(); return; }
            _settingsStore = new SettingsStore(Path.Combine(root, "settings.json"));
            _settings = await _settingsStore.LoadAsync();
            if (!_development) _settings = _settings with { StartAtLogin = StartupEnabled() };
            _store = new HistoryStore(Path.Combine(root, "history"), new WindowsProtector(), _settings.HistoryLimits());
            await Task.Run(() => _store.InitializeAsync());

            _clipboard = new ClipboardService(SaveAsync, _settings.Shortcut) { Settings = _settings, Paused = _settings.CapturePaused };

            _clipboard.ToggleRequested += ToggleRope;
            _clipboard.Notice += Notify;
            _foreground = new ForegroundTracker();
            CreateTray();
            _maintenance = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            _maintenance.Tick += async (_, _) =>
            {
                try { await Task.Run(() => _store.PruneAsync()); ++_historyVersion; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("History cleanup failed. Check available disk space and permissions."); }
            };
            _maintenance.Start();
            if (!_clipboard.HotkeyAvailable) Notify(SettingsView.ShortcutLabel(_settings.Shortcut) + " is used by another app. Open Clippboard from its tray icon.");
            if (_store.UnreadableEntries > 0) Notify($"Removed {_store.UnreadableEntries} damaged or unreadable history entries.");
            if (!e.Args.Contains("--background")) ToggleRope();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            if (!e.Args.Contains("--overlay") && !e.Args.Contains("--lifecycle-diagnostics"))
                MessageBox.Show("Clippboard could not start. Check storage permissions and whether another copy is running.", "Clippboard", MessageBoxButton.OK, MessageBoxImage.Error);
            else Console.Error.WriteLine("Clippboard process could not start: " + ex.GetType().Name);
            Shutdown(1);
        }
    }

    private async Task SaveAsync(Clip clip)
    {
        try
        {
            if (!_settings.Captures(clip.Kind)) return;
            if (!await Task.Run(() => _store!.AddAsync(clip))) Notify("History is full of pinned notes, or this item exceeds the storage limit. Unpin or delete some notes.");
            else ++_historyVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { Notify("Could not save this clip. Check disk space and storage permissions."); }
        finally { if (clip.ImagePng is byte[] png) System.Security.Cryptography.CryptographicOperations.ZeroMemory(png); }
    }

    private void ToggleRope()
    {
        if (_toggling || _selecting || _store is null) return;
        if (_overlay is not null && !_overlay.Process.HasExited) { _closeOverlay = true; return; }
        _toggling = true;
        try
        {
            _overlay?.Dispose(); _closeOverlay = false; _restoredSequence = 0;
            _target = _foreground?.LastWindow ?? NativeMethods.GetForegroundWindow();
            var session = new OverlayProcess(request => Dispatcher.InvokeAsync(() => HandleOverlayAsync(request)).Task.Unwrap());
            _overlay = session; _foreground!.OverlayProcessId = (uint)session.Process.Id;
            NativeMethods.AllowSetForegroundWindow((uint)session.Process.Id);
            session.Process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_overlay, session)) return;
                var failed = session.Process.ExitCode != 0; _overlay = null; _foreground.OverlayProcessId = 0; session.Dispose();
                if (failed) Notify("The rope closed unexpectedly. Your saved history is safe; open it again.");
            }));
            session.Process.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { Notify("Could not open history. Try again."); }
        finally { _toggling = false; }
    }
    private async void TogglePause()
    {
        if (_clipboard is null) return;
        var error = await SaveSettingsAsync(_settings with { CapturePaused = !_settings.CapturePaused });
        Notify(error ?? (_settings.CapturePaused ? "Capture paused. New copies won't be saved." : "Capture resumed."));
    }

    private async Task<string?> SaveSettingsAsync(AppSettings settings)
    {
        if (_savingSettings || _clipboard is null) return "A settings change is already in progress. Try again.";
        _savingSettings = true;
        var previous = _settings; var next = settings.Normalize(); var persisted = false;
        _clipboard.Paused = true;
        try
        {
            var entries = await Task.Run(() => _store!.ListAsync());
            var cutoff = DateTimeOffset.UtcNow.AddDays(-next.RetentionDays);
            var pins = entries.Where(x => x.Pinned && x.CopiedAt >= cutoff).ToArray();
            if (pins.Length > next.MaxEntries || pins.Sum(x => x.StoredBytes) > next.HistoryLimits().MaxBytes)
                return "Starred notes exceed that budget. Unstar or delete some, or increase the limit.";
            if (next.Shortcut != previous.Shortcut && !_clipboard.ChangeShortcut(next.Shortcut))
                return "That shortcut is in use. Your current shortcut is unchanged.";
            if (!_development && next.StartAtLogin != previous.StartAtLogin) SetStartup(next.StartAtLogin);
            await _settingsStore!.SaveAsync(next); persisted = true;
            if (!await Task.Run(() => _store!.UpdateLimitsAsync(next.HistoryLimits())))
            {
                await _settingsStore.SaveAsync(previous); persisted = false;
                return "Starred notes changed while saving. Increase the budget and try again.";
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Security.Cryptography.CryptographicException)
        { return persisted ? "Settings saved, but history cleanup could not finish. Check storage permissions." : "Could not save settings. Check storage permissions and try again."; }
        finally
        {
            if (persisted) _settings = next;
            else
            {
                if (next.Shortcut != previous.Shortcut) _clipboard.ChangeShortcut(previous.Shortcut);
                if (!_development && next.StartAtLogin != previous.StartAtLogin)
                    try { SetStartup(previous.StartAtLogin); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Notify("Check your Windows sign-in startup preference."); }
            }
            _clipboard.Settings = _settings; _clipboard.Paused = _settings.CapturePaused;
            ++_historyVersion;
            if (_tray is not null)
            {
                _tray.Text = _settings.CapturePaused ? "Clippboard · Capture paused" : "Clippboard · " + SettingsView.ShortcutLabel(_settings.Shortcut);
                _tray.ContextMenuStrip!.Items[0].Text = "Open rope · " + SettingsView.ShortcutLabel(_settings.Shortcut);
                if (!_development && _tray.ContextMenuStrip.Items[2] is Forms.ToolStripMenuItem startup) startup.Checked = _settings.StartAtLogin;
            }
            _savingSettings = false;
        }
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open rope · " + SettingsView.ShortcutLabel(_settings.Shortcut), null, (_, _) => ToggleRope());
        menu.Items.Add("Pause / resume capture", null, (_, _) => TogglePause());
        if (!_development)
        {
            var startup = new Forms.ToolStripMenuItem("Start at sign-in") { Checked = StartupEnabled() };
            startup.Click += async (_, _) =>
            {
                var error = await SaveSettingsAsync(_settings with { StartAtLogin = !startup.Checked });
                if (error is not null) Notify(error);
            };
            menu.Items.Add(startup);
        }
        menu.Items.Add("Help & feedback", null, (_, _) => { if (!Services.Feedback.Open("discuss")) Notify("Could not open your browser. Visit github.com/shardapps/clippboard/discussions."); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Clippboard", null, (_, _) => Shutdown());
        _trayIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "Clippboard · " + SettingsView.ShortcutLabel(_settings.Shortcut), ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleRope(); };
    }

    private static bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("Clippboard") is string;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("Clippboard", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("Clippboard", false);
    }

    private void Notify(string message)
    {
        _rope?.SetStatus(message);
        _tray?.ShowBalloonTip(4500, "Clippboard", message, Forms.ToolTipIcon.Info);
    }

    private async Task PreviewAsync(string output, string[] args)
    {
        var themeIndex = Array.IndexOf(args, "--theme");
        var theme = themeIndex >= 0 && themeIndex + 1 < args.Length && Enum.TryParse<AppTheme>(args[themeIndex + 1], out var parsed) ? parsed : AppTheme.Cloud;
        _rope = new RopeWindow(_store!, preview: true, settings: new AppSettings { Theme = theme, Animate = false }) { Width = 1600, Height = 900, ShowInTaskbar = false };
        var measureIndex = Array.IndexOf(args, "--measure");
        _rope.Show(); _rope.RenderDemo(measureIndex >= 0 ? 1000 : 8);
        if (args.Contains("--settings")) _rope.OpenSettings();
        if (args.Contains("--detail"))
        {
            var clip = new Clip { Kind = ClipKind.Text, Text = "A little more room to think.\n\n" + string.Join("\n\n", Enumerable.Repeat("Open the whole note. Read at your own pace. Resize it, select a passage, then restore it when you need it.", 12)) };
            await _rope.PreviewDetailAsync(clip);
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        _rope.UpdateLayout();
        SavePreview(output);
        if (measureIndex >= 0 && measureIndex + 1 < args.Length)
        {
            using var process = Process.GetCurrentProcess(); process.Refresh();
            var shownBytes = process.WorkingSet64; var shownPrivate = process.PrivateMemorySize64;
            var rendered = _rope.RenderedNoteCount;
            await _rope.CloseRopeAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(2000);
            process.Refresh(); var hiddenBytes = process.WorkingSet64; var hiddenPrivate = process.PrivateMemorySize64;
            var initialCpu = process.TotalProcessorTime; var watch = Stopwatch.StartNew();
            await Task.Delay(5000); process.Refresh();
            var cpuPercent = (process.TotalProcessorTime - initialCpu).TotalSeconds / watch.Elapsed.TotalSeconds * 100;
            var report = new { Scenario = "Synthetic 1000 metadata notes; no clipboard listener; forced GC after hide", Runtime = Environment.Version.ToString(),
                RenderedNotes = rendered, ShownWorkingSetBytes = shownBytes, ShownPrivateBytes = shownPrivate, HiddenWorkingSetBytes = hiddenBytes,
                HiddenPrivateBytes = hiddenPrivate, HiddenCachedImageBytes = _rope.CachedImageBytes, HiddenRenderedNotes = _rope.RenderedNoteCount,
                HiddenManagedBytes = GC.GetTotalMemory(false), IdleCpuPercentOneCore = Math.Round(cpuPercent, 3), IdleSampleSeconds = watch.Elapsed.TotalSeconds };
            var reportPath = Path.GetFullPath(args[measureIndex + 1]); Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private void SavePreview(string output)
    {
        var bitmap = new RenderTargetBitmap(1600, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(_rope!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var file = File.Create(output); encoder.Save(file);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _overlay?.Dispose(); _maintenance?.Stop(); _clipboard?.Dispose(); _foreground?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
        _trayIcon?.Dispose();
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
