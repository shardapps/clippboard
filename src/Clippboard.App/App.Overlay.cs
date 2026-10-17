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
    private BackupJobs? _transfers;
    private long _historyVersion;
    private bool _closeOverlay;
    private long _visibilityRevision;
    private uint _restoredSequence;
    private async Task RunOverlayAsync(string pipeName, bool software, bool runtimeOnlyPreparation = false)
    {
        if (software) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        using var client = new OverlayClient(pipeName);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = await client.CallAsync(new("init"));
        if (initial.Settings?.LowMemoryRendering == true) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (initial.Demo) await client.CallAsync(new("diagnostic-renderer", Id: System.Windows.Media.RenderOptions.ProcessRenderMode.ToString()));
        if (initial.Standby)
        {
            // Measure the cheaper runtime first. A listener that has processed
            // large images is not a useful estimate of an empty child's cost.
            var preparation = await client.CallAsync(new("standby-ready", Flag: Windows.Count == 0));
            // Build the empty controls before activation, without
            // creating an HWND, reading history or decoding a preview.
            if (!runtimeOnlyPreparation && preparation.PrepareControls) _rope = new RopeWindow(client, initial.Demo, initial.Settings) { StartupAvailable = initial.StartupAvailable };
            await client.CallAsync(new("standby-prepared", Id: _rope is null ? "runtime" : "controls", Flag: _rope is null ? Windows.Count == 0 : !_rope.IsVisible && new System.Windows.Interop.WindowInteropHelper(_rope).Handle == 0));
            do { initial = await client.CallAsync(new("wait-open")); } while (initial.Standby && !initial.Close);
            if (initial.Close) { Shutdown(); return; }
            System.Windows.Media.RenderOptions.ProcessRenderMode = initial.Settings?.LowMemoryRendering == true || software
                ? System.Windows.Interop.RenderMode.SoftwareOnly : System.Windows.Interop.RenderMode.Default;
        }
        if (initial.Close) { Shutdown(); return; }
        _rope ??= new RopeWindow(client, initial.Demo, initial.Settings) { StartupAvailable = initial.StartupAvailable };
        if (initial.Settings is not null && _rope.SavedSettings != initial.Settings) _rope.ApplyAppearance(initial.Settings);
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
        async Task RestoreAndPasteAsync(OverlayRequest request, bool copyOnly)
        {
            if (_selecting) return;
            _selecting = true;
            try
            {
                _rope.SetStatus("Preparing this note…");
                await client.CallAsync(request);
                if (initial.ListenerProcess != 0) NativeMethods.AllowSetForegroundWindow((uint)initial.ListenerProcess);
                await _rope.CloseRopeAsync();
                await client.CallAsync(new("paste", Flag: copyOnly));
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            { if (!_rope.Retired) _rope.SetStatus(ex.Message); }
            finally { _selecting = false; if (_rope.Retired) { closed.TrySetResult(); Shutdown(); } }
        }
        _rope.Selected += summary => RestoreAndPasteAsync(new("restore", summary.Id), !_rope.SavedSettings.AutoPaste || (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        _rope.EditedTextSelected += text => RestoreAndPasteAsync(new("restore", Text: text), true);
        var visibilityRevision = initial.VisibilityRevision;
        _rope.DismissRequested += async () =>
        {
            using var deadline = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try { await client.CallAsync(new("dismiss", VisibilityRevision: visibilityRevision), deadline.Token); }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
        };
        async Task ApplyVisibilityAsync(bool close, long revision)
        {
            if (revision <= visibilityRevision || _rope.Retired) return;
            visibilityRevision = revision;
            if (close) _ = _rope.RequestRetractionAsync();
            else
            {
                try { await _rope.ResumeRequestedOpenAsync(explicitRequest: true); }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { await _rope.CloseRopeAsync(); }
            }
        }
        _rope.VisibilityRequested += async (close, revision) => await ApplyVisibilityAsync(close, revision);
        _rope.SourceInitialized += async (_, _) =>
        {
            try { await client.CallAsync(new("surface-ready", Surface: (long)new System.Windows.Interop.WindowInteropHelper(_rope).Handle)); }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
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
                await ApplyVisibilityAsync(state.Close, state.VisibilityRevision);
                if (!state.Close && state.Version != version)
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
            case "surface-ready":
                if (_overlay?.SetSurface((nint)request.Surface) == true) _overlay.NotifyVisibility(_closeOverlay, _visibilityRevision);
                return new();
            case "dismiss":
                if (request.VisibilityRevision == _visibilityRevision) { _closeOverlay = true; ++_visibilityRevision; }
                return new();
            case "diagnostics":
                var diagnosticSettings = _settings; var diagnosticProcess = _overlay?.Process;
                var captureSuspended = _clipboard?.SessionLocked == true || (_settings.ClearOnLock && !_sessionWatchAvailable);
                var transferActive = _transfers?.HoldsCapture == true;
                return new(Diagnostics: await Task.Run(() => SupportDiagnostics.Collect(diagnosticSettings, diagnosticProcess, captureSuspended, transferActive)));
            case "transfer-export": case "transfer-import":
                if (_sessionLocked || _clipboard?.SessionLocked == true) return new(Error: "Unlock Windows before transferring history.");
                _transfers ??= new BackupJobs(_store!, () =>
                {
                    if (!_exiting && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => { if (!_exiting) ++_historyVersion; }));
                });
                return new(Transfer: _transfers.Start(request.Id ?? "", request.Command == "transfer-import", request.Path ?? throw new ArgumentException("Backup path required."),
                    request.Secret ?? throw new ArgumentException("Password required."), request.Destination, _captureLease.Token));
            case "transfer-state": return new(Transfer: _transfers?.Snapshot(request.Id ?? ""));
            case "transfer-commit":
                if (_sessionLocked || _clipboard?.SessionLocked == true) return new(Error: "Unlock Windows before restoring history.");
                return new(Transfer: _transfers?.Commit(request.Id ?? "") ?? throw new InvalidOperationException("Inspect a backup first."));
            case "transfer-cancel": _transfers?.Cancel(request.Id ?? ""); return new();
            case "wait-open": if (_overlay is not null) await _overlay.WaitForOpenAsync(); goto case "init";
            case "standby-ready":
                if (_overlay is null || _overlay.OpenRequested || !request.Flag) return new();
                _runtimePreparationBytes = MeasuredStandbyBytes();
                using (var listener = System.Diagnostics.Process.GetCurrentProcess())
                {
                    _overlay.ControlsPrepared = _runtimePreparationBytes > 0 && _settings.FastOpen && MemoryAvailability.PrepareControls(MemoryAvailability.AvailableBytes(), _controlPreparationBytes, listener.PrivateMemorySize64);
                    return new(PrepareControls: _overlay.ControlsPrepared);
                }
            case "standby-prepared":
                if (_overlay is not null && !_overlay.OpenRequested && request.Flag && request.Id == "controls")
                { _overlay.ControlsPrepared = true; _controlPreparationBytes = Math.Max(_controlPreparationBytes, MeasuredStandbyBytes()); }
                return new();
            case "init": case "state": return new(Settings: _settings, Target: (long)_target, Version: _historyVersion, Close: _closeOverlay, VisibilityRevision: _visibilityRevision, StartupAvailable: !_development && !PackageIdentity.IsPackaged, ListenerProcess: Environment.ProcessId, Standby: _overlay is { OpenRequested: false }, CaptureSuspended: _transfers?.HoldsCapture == true || _clipboard?.SessionLocked == true || (_settings.ClearOnLock && !_sessionWatchAvailable));
            case "list": return new(Entries: await Task.Run(() => _store!.ListAsync()));
            case "search": return new(Matches: await Task.Run(() => HistorySearch.MatchBatchAsync(_store!,
                request.Query ?? throw new ArgumentException("Search query required."), request.Ids ?? throw new ArgumentException("Search IDs required."))));
            case "get": return new(Clip: await Task.Run(() => _store!.GetAsync(request.Id ?? "")));
            case "save-snippet":
                if (_sessionLocked || _clipboard?.SessionLocked == true) return new(Error: "Unlock and finish history cleanup before saving a snippet.");
                var saveCancellation = _captureLease.Token;
                var saved = await Task.Run(() => _store!.SaveSnippetAsync(request.Id, request.Text ?? throw new ArgumentException("Snippet text required."), request.Title ?? "", request.Labels ?? [], saveCancellation));
                if (saved) ++_historyVersion;
                return new(Saved: saved);
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
                    using var restoreLifetime = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_captureLease.Token, _overlay?.Cancellation ?? System.Threading.CancellationToken.None);
                    if (request.Text?.Length > TextReuse.MaxTextLength) throw new ArgumentException("Edited text exceeds its limit.");
                    var clip = request.Text is not null ? new Clip { Kind = ClipKind.Text, Text = request.Text }
                        : await Task.Run(() => _store!.GetAsync(request.Id ?? ""));
                    if (clip is null) return new(Error: "This note has expired or was deleted.");
                    try { await _clipboard!.RestoreAsync(clip, restoreLifetime.Token); _restoredSequence = NativeMethods.GetClipboardSequenceNumber(); }
                    finally { if (clip.ImagePng is byte[] png) System.Security.Cryptography.CryptographicOperations.ZeroMemory(png); }
                    return new();
                }
                catch (ImageMemoryException ex) { return new(Error: ex.Message); }
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
