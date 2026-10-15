using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using Clippboard.App.Platform;
using Clippboard.App.Services;
using Clippboard.App.Views;
using Clippboard.Core;

namespace Clippboard.App;

public partial class App
{
    private OverlayProcess? _overlay;
    private long _historyVersion;
    private bool _closeOverlay;
    private uint _restoredSequence;
    private async Task RunOverlayAsync(string pipeName, bool software)
    {
        if (software) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        using var client = new OverlayClient(pipeName);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = await client.CallAsync(new("init"));
        if (initial.Settings?.LowMemoryRendering == true) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (initial.Demo) await client.CallAsync(new("diagnostic-renderer", Id: System.Windows.Media.RenderOptions.ProcessRenderMode.ToString()));
        if (initial.Standby)
        {
            // Warm the runtime/resource loader, not a hidden control tree. No
            // Window, payload, preview or graphics surface exists while idle.
            await client.CallAsync(new("standby-ready", Flag: Windows.Count == 0));
            do { initial = await client.CallAsync(new("wait-open")); } while (initial.Standby && !initial.Close);
            if (initial.Close) { Shutdown(); return; }
            System.Windows.Media.RenderOptions.ProcessRenderMode = initial.Settings?.LowMemoryRendering == true || software
                ? System.Windows.Interop.RenderMode.SoftwareOnly : System.Windows.Interop.RenderMode.Default;
        }
        if (initial.Close) { Shutdown(); return; }
        _rope = new RopeWindow(client, initial.Demo, initial.Settings) { StartupAvailable = initial.StartupAvailable };
        _rope.SetPaused(initial.Settings?.IsCapturePaused == true || initial.CaptureSuspended);
        _rope.SettingsSaveRequested += async settings =>
        {
            try { return (await client.CallAsync(new("settings", Settings: settings))).Error; }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { return ex.Message; }
        };
        _rope.PauseRequested += async () =>
        {
            try { var state = await client.CallAsync(new("pause")); if (state.Settings is not null) _rope.ApplyAppearance(state.Settings); }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { _rope.SetStatus("Could not change capture. Try again."); }
        };
        _rope.Selected += async summary =>
        {
            if (_selecting) return;
            _selecting = true;
            try
            {
                var copyOnly = !_rope.SavedSettings.AutoPaste || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                await client.CallAsync(new("restore", summary.Id));
                if (initial.ListenerProcess != 0) NativeMethods.AllowSetForegroundWindow((uint)initial.ListenerProcess);
                await _rope.CloseRopeAsync();
                await client.CallAsync(new("paste", Flag: copyOnly));
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            { if (!_rope.Retired) _rope.SetStatus(ex.Message); }
            finally { _selecting = false; if (_rope.Retired) { closed.TrySetResult(); Shutdown(); } }
        };
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        var polling = false; var version = initial.Version;
        poll.Tick += async (_, _) =>
        {
            if (polling || _selecting || _rope.Retired) return;
            polling = true;
            try
            {
                var state = await client.CallAsync(new("state"));
                _rope.SetPaused(state.Settings?.IsCapturePaused == true || state.CaptureSuspended);
                if (state.Close) await _rope.CloseRopeAsync();
                else if (state.Version != version)
                {
                    version = state.Version;
                    if (state.Settings is not null && state.Settings != _rope.SavedSettings) _rope.ApplyAppearance(state.Settings);
                    await _rope.RefreshAsync();
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { await _rope.CloseRopeAsync(); }
            finally { polling = false; }
        };
        _rope.Closed += (_, _) => { poll.Stop(); if (!_selecting) { closed.TrySetResult(); Shutdown(); } };
        if (initial.Demo)
        {
            _rope.ContentRendered += async (_, _) => { try { await client.CallAsync(new("diagnostic-first-frame")); } catch (IOException) { } };
            _rope.Width = 1600; _rope.Height = 720; _rope.ShowActivated = false; _rope.Topmost = false;
            _rope.DiagnosticCompactSurface();
            _rope.Show(); _rope.RenderDemo(1000); _rope.UpdateLayout(); _rope.AnimateEntrance();
            await Task.Delay(1700); _rope.DiagnosticScroll(900); await Task.Delay(900);
            _rope.OpenSettings(); await Task.Delay(700); await _rope.CloseRopeAsync();
        }
        else { await _rope.OpenAsync((nint)initial.Target); if (!_rope.Retired) poll.Start(); }
        await closed.Task;
    }

    private async Task<OverlayReply> HandleOverlayAsync(OverlayRequest request)
    {
        switch (request.Command)
        {
            case "wait-open": if (_overlay is not null) await _overlay.WaitForOpenAsync(); goto case "init";
            case "standby-ready": return new();
            case "init": case "state": return new(Settings: _settings, Target: (long)_target, Version: _historyVersion, Close: _closeOverlay, StartupAvailable: !_development && !PackageIdentity.IsPackaged, ListenerProcess: Environment.ProcessId, Standby: _overlay is { OpenRequested: false }, CaptureSuspended: _clipboard?.SessionLocked == true || (_settings.ClearOnLock && !_sessionWatchAvailable));
            case "list": return new(Entries: await Task.Run(() => _store!.ListAsync()));
            case "search": return new(Matches: await Task.Run(() => HistorySearch.MatchBatchAsync(_store!,
                request.Query ?? throw new ArgumentException("Search query required."), request.Ids ?? throw new ArgumentException("Search IDs required."))));
            case "get": return new(Clip: await Task.Run(() => _store!.GetAsync(request.Id ?? "")));
            case "preview":
                var thumbnail = await Task.Run(() => _store!.GetPreviewAsync(request.Id ?? ""));
                return new(Clip: thumbnail is null ? null : new Clip { Id = request.Id!, Kind = ClipKind.Image, ImagePng = thumbnail });
            case "pin": await Task.Run(() => _store!.SetPinnedAsync(request.Id ?? "", request.Flag)); ++_historyVersion; return new();
            case "delete": await Task.Run(() => _store!.DeleteAsync(request.Id ?? "")); ++_historyVersion; return new();
            case "clear": await Task.Run(() => _store!.ClearAsync()); ++_historyVersion; return new();
            case "settings": return new(Error: await SaveSettingsAsync(request.Settings ?? throw new ArgumentException("Settings required.")), Settings: _settings);
            case "pause": return new(Error: _clipboard?.SessionLocked == true ? await RetryLockCleanupAsync()
                : await SaveSettingsAsync(_settings with { CapturePaused = !_settings.IsCapturePaused, PauseUntil = null }), Settings: _settings);
            case "restore":
                if (_selecting) return new(Error: "An item is already being restored.");
                _selecting = true;
                try
                {
                    var clip = await Task.Run(() => _store!.GetAsync(request.Id ?? ""));
                    if (clip is null) return new(Error: "This note has expired or was deleted.");
                    try { await _clipboard!.RestoreAsync(clip); _restoredSequence = NativeMethods.GetClipboardSequenceNumber(); }
                    finally { if (clip.ImagePng is byte[] png) System.Security.Cryptography.CryptographicOperations.ZeroMemory(png); }
                    return new();
                }
                catch (FileNotFoundException) { return new(Error: "An original file is unavailable. Copy the file again."); }
                catch (FileAvailabilityException ex) { return new(Error: ex.Message); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException or FormatException or System.Security.Cryptography.CryptographicException)
                { return new(Error: "Could not restore this item. Try again or copy the original again."); }
                finally { _selecting = false; }
            case "paste":
                if (_restoredSequence == 0 || _restoredSequence != NativeMethods.GetClipboardSequenceNumber()) return new(Error: "The clipboard changed. Paste manually if needed.");
                var sequence = _restoredSequence; _restoredSequence = 0;
                await CompletePasteAsync(request.Flag, sequence); return new();
            default: throw new ArgumentException("Unknown overlay command.");
        }
    }

    private async Task CompletePasteAsync(bool copyOnly, uint sequence)
    {
        if (copyOnly) { Notify("Restored to the clipboard. Use Ctrl + V wherever you need it."); return; }
        if (_target == 0 || !NativeMethods.IsWindow(_target) || !NativeMethods.SetForegroundWindow(_target))
        { Notify("Restored to the clipboard. Focus your destination and press Ctrl + V."); return; }
        var ready = false;
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(20);
            if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(k => (NativeMethods.GetAsyncKeyState(k) & 0x8000) == 0)) { ready = true; break; }
        }
        if (!ready || NativeMethods.GetForegroundWindow() != _target || sequence != NativeMethods.GetClipboardSequenceNumber() || !NativeMethods.Paste()) Notify("Restored to the clipboard. Press Ctrl + V in your destination app.");
    }
}
