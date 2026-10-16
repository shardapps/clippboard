using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Clippboard.App;

public partial class App
{
    private CancellationTokenSource _captureLease = new();
    private bool _sessionLocked;
    private bool _sessionWatchAvailable = true;
    private Task<bool> _lockCleanup = Task.FromResult(true);
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not SessionSwitchReason.SessionLock and not SessionSwitchReason.SessionUnlock) return;
        Dispatcher.BeginInvoke(new Action(async () => await SetSessionLockedAsync(e.Reason == SessionSwitchReason.SessionLock)));
    }
    private async Task SetSessionLockedAsync(bool locked)
    {
        if (_exiting || _clipboard is null || _store is null) return;
        _sessionLocked = locked;
        if (locked)
        {
            _clipboard.SessionLocked = true; _transfers?.Cancel(); _captureLease.Cancel(); _closeOverlay = true;
            if (_settings.ClearOnLock) _lockCleanup = ClearLockedHistoryAsync();
            ++_historyVersion;
        }
        else
        {
            // Do not save newly unlocked copies ahead of a queued lock purge.
            var cleared = await _lockCleanup;
            if (_sessionLocked || _exiting || !_clipboard.SessionLocked) return;
            if (!cleared) { Notify("Capture stays suspended until history cleanup succeeds. Use Resume capture to retry cleanup."); return; }
            ResumeAfterLockCleanup();
            MaintainStandby();
        }
    }
    private void ResumeAfterLockCleanup()
    {
        _captureLease.Dispose(); _captureLease = new(); _clipboard!.SessionLocked = false; ++_historyVersion;
    }
    private async Task<string?> RetryLockCleanupAsync()
    {
        if (_sessionLocked) return "Capture is suspended while Windows is locked.";
        _lockCleanup = ClearLockedHistoryAsync();
        if (!await _lockCleanup) return "History cleanup still needs storage access. Capture remains suspended.";
        if (!_sessionLocked && !_exiting) ResumeAfterLockCleanup();
        return null;
    }
    private async Task<bool> ClearLockedHistoryAsync()
    {
        try { await Task.Run(() => _store!.ClearAsync()); ++_historyVersion; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { if (!_exiting) Notify("Could not finish clearing locked history. Check storage permissions; restart retries interrupted cleanup."); return false; }
    }
    private void RefreshTimedPause()
    {
        if (_settings.PauseUntil is not { } until || until > DateTimeOffset.UtcNow) return;
        _settings = _settings with { PauseUntil = null }; ++_historyVersion;
        if (_clipboard is not null) _clipboard.Settings = _settings;
        if (_tray is not null) _tray.Text = _settings.IsCapturePaused ? "Clippboard · Capture paused" : "Clippboard · " + Views.SettingsView.ShortcutLabel(_settings.Shortcut);
    }
}
