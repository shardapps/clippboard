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
            _development = e.Args.Contains("--dev");
            var preview = Array.IndexOf(e.Args, "--preview");
            var root = _development || preview >= 0
                ? Environment.GetEnvironmentVariable("CLIPPBOARD_DEV_DATA") ?? Path.Combine(AppContext.BaseDirectory, ".data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShardApps", "Clippboard");
            _store = new HistoryStore(Path.Combine(root, "history"), new WindowsProtector());
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
            _rope = CreateRope();
            _clipboard = new ClipboardService(SaveAsync, _settings.Shortcut) { Settings = _settings, Paused = _settings.CapturePaused };
            _rope.SetPaused(_settings.CapturePaused);
            _clipboard.ToggleRequested += ToggleRope;
            _clipboard.Notice += Notify;
            _foreground = new ForegroundTracker();
            CreateTray();
            _maintenance = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            _maintenance.Tick += async (_, _) =>
            {
                try { await Task.Run(() => _store.PruneAsync()); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("History cleanup failed. Check available disk space and permissions."); }
            };
            _maintenance.Start();
            if (!_clipboard.HotkeyAvailable) Notify(SettingsView.ShortcutLabel(_settings.Shortcut) + " is used by another app. Open Clippboard from its tray icon.");
            if (_store.UnreadableEntries > 0) Notify($"Removed {_store.UnreadableEntries} damaged or unreadable history entries.");
            if (!e.Args.Contains("--background")) ToggleRope();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            MessageBox.Show("Clippboard could not start. Check storage permissions and whether another copy is running.", "Clippboard", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task SaveAsync(Clip clip)
    {
        try
        {
            if (!_settings.Captures(clip.Kind)) return;
            if (!await Task.Run(() => _store!.AddAsync(clip))) Notify("History is full of pinned notes, or this item exceeds the storage limit. Unpin or delete some notes.");
            else if (_rope!.IsVisible) await _rope.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { Notify("Could not save this clip. Check disk space and storage permissions."); }
    }

    private async void ToggleRope()
    {
        if (_rope is null || _toggling || _selecting) return;
        _toggling = true;
        try
        {
            if (_rope.IsVisible) await _rope.CloseRopeAsync();
            else
            {
                if (_rope.Retired) _rope = CreateRope();
                _target = _foreground?.LastWindow ?? NativeMethods.GetForegroundWindow();
                await _rope.OpenAsync(_target);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Notify("Could not open history. Try again."); }
        finally { _toggling = false; }
    }

    private RopeWindow CreateRope()
    {
        var window = new RopeWindow(_store!, settings: _settings) { StartupAvailable = !_development };
        window.SettingsSaveRequested += SaveSettingsAsync;
        window.Selected += SelectAsync; window.PauseRequested += TogglePause;
        return window;
    }

    private async Task SelectAsync(ClipSummary summary)
    {
        if (_selecting) return;
        _selecting = true;
        var copyOnly = !_settings.AutoPaste || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        try
        {
            var clip = await Task.Run(() => _store!.GetAsync(summary.Id));
            if (clip is null) return;
            await _clipboard!.RestoreAsync(clip);
            await _rope!.CloseRopeAsync();
            if (copyOnly) { Notify("Restored to the clipboard. Use Ctrl + V wherever you need it."); return; }
            if (_target == 0 || !NativeMethods.IsWindow(_target) || !NativeMethods.SetForegroundWindow(_target))
            { Notify("Restored to the clipboard. Focus your destination and press Ctrl + V."); return; }
            // Never synthesize paste while shortcut modifiers remain physically down.
            var ready = false;
            for (var i = 0; i < 25; i++)
            {
                await Task.Delay(20);
                if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(k => (NativeMethods.GetAsyncKeyState(k) & 0x8000) == 0)) { ready = true; break; }
            }
            if (!ready || NativeMethods.GetForegroundWindow() != _target || !NativeMethods.Paste())
                Notify("Restored to the clipboard. Press Ctrl + V in your destination app.");
        }
        catch (FileNotFoundException) { _rope!.SetStatus("An original file is unavailable. Copy the file again to update this note."); }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { _rope!.SetStatus("Could not restore this item. Try again or copy the original again."); }
        finally { _selecting = false; }
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
            _rope!.ApplyAppearance(_settings);
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
        menu.Items.Add("Project & feedback", null, (_, _) => Process.Start(new ProcessStartInfo("https://github.com/shardapps/clippboard") { UseShellExecute = true }));
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
        _maintenance?.Stop(); _clipboard?.Dispose(); _foreground?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
        _trayIcon?.Dispose();
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
