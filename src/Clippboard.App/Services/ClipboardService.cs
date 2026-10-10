using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clippboard.App.Platform;
using Clippboard.Core;

namespace Clippboard.App.Services;

internal sealed class ClipboardService : IDisposable
{
    private readonly HwndSource _source;
    private readonly DispatcherTimer _retry;
    private readonly Func<Clip, Task> _save;
    private uint _sequence;
    private uint _ownSequence;
    private int _attempts;
    private bool _saving;
    public bool Paused { get; set; }
    public AppSettings Settings { get; set; } = new();
    public bool HotkeyAvailable { get; }
    public event Action? ToggleRequested;
    public event Action<string>? Notice;

    public ClipboardService(Func<Clip, Task> save)
    {
        _save = save;
        _source = new HwndSource(new HwndSourceParameters("Clippboard listener") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
        if (!NativeMethods.AddClipboardFormatListener(_source.Handle)) throw new InvalidOperationException("Cannot listen to the Windows clipboard.");
        HotkeyAvailable = NativeMethods.RegisterHotKey(_source.Handle, 1, 0x4000 | 0x0002 | 0x0004, 0x56); // No repeat + Ctrl + Shift + V
        _retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _retry.Tick += Retry;
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312) { ToggleRequested?.Invoke(); handled = true; }
        if (message == 0x031D && !Paused)
        {
            _sequence = NativeMethods.GetClipboardSequenceNumber();
            if (_sequence != _ownSequence) { _attempts = 0; _retry.Start(); }
        }
        return 0;
    }

    private async void Retry(object? sender, EventArgs e)
    {
        if (_saving) return;
        if (Paused || _sequence == _ownSequence) { _retry.Stop(); return; }
        var sequence = _sequence;
        try
        {
            var clip = ReadCurrent(Settings);
            _retry.Stop();
            if (sequence != NativeMethods.GetClipboardSequenceNumber() || clip is null) return;
            _saving = true;
            try { await _save(clip); }
            finally { _saving = false; if (_sequence != sequence && !Paused) _retry.Start(); }
        }
        catch (COMException)
        {
            if (++_attempts >= 8) { _retry.Stop(); Notice?.Invoke("Clipboard was busy. Copy that item again to save it."); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _retry.Stop();
            Notice?.Invoke("This clipboard item could not be saved. Try copying it again.");
        }
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
        if (settings?.CapturePaused == true) return null;
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
            if (paths is null || paths.Length == 0 || paths.Length > 100 || paths.Any(x => x.Length > 32767)) return null;
            return new Clip { Kind = ClipKind.Files, FilePaths = paths };
        }
        if (settings?.CaptureImages != false && data.GetDataPresent(DataFormats.Bitmap))
        {
            var image = imageProvider();
            if (image is null || (long)image.PixelWidth * image.PixelHeight > 16_000_000) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            if (buffer.Length > 16 * 1024 * 1024) return null;
            return new Clip { Kind = ClipKind.Image, ImagePng = buffer.ToArray() };
        }
        if (settings?.CaptureText != false && data.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = data.GetData(DataFormats.UnicodeText) as string;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 512 * 1024) return null;
            return new Clip { Kind = ClipKind.Text, Text = text };
        }
        return null;
    }

    public async Task RestoreAsync(Clip clip)
    {
        var data = new DataObject();
        switch (clip.Kind)
        {
            case ClipKind.Text: data.SetText(clip.Text ?? "", TextDataFormat.UnicodeText); break;
            case ClipKind.Image:
                using (var stream = new MemoryStream(clip.ImagePng ?? []))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                    data.SetImage(bitmap);
                }
                break;
            case ClipKind.Files:
                var paths = clip.FilePaths ?? [];
                // Avoid blocking Explorer's UI on unavailable network shares.
                var available = await Task.Run(() => paths.All(x => File.Exists(x) || Directory.Exists(x)));
                if (!available) throw new FileNotFoundException("An original file was moved, deleted, or is unavailable. Copy it again.");
                var files = new StringCollection(); files.AddRange(paths); data.SetFileDropList(files);
                // Always restore as COPY, never replay an old cut operation.
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
                break;
        }
        // The setter is synchronous, so own sequence is recorded before the message pump handles its event.
        Clipboard.SetDataObject(data, true);
        _ownSequence = NativeMethods.GetClipboardSequenceNumber();
    }

    public void Dispose()
    {
        _retry.Stop(); _retry.Tick -= Retry;
        NativeMethods.UnregisterHotKey(_source.Handle, 1);
        NativeMethods.RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc); _source.Dispose();
    }
}
