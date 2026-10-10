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
                await PreviewAsync(preview + 1 < e.Args.Length ? e.Args[preview + 1] : throw new ArgumentException("Preview output path required."));
                Shutdown(); return;
            }
            _instance = new Mutex(true, @"Local\ShardApps.Clippboard." + Environment.UserName + (_development ? ".dev" : ""), out _ownsMutex);
            if (!_ownsMutex) { MessageBox.Show("Clippboard is already running. Use Ctrl + Shift + V or its tray icon.", "Clippboard"); Shutdown(); return; }
            _settingsStore = new SettingsStore(Path.Combine(root, "settings.json"));
            _settings = await _settingsStore.LoadAsync();
            _store = new HistoryStore(Path.Combine(root, "history"), new WindowsProtector(), _settings.HistoryLimits());
            await Task.Run(() => _store.InitializeAsync());
            _rope = new RopeWindow(_store);
            _rope.Selected += SelectAsync;
            _rope.PauseRequested += TogglePause;
            _clipboard = new ClipboardService(SaveAsync) { Settings = _settings, Paused = _settings.CapturePaused };
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
            if (!_clipboard.HotkeyAvailable) Notify("Ctrl + Shift + V is used by another app. Open Clippboard from its tray icon.");
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
                _target = _foreground?.LastWindow ?? NativeMethods.GetForegroundWindow();
                await _rope.OpenAsync(_target);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Notify("Could not open history. Try again."); }
        finally { _toggling = false; }
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
        var next = _settings with { CapturePaused = !_clipboard.Paused };
        try { await _settingsStore!.SaveAsync(next); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("Could not save capture preference."); return; }
        _settings = next;
        _clipboard.Settings = next;
        _clipboard.Paused = next.CapturePaused;
        _rope!.SetPaused(_clipboard.Paused);
        _tray!.Text = _clipboard.Paused ? "Clippboard · Capture paused" : "Clippboard · Ctrl + Shift + V";
        Notify(_clipboard.Paused ? "Capture paused. New copies won't be saved." : "Capture resumed.");
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open rope · Ctrl + Shift + V", null, (_, _) => ToggleRope());
        menu.Items.Add("Pause / resume capture", null, (_, _) => TogglePause());
        if (!_development)
        {
            var startup = new Forms.ToolStripMenuItem("Start at sign-in") { Checked = StartupEnabled() };
            startup.Click += (_, _) =>
            {
                try { SetStartup(!startup.Checked); startup.Checked = StartupEnabled(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { Notify("Could not change sign-in startup. Check your Windows account permissions."); }
            };
            menu.Items.Add(startup);
        }
        menu.Items.Add("Project & feedback", null, (_, _) => Process.Start(new ProcessStartInfo("https://github.com/shardapps/clippboard") { UseShellExecute = true }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Clippboard", null, (_, _) => Shutdown());
        _trayIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (Icon)SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "Clippboard · Ctrl + Shift + V", ContextMenuStrip = menu, Visible = true };
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

    private async Task PreviewAsync(string output)
    {
        _rope = new RopeWindow(_store!, preview: true) { Width = 1240, Height = 560, ShowInTaskbar = false };
        _rope.Show(); _rope.RenderDemo();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        _rope.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1240, 560, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_rope);
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
