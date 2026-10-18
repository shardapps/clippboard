using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Clippboard.App.Platform;

// Windows composites cached note pixels. Input stays with the accessible WPF
// owner underneath. Buffers and windows belong to one visible rope session.
internal sealed class LayeredSurface : Forms.NativeWindow, IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct PointI { internal int X, Y; internal PointI(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SizeI { internal int Width, Height; internal SizeI(int width, int height) { Width = width; Height = height; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { internal byte Operation, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, Bits; internal uint Compression, ImageSize;
        internal int XPixels, YPixels; internal uint Colors, Important;
    }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint window, nint destination, ref PointI point, ref SizeI size, nint source, ref PointI origin, uint color, ref Blend blend, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);

    private sealed class PixelsBuffer : IDisposable
    {
        private static readonly byte[] Zeros = new byte[4096];
        internal readonly int Width, Height, Stride, Bytes;
        internal readonly nint Dc, Pixels;
        private readonly nint _bitmap, _old;
        internal readonly Bitmap Image;
        private bool _disposed;
        internal PixelsBuffer(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            Width = width; Height = height; Stride = checked(width * 4); Bytes = checked(Stride * height);
            Dc = CreateCompatibleDC(0);
            var info = new BitmapInfo { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, Bits = 32 };
            _bitmap = CreateDIBSection(Dc, ref info, 0, out var pixels, 0, 0); Pixels = pixels;
            if (Dc == 0 || _bitmap == 0 || pixels == 0)
            {
                if (_bitmap != 0) DeleteObject(_bitmap); if (Dc != 0) DeleteDC(Dc);
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            _old = SelectObject(Dc, _bitmap);
            try { Image = new Bitmap(width, height, Stride, PixelFormat.Format32bppPArgb, Pixels); }
            catch { SelectObject(Dc, _old); DeleteObject(_bitmap); DeleteDC(Dc); throw; }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; Image.Dispose();
            for (var offset = 0; offset < Bytes; offset += Zeros.Length) Marshal.Copy(Zeros, 0, Pixels + offset, Math.Min(Zeros.Length, Bytes - offset));
            SelectObject(Dc, _old); DeleteObject(_bitmap); DeleteDC(Dc);
        }
    }

    private readonly PixelsBuffer _source;
    private readonly nint _owner;
    private PixelsBuffer? _rotated;
    private bool _disposed;
    private bool _shown;
    private (int Left, int Top, int Right, int Bottom)? _clip;
    private (int X, int Y)? _position;
    private (double A, double B, double C, double D)? _matrix;
    private byte? _alpha;
    private int _revision, _uploadedRevision = -1;
    internal nint Pixels => _source.Pixels;
    internal int SourceWidth => _source.Width;
    internal int SourceHeight => _source.Height;
    internal int Stride => _source.Stride;
    internal int SourceBytes => _source.Bytes;
    internal long PixelBytes => _source.Bytes + (long)(_rotated?.Bytes ?? 0);
    internal int Uploads { get; private set; }
    // Source and worst rotation count towards admission. The view separately
    // reserves its single shared WPF snapshot, never one snapshot per note.
    internal static long EstimatedBytes(int width, int height)
    {
        var side = (long)Math.Ceiling(Math.Sqrt((double)width * width + (double)height * height)) + 2;
        return checked((long)width * height * 4 + side * side * 4);
    }
    protected override void WndProc(ref Forms.Message message)
    {
        // HTTRANSPARENT routes to this UI thread's owner without the extra
        // owner repainting imposed by WS_EX_TRANSPARENT.
        if (message.Msg == 0x0084) { message.Result = -1; return; }
        if (message.Msg == 0x0021) { message.Result = 3; return; }
        // Inactive-wheel routing can address the cached HWND directly instead
        // of using hit testing. Keep high-resolution horizontal/vertical deltas
        // intact and send them to the existing scroll/input implementation.
        if (message.Msg is 0x020A or 0x020E && _owner != 0)
        { message.Result = SendMessage(_owner, message.Msg, message.WParam, message.LParam); return; }
        base.WndProc(ref message);
    }
    internal LayeredSurface(int width, int height, nint owner)
    {
        _owner = owner;
        _source = new PixelsBuffer(width, height);
        try
        {
            // A bare HWND has no extra Form/control/accessibility tree, font
            // defaults or per-note layout work. The WPF owner provides those.
            CreateHandle(new Forms.CreateParams
            {
                Parent = owner, Caption = "", X = -20000, Y = -20000, Width = width, Height = height,
                Style = unchecked((int)0x80000000), // WS_POPUP; show only after pixels are ready
                ExStyle = 0x00080000 | 0x00000080 | 0x08000000 // LAYERED | TOOLWINDOW | NOACTIVATE
            });
        }
        catch { if (Handle != 0) DestroyHandle(); _source.Dispose(); throw; }
    }
    internal void PixelsChanged() => ++_revision;
    internal void ReleaseRotation()
    {
        if (_matrix != (1.0, 0.0, 0.0, 1.0)) return;
        _rotated?.Dispose(); _rotated = null;
    }
    internal void Present(double screenX, double screenY, double a, double b, double c, double d, byte alpha,
        int clipLeft, int clipTop, int clipRight, int clipBottom)
    {
        static double Stable(double value) => Math.Round(value, 5);
        var matrix = (Stable(a), Stable(b), Stable(c), Stable(d));
        var transformed = matrix != (1.0, 0.0, 0.0, 1.0);
        var minX = transformed ? Math.Floor(Math.Min(Math.Min(0, a * SourceWidth), Math.Min(c * SourceHeight, a * SourceWidth + c * SourceHeight))) : 0;
        var minY = transformed ? Math.Floor(Math.Min(Math.Min(0, b * SourceWidth), Math.Min(d * SourceHeight, b * SourceWidth + d * SourceHeight))) : 0;
        var x = checked((int)Math.Round(screenX + minX)); var y = checked((int)Math.Round(screenY + minY));
        var redraw = _matrix != matrix || _uploadedRevision != _revision;
        var buffer = _source;
        if (transformed)
        {
            var side = checked((int)Math.Ceiling(Math.Sqrt((double)SourceWidth * SourceWidth + (double)SourceHeight * SourceHeight)) + 2);
            if (Math.Abs(a * a + b * b - 1) > .002 || Math.Abs(c * c + d * d - 1) > .002) throw new InvalidOperationException("Unsupported note transform.");
            _rotated ??= new PixelsBuffer(side, side); buffer = _rotated;
            if (redraw)
            {
                using var graphics = Graphics.FromImage(buffer.Image);
                graphics.CompositingMode = CompositingMode.SourceCopy; graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                using var transform = new Matrix((float)a, (float)b, (float)c, (float)d, (float)-minX, (float)-minY);
                graphics.Transform = transform;
                graphics.DrawImage(_source.Image, new Rectangle(0, 0, SourceWidth, SourceHeight), 0, 0, SourceWidth, SourceHeight, GraphicsUnit.Pixel);
            }
        }
        var clip = (Math.Clamp(clipLeft - x, 0, buffer.Width), Math.Clamp(clipTop - y, 0, buffer.Height),
            Math.Clamp(clipRight - x, 0, buffer.Width), Math.Clamp(clipBottom - y, 0, buffer.Height));
        if (_clip != clip)
        {
            var region = CreateRectRgn(clip.Item1, clip.Item2, Math.Max(clip.Item1, clip.Item3), Math.Max(clip.Item2, clip.Item4));
            if (region == 0 || SetWindowRgn(Handle, region, false) == 0)
            { if (region != 0) DeleteObject(region); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            _clip = clip;
        }
        if (redraw || _alpha != alpha)
        {
            var point = new PointI(x, y); var size = new SizeI(buffer.Width, buffer.Height); var origin = new PointI(0, 0); var blend = new Blend { Alpha = alpha, Format = 1 };
            if (!UpdateLayeredWindow(Handle, 0, ref point, ref size, buffer.Dc, ref origin, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _matrix = matrix; _alpha = alpha; _uploadedRevision = _revision; _position = (x, y); ++Uploads;
            if (!_shown) { ShowWindow(Handle, 4); _shown = true; } // SW_SHOWNOACTIVATE
        }
        else if (_position != (x, y))
        {
            // Keep cached pixels and ownership order; do not synchronously
            // repaint windows underneath every translated note.
            if (!SetWindowPos(Handle, 0, x, y, 0, 0, 0x261D)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _position = (x, y);
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; if (Handle != 0) DestroyHandle();
        _rotated?.Dispose(); _source.Dispose(); GC.SuppressFinalize(this);
    }
}
