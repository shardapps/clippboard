using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clippboard.App.Platform;
using Clippboard.Core;

namespace Clippboard.App.Services;

internal sealed class ClipboardService : IDisposable
{
    private readonly MessageWindow _source;
    private readonly DispatcherTimer _retry;
    private readonly Func<Clip, Task> _save;
    private uint _sequence;
    private uint _ownSequence;
    private int _attempts;
    private bool _saving;
    private bool _disposed;
    private readonly FileAvailabilityProbe _fileProbe = new();
    private readonly BoundedClipboardReader _reader;
    public bool Paused { get; set; }
    private bool _sessionLocked;
    private int _sessionEpoch;
    public bool SessionLocked
    {
        get => _sessionLocked;
        set { if (_sessionLocked == value) return; _sessionLocked = value; ++_sessionEpoch; }
    }
    private bool CaptureBlocked => Paused || SessionLocked || Settings.IsCapturePaused;
    private bool SourceAllowed()
    {
        if (Settings.ExcludedApps.Length == 0) return true;
        var owner = NativeMethods.GetClipboardOwner();
        if (owner == 0) return false;
        NativeMethods.GetWindowThreadProcessId(owner, out var process);
        try
        {
            using var source = System.Diagnostics.Process.GetProcessById((int)process);
            return CapturePolicy.AllowsSource(Settings.ExcludedApps, source.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; } // Unknown sources are skipped when exclusions are active.
    }
    public AppSettings Settings { get; set; } = new();
    public bool HotkeyAvailable { get; private set; }
    private int _hotkeyId = 1;
    private ShortcutChoice _shortcut;
    public event Action? ToggleRequested;
    public event Action<string>? Notice;
    private static void SessionTrace(string message)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && Environment.GetEnvironmentVariable("CLIPPBOARD_SESSION_DIAGNOSTICS") == "1") Console.Error.WriteLine(message);
    }

    public ClipboardService(Func<Clip, Task> save, ShortcutChoice shortcut = ShortcutChoice.CtrlShiftV, Func<AppSettings, Clip?>? read = null)
    {
        _save = save;
        _reader = new(read ?? ReadCurrent);
        _source = new MessageWindow(WndProc);
        if (!NativeMethods.AddClipboardFormatListener(_source.Handle)) { _source.Dispose(); throw new InvalidOperationException("Cannot listen to the Windows clipboard."); }
        _shortcut = shortcut;
        HotkeyAvailable = NativeMethods.RegisterHotKey(_source.Handle, _hotkeyId, ShortcutModifiers(shortcut), 0x56);
        _retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _retry.Tick += Retry;
        SessionTrace("Clipboard listener ready");
    }

    private bool WndProc(int message, nint wParam, nint lParam)
    {
        if (_disposed) return false;
        if (message == 0x0312 && wParam == _hotkeyId) { ToggleRequested?.Invoke(); return true; }
        if (message == 0x031D && !CaptureBlocked)
        {
            SessionTrace("Clipboard update received");
            _sequence = NativeMethods.GetClipboardSequenceNumber();
            if (_sequence != _ownSequence) { _attempts = 0; _retry.Start(); }
        }
        return false;
    }

    private async void Retry(object? sender, EventArgs e)
    {
        if (_disposed) { _retry.Stop(); return; }
        if (_saving) return;
        if (CaptureBlocked || !SourceAllowed() || _sequence == _ownSequence) { _retry.Stop(); return; }
        var sequence = _sequence;
        var sessionEpoch = _sessionEpoch;
        _saving = true;
        try
        {
            var clip = await _reader.ReadAsync(Settings);
            SessionTrace(clip is null ? "Read returned no supported clip" : "Read returned " + clip.Kind);
            _retry.Stop();
            if (_disposed || sessionEpoch != _sessionEpoch || CaptureBlocked || !SourceAllowed() || sequence != NativeMethods.GetClipboardSequenceNumber() || clip is null)
            {
                if (clip?.ImagePng is byte[] png) System.Security.Cryptography.CryptographicOperations.ZeroMemory(png);
                return;
            }
            // A successful callback owns this payload; clearing it here would corrupt retained consumers.
            await _save(clip);
        }
        catch (COMException)
        {
            SessionTrace("Clipboard COM retry");
            if (++_attempts >= 8) { _retry.Stop(); if (!_disposed) Notice?.Invoke("Clipboard was busy. Copy that item again to save it."); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            SessionTrace("Clipboard read failed: " + ex.GetType().Name + ": " + ex.Message);
            _retry.Stop();
            if (!_disposed) Notice?.Invoke("This clipboard item could not be saved. Try copying it again.");
        }
        finally { _saving = false; if (_sequence != sequence && !CaptureBlocked && !_disposed && !_reader.HasOutstandingRead) _retry.Start(); }
    }

    internal static Clip? ReadCurrent(AppSettings? settings = null)
    {
        var data = Clipboard.GetDataObject();
        return data is null ? null : ReadData(data, Clipboard.GetImage, settings);
    }

    // Separate conversion from OS access so format/limit checks can use synthetic data.
    internal static Clip? ReadData(IDataObject data, Func<System.Windows.Media.Imaging.BitmapSource?> imageProvider, AppSettings? settings = null)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing", false)) return null;
        if (settings?.IsCapturePaused == true) return null;
        if (data.GetDataPresent("CanIncludeInClipboardHistory", false))
        {
            var flag = data.GetData("CanIncludeInClipboardHistory", false);
            if (flag is MemoryStream stream && stream.Length >= 4)
            {
                stream.Position = 0;
                if (new BinaryReader(stream, System.Text.Encoding.UTF8, true).ReadUInt32() == 0) return null;
            }
        }
        if (settings?.CaptureFiles != false && data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = data.GetData(DataFormats.FileDrop) as string[];
            if (paths is null || paths.Length == 0 || paths.Length > 100 || paths.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 32767)) return null;
            return new Clip { Kind = ClipKind.Files, FilePaths = paths };
        }
        if (settings?.CaptureImages != false && data.GetDataPresent(DataFormats.Bitmap))
        {
            var image = imageProvider();
            if (image is null || (long)image.PixelWidth * image.PixelHeight > 16_000_000) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var buffer = new BoundedImageBuffer(16 * 1024 * 1024);
            try { encoder.Save(buffer); }
            catch (Exception ex) when (buffer.LimitExceeded && ex is IOException or ArgumentException or COMException or InvalidOperationException or NotSupportedException)
            { return null; } // WIC can translate the stream guard into an HRESULT.
            if (buffer.LimitExceeded || buffer.Length > 16 * 1024 * 1024) return null;
            return new Clip { Kind = ClipKind.Image, ImagePng = buffer.ToArray() };
        }
        if (settings?.CaptureText != false && data.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = data.GetData(DataFormats.UnicodeText) as string;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 512 * 1024) return null;
            return new Clip { Kind = ClipKind.Text, Text = text,
                Html = settings?.CaptureFormattedText != false ? FormattedClipboard.Read(data, DataFormats.Html) : null,
                Rtf = settings?.CaptureFormattedText != false ? FormattedClipboard.Read(data, DataFormats.Rtf) : null };
        }
        return null;
    }

    public async Task RestoreAsync(Clip clip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var data = new DataObject();
        switch (clip.Kind)
        {
            case ClipKind.Text: data = FormattedClipboard.ForText(clip); break;
            case ClipKind.Image:
                // Preserve original dimensions, without retaining its encoded buffer.
                data.SetImage(PreviewCache.Decode(clip.ImagePng ?? [], int.MaxValue, int.MaxValue));
                break;
            case ClipKind.Files:
                var paths = clip.FilePaths ?? [];
                // Avoid blocking Explorer's UI on unavailable network shares.
                var available = await _fileProbe.CheckAsync(paths);
                if (!available) throw new FileNotFoundException("An original file was moved, deleted, or is unavailable. Copy it again.");
                var files = new StringCollection(); files.AddRange(paths); data.SetFileDropList(files);
                // Always restore as COPY, never replay an old cut operation.
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
                break;
        }
        // The setter is synchronous, so own sequence is recorded before the message pump handles its event.
        ObjectDisposedException.ThrowIf(_disposed, this);
        Clipboard.SetDataObject(data, true);
        _ownSequence = NativeMethods.GetClipboardSequenceNumber();
    }

    internal static uint ShortcutModifiers(ShortcutChoice choice) => 0x4000u | (choice switch { ShortcutChoice.AltV => 0x0001u, ShortcutChoice.CtrlAltV => 0x0003u, _ => 0x0006u });
    public bool ChangeShortcut(ShortcutChoice choice)
    {
        if (choice == _shortcut && HotkeyAvailable) return true;
        var nextId = _hotkeyId == 1 ? 2 : 1;
        // Reserve the replacement before releasing the working shortcut.
        if (!NativeMethods.RegisterHotKey(_source.Handle, nextId, ShortcutModifiers(choice), 0x56)) return false;
        if (HotkeyAvailable) NativeMethods.UnregisterHotKey(_source.Handle, _hotkeyId);
        _hotkeyId = nextId; _shortcut = choice; HotkeyAvailable = true; return true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reader.Dispose(); _retry.Stop(); _retry.Tick -= Retry;
        NativeMethods.UnregisterHotKey(_source.Handle, _hotkeyId);
        NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        _source.Dispose();
    }
}
