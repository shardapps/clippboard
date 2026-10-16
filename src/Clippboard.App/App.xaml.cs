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
    private DispatcherTimer? _standbyMaintenance;
    private long _standbyPrivateBytes;
    private bool _exiting;
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
            // Only disposable visual processes need the control/style tree.
            // The long-lived tray listener keeps no application UI dictionary.
            if (e.Args.Contains("--overlay") || e.Args.Contains("--preview") || e.Args.Contains("--diagnostics"))
                Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clippboard;component/Design/Controls.xaml", UriKind.Relative) });
            var lifecycleIndex = Array.IndexOf(e.Args, "--lifecycle-diagnostics");
            if (lifecycleIndex >= 0) { if (lifecycleIndex + 1 >= e.Args.Length) throw new ArgumentException("Diagnostic output required."); await OverlayDiagnostics.RunAsync(e.Args[lifecycleIndex + 1], e.Args.Contains("--software")); Shutdown(); return; }
            var decodeIndex = Array.IndexOf(e.Args, "--decode-diagnostics");
            if (decodeIndex >= 0) { if (decodeIndex + 1 >= e.Args.Length) throw new ArgumentException("Diagnostic output required."); await PerformanceDiagnostics.RunDecodeAsync(e.Args[decodeIndex + 1]); Shutdown(); return; }
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
                if (e.Args.Contains("--scroll-only")) await PerformanceDiagnostics.RunScrollAsync(_store, e.Args[diagnostic + 1]);
                else await PerformanceDiagnostics.RunAsync(_store, e.Args[diagnostic + 1], e.Args.Contains("--software"));
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
            if (!_development) _settings = _settings with { StartAtLogin = !PackageIdentity.IsPackaged && StartupEnabled() };
            _store = new HistoryStore(Path.Combine(root, "history"), new WindowsProtector(), _settings.HistoryLimits(), PreviewCache.EncodeThumbnail);
            await Task.Run(() => _store.InitializeAsync());

            _clipboard = new ClipboardService(SaveAsync, _settings.Shortcut) { Settings = _settings, Paused = _settings.CapturePaused, CaptureHold = () => _transfers?.HoldsCapture == true };
            try { SystemEvents.SessionSwitch += OnSessionSwitch; }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            { _sessionWatchAvailable = false; _clipboard.Paused |= _settings.ClearOnLock; }

            _clipboard.ToggleRequested += ToggleRope;
            _clipboard.Notice += Notify;
            _foreground = new ForegroundTracker();
            CreateTray();
            if (!_sessionWatchAvailable) Notify(_settings.ClearOnLock
                ? "Windows lock monitoring is unavailable. Capture is paused; disable clear-on-lock in settings to continue."
                : "Windows lock monitoring is unavailable in this session. Clear-on-lock cannot be enabled.");
            _maintenance = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            _maintenance.Tick += async (_, _) =>
            {
                try { await Task.Run(() => _store.PruneAsync()); ++_historyVersion; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("History cleanup failed. Check available disk space and permissions."); }
            };
            _maintenance.Start();
            _standbyMaintenance = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _standbyMaintenance.Tick += (_, _) => { RefreshTimedPause(); _transfers?.Expire(); MaintainStandby(); };
            _standbyMaintenance.Start();
            if (!_clipboard.HotkeyAvailable) Notify(SettingsView.ShortcutLabel(_settings.Shortcut) + " is used by another app. Open Clippboard from its tray icon.");
            if (_store.UnreadableEntries > 0) Notify($"Removed {_store.UnreadableEntries} damaged or unreadable history entries.");
            if (!e.Args.Contains("--background")) ToggleRope();
            else _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(MaintainStandby));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException or ArgumentException)
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
            if (!_settings.Captures(clip.Kind) || _sessionLocked || (_settings.ClearOnLock && !_sessionWatchAvailable)) return;
            var cancellation = _captureLease.Token;
            if (!await Task.Run(() => _store!.AddAsync(clip, cancellation))) Notify("History is full of pinned notes, or this item exceeds the storage limit. Unpin or delete some notes.");
            else ++_historyVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { Notify("Could not save this clip. Check disk space and storage permissions."); }
        catch (OperationCanceledException) { /* Session lock invalidates in-flight captures. */ }
        finally { if (clip.ImagePng is byte[] png) System.Security.Cryptography.CryptographicOperations.ZeroMemory(png); }
    }

    private void ToggleRope()
    {
        if (_toggling || _selecting || _store is null || _sessionLocked) return;
        if (_overlay is not null && !_overlay.Process.HasExited)
        {
            if (_overlay.OpenRequested) { _closeOverlay = !_closeOverlay; ++_visibilityRevision; _overlay.NotifyVisibility(_closeOverlay, _visibilityRevision); return; }
            _target = _foreground?.LastWindow ?? NativeMethods.GetForegroundWindow();
            _closeOverlay = false; _restoredSequence = 0;
            NativeMethods.AllowSetForegroundWindow((uint)_overlay.Process.Id);
            _overlay.RequestOpen(); return;
        }
        StartOverlay(open: true);
    }
    private void MaintainStandby()
    {
        if (_exiting || _toggling || _store is null || _sessionLocked || _overlay is { OpenRequested: true }) return;
        if (_standbyPrivateBytes <= 0)
        {
            using var listener = Process.GetCurrentProcess(); _standbyPrivateBytes = listener.PrivateMemorySize64;
        }
        var running = _overlay is not null && !_overlay.Process.HasExited;
        if (running)
        {
            try { _overlay!.Process.Refresh(); _standbyPrivateBytes = Math.Max(_standbyPrivateBytes, _overlay.Process.PrivateMemorySize64); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return; }
        }
        if (!_settings.FastOpen || !MemoryAvailability.KeepStandby(MemoryAvailability.AvailableBytes(), _standbyPrivateBytes, running))
        {
            if (running) { var standby = _overlay!; _overlay = null; if (_foreground is not null) _foreground.OverlayProcessId = 0; standby.Dispose(); }
            return;
        }
        if (!running) StartOverlay(open: false);
    }
    private void StartOverlay(bool open)
    {
        _toggling = true;
        try
        {
            _overlay?.Dispose(); _closeOverlay = false; _visibilityRevision = 0; _restoredSequence = 0;
            if (open) _target = _foreground?.LastWindow ?? NativeMethods.GetForegroundWindow();
            OverlayProcess? session = null;
            session = new OverlayProcess(request => Dispatcher.InvokeAsync(() =>
                ReferenceEquals(_overlay, session) && !_exiting ? HandleOverlayAsync(request)
                    : Task.FromResult(new OverlayReply(Error: "This rope session has closed."))).Task.Unwrap(), standby: !open);
            _overlay = session; _foreground!.OverlayProcessId = (uint)session.Process.Id;
            if (open) NativeMethods.AllowSetForegroundWindow((uint)session.Process.Id);
            session.Process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_overlay, session)) return;
                var failed = session.Process.ExitCode != 0 && session.OpenRequested; _overlay = null; _foreground.OverlayProcessId = 0; _transfers?.Cancel(); session.Dispose();
                if (failed) Notify("The rope closed unexpectedly. Your saved history is safe; open it again.");
                if (!failed && !_exiting && !_sessionLocked && session.OpenRequested && !_closeOverlay) StartOverlay(open: true);
                else if (!failed && !_exiting) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(MaintainStandby));
            }));
            session.Process.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { if (open) Notify("Could not open history. Try again."); }
        finally { _toggling = false; }
    }
    private async void TogglePause()
    {
        if (_clipboard is null) return;
        if (_clipboard.SessionLocked) { Notify(await RetryLockCleanupAsync() ?? "History cleanup finished; session capture resumed."); return; }
        var error = await SaveSettingsAsync(_settings with { CapturePaused = !_settings.IsCapturePaused, PauseUntil = null });
        Notify(error ?? (_settings.IsCapturePaused ? "Capture paused. New copies won't be saved." : "Capture resumed."));
    }

    private async Task<string?> SaveSettingsAsync(AppSettings settings)
    {
        if (_savingSettings || _clipboard is null) return "A settings change is already in progress. Try again.";
        if (CapturePolicy.ValidateExclusions(settings.ExcludedApps) is string invalid) return invalid;
        if (settings.ClearOnLock && !_sessionWatchAvailable) return "Windows lock monitoring is unavailable. Disable clear-on-lock to save settings and resume capture.";
        _savingSettings = true;
        var previous = _settings; var next = settings.Normalize(); var persisted = false;
        if (PackageIdentity.IsPackaged) next = next with { StartAtLogin = false };
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
            if (!_development && !PackageIdentity.IsPackaged && next.StartAtLogin != previous.StartAtLogin) SetStartup(next.StartAtLogin);
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
                if (!_development && !PackageIdentity.IsPackaged && next.StartAtLogin != previous.StartAtLogin)
                    try { SetStartup(previous.StartAtLogin); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Notify("Check your Windows sign-in startup preference."); }
            }
            _clipboard.Settings = _settings; _clipboard.Paused = _settings.CapturePaused || (_settings.ClearOnLock && !_sessionWatchAvailable);
            ++_historyVersion;
            if (_tray is not null)
            {
                _tray.Text = _settings.IsCapturePaused ? "Clippboard · Capture paused" : "Clippboard · " + SettingsView.ShortcutLabel(_settings.Shortcut);
                _tray.ContextMenuStrip!.Items[0].Text = "Open rope · " + SettingsView.ShortcutLabel(_settings.Shortcut);
                if (!_development && !PackageIdentity.IsPackaged && _tray.ContextMenuStrip.Items[2] is Forms.ToolStripMenuItem startup) startup.Checked = _settings.StartAtLogin;
            }
            _savingSettings = false;
            MaintainStandby();
        }
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open rope · " + SettingsView.ShortcutLabel(_settings.Shortcut), null, (_, _) => ToggleRope());
        menu.Items.Add("Pause / resume capture", null, (_, _) => TogglePause());
        if (!_development && PackageIdentity.IsPackaged)
            menu.Items.Add("Manage Windows startup", null, (_, _) => { try { Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { Notify("Open Windows Settings → Apps → Startup to enable Clippboard."); } });
        else if (!_development)
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
        if (args.Contains("--help-preview")) _rope.OpenSettings(4);
        if (args.Contains("--notices")) _rope.OpenNotices();
        if (args.Contains("--detail"))
        {
            var clip = new Clip { Kind = ClipKind.Text, Text = "A little more room to think.\n\n" + string.Join("\n\n", Enumerable.Repeat("Open the whole note. Read at your own pace. Resize it, select a passage, then restore it when you need it.", 12)) };
            await _rope.PreviewDetailAsync(clip);
        }
        if (args.Contains("--snippet"))
            await _rope.OpenTextEditorAsync(new Clip { Kind = ClipKind.Text, IsSnippet = true, Title = "A friendly follow-up", Labels = ["work", "email"],
                Text = "Hi there,\n\nThanks for sharing this. I'll take a look and get back to you shortly.\n\nBest wishes" }.Summary(0));
        if (args.Contains("--combination")) await _rope.PreviewCombinationAsync();
        if (args.Contains("--ocr")) await _rope.PreviewOcrAsync();
        if (args.Contains("--backup")) _rope.PreviewBackup();
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
        _exiting = true; _standbyMaintenance?.Stop();
        _transfers?.Dispose();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _captureLease.Cancel(); _captureLease.Dispose();
        _overlay?.Dispose(); _maintenance?.Stop(); _clipboard?.Dispose(); _foreground?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
        _trayIcon?.Dispose();
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
