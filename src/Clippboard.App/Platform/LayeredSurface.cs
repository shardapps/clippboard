using System;
using System.ComponentModel;
using System.Collections.Generic;
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
    [DllImport("user32.dll")] private static extern nint BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] private static extern nint DeferWindowPos(nint batch, nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool EndDeferWindowPos(nint batch);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);

    internal sealed class PixelsBuffer : IDisposable
    {
        private static readonly byte[] Zeros = new byte[4096];
        internal readonly int Width, Height, Stride, Bytes;
        internal readonly nint Dc, Pixels;
        private readonly nint _bitmap, _old;
        private Bitmap? _image;
        internal Bitmap Image => _image ??= new Bitmap(Width, Height, Stride, PixelFormat.Format32bppPArgb, Pixels);
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
            if (_old == 0 || _old == -1)
            {
                var error = Marshal.GetLastWin32Error();
                DeleteObject(_bitmap); DeleteDC(Dc);
                throw new Win32Exception(error);
            }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _image?.Dispose();
            for (var offset = 0; offset < Bytes; offset += Zeros.Length) Marshal.Copy(Zeros, 0, Pixels + offset, Math.Min(Zeros.Length, Bytes - offset));
            SelectObject(Dc, _old); DeleteObject(_bitmap); DeleteDC(Dc);
        }
    }

    // UpdateLayeredWindow copies the pixels into the window. One dispatcher-owned
    // upload buffer can therefore serve every note, without a private copy at rest.
    internal sealed class UploadBuffer : IDisposable
    {
        private PixelsBuffer? _buffer;
        internal long Bytes => _buffer?.Bytes ?? 0;
        internal PixelsBuffer Get(int width, int height)
        {
            if (_buffer is { } existing && existing.Width >= width && existing.Height >= height) return existing;
            var nextWidth = Math.Max(width, _buffer?.Width ?? 0);
            var nextHeight = Math.Max(height, _buffer?.Height ?? 0);
            Dispose();
            return _buffer = new PixelsBuffer(nextWidth, nextHeight);
        }
        public void Dispose() { _buffer?.Dispose(); _buffer = null; }
    }

    // Group moves of already-uploaded notes into one window-manager update.
    // If a graphics/session configuration rejects batching, use ordinary moves.
    internal sealed class PositionBatch
    {
        private const uint MoveFlags = 0x261D; // NOSIZE | NOZORDER | NOREDRAW | NOACTIVATE | NOOWNERZORDER | NOSENDCHANGING | DEFERERASE
        private readonly List<(LayeredSurface Surface, int X, int Y)> _moves = new();
        private bool _supportsBatch = true;
        internal int Completed { get; private set; }

        internal void Add(LayeredSurface surface, int x, int y) => _moves.Add((surface, x, y));
        internal void Clear() => _moves.Clear();

        internal void Apply()
        {
            if (_moves.Count == 0) return;
            if (_supportsBatch && _moves.Count > 1)
            {
                var batch = BeginDeferWindowPos(_moves.Count);
                foreach (var (surface, x, y) in _moves)
                {
                    if (batch == 0) break;
                    batch = DeferWindowPos(batch, surface.Handle, 0, x, y, 0, 0, MoveFlags);
                }
                // A failing DeferWindowPos frees its batch. Only finish a valid handle.
                if (batch != 0 && EndDeferWindowPos(batch))
                {
                    foreach (var (surface, x, y) in _moves) surface._position = (x, y);
                    ++Completed;
                    return;
                }
                _supportsBatch = false;
            }
            foreach (var (surface, x, y) in _moves)
            {
                if (!SetWindowPos(surface.Handle, 0, x, y, 0, 0, MoveFlags)) throw new Win32Exception(Marshal.GetLastWin32Error());
                surface._position = (x, y);
            }
        }
    }

    private readonly nint _owner;
    private PixelsBuffer? _rotationSource;
    private bool _disposed;
    private bool _shown;
    private (int Left, int Top, int Right, int Bottom)? _clip;
    private (int X, int Y)? _position;
    private (double A, double B, double C, double D)? _matrix;
    private byte? _alpha;
    internal int SourceWidth { get; }
    internal int SourceHeight { get; }
    internal long PixelBytes => _rotationSource?.Bytes ?? 0;
    internal int Uploads { get; private set; }
    // Conservatively reserve the temporary rotation source and the largest
    // presented window. The shared snapshot/upload buffers are accounted separately.
    internal static int RotationScratchSide(int width, int height)
        => checked((int)Math.Ceiling(Math.Sqrt((double)width * width + (double)height * height)) + 2);
    internal static long EstimatedBytes(int width, int height)
    {
        var side = (long)RotationScratchSide(width, height);
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
        SourceWidth = width; SourceHeight = height;
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
        catch { if (Handle != 0) DestroyHandle(); throw; }
    }
    internal void Present(double screenX, double screenY, double a, double b, double c, double d, byte alpha,
        int clipLeft, int clipTop, int clipRight, int clipBottom, bool dirty, UploadBuffer upload, Action<PixelsBuffer> capture, PositionBatch positions)
    {
        static double Stable(double value) => Math.Round(value, 5);
        var matrix = (Stable(a), Stable(b), Stable(c), Stable(d));
        (a, b, c, d) = matrix;
        var transformed = matrix != (1.0, 0.0, 0.0, 1.0);
        var minX = transformed ? Math.Floor(Math.Min(Math.Min(0, a * SourceWidth), Math.Min(c * SourceHeight, a * SourceWidth + c * SourceHeight))) : 0;
        var minY = transformed ? Math.Floor(Math.Min(Math.Min(0, b * SourceWidth), Math.Min(d * SourceHeight, b * SourceWidth + d * SourceHeight))) : 0;
        var x = checked((int)Math.Round(screenX + minX)); var y = checked((int)Math.Round(screenY + minY));
        var redraw = !_shown || dirty || _matrix != matrix || _alpha != alpha;
        var width = SourceWidth; var height = SourceHeight;
        if (transformed)
        {
            if (Math.Abs(a * a + b * b - 1) > .002 || Math.Abs(c * c + d * d - 1) > .002)
                throw new InvalidOperationException("Unsupported note transform.");
            width = checked((int)(Math.Ceiling(Math.Max(Math.Max(0, a * SourceWidth), Math.Max(c * SourceHeight, a * SourceWidth + c * SourceHeight))) - minX));
            height = checked((int)(Math.Ceiling(Math.Max(Math.Max(0, b * SourceWidth), Math.Max(d * SourceHeight, b * SourceWidth + d * SourceHeight))) - minY));
        }
        var clip = (Math.Clamp(clipLeft - x, 0, width), Math.Clamp(clipTop - y, 0, height),
            Math.Clamp(clipRight - x, 0, width), Math.Clamp(clipBottom - y, 0, height));
        if (_clip != clip)
        {
            var region = CreateRectRgn(clip.Item1, clip.Item2, Math.Max(clip.Item1, clip.Item3), Math.Max(clip.Item2, clip.Item4));
            if (region == 0 || SetWindowRgn(Handle, region, false) == 0)
            { if (region != 0) DeleteObject(region); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            _clip = clip;
        }
        if (redraw)
        {
            // The rotation scratch grows once for this geometry, but only the
            // tight bounding rectangle is uploaded to Windows for each angle.
            var side = transformed ? RotationScratchSide(SourceWidth, SourceHeight) : 0;
            // A clean rotation source already contains the upright note.
            // Use it once on settling, then release it instead of redrawing text.
            var upright = !transformed && !dirty ? _rotationSource : null;
            var buffer = upright ?? upload.Get(Math.Max(width, side), Math.Max(height, side));
            if (transformed)
            {
                var missing = _rotationSource is null;
                var source = _rotationSource ??= new PixelsBuffer(SourceWidth, SourceHeight);
                if (dirty || missing) capture(source);
                using var graphics = Graphics.FromImage(buffer.Image);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.FillRectangle(Brushes.Transparent, 0, 0, width, height);
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                using var transform = new Matrix((float)a, (float)b, (float)c, (float)d, (float)-minX, (float)-minY);
                graphics.Transform = transform;
                graphics.DrawImage(source.Image, new Rectangle(0, 0, SourceWidth, SourceHeight), 0, 0, SourceWidth, SourceHeight, GraphicsUnit.Pixel);
            }
            else if (upright is null) capture(buffer);
            var point = new PointI(x, y); var size = new SizeI(width, height); var origin = new PointI(0, 0); var blend = new Blend { Alpha = alpha, Format = 1 };
            if (!UpdateLayeredWindow(Handle, 0, ref point, ref size, buffer.Dc, ref origin, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _matrix = matrix; _alpha = alpha; _position = (x, y); ++Uploads;
            if (!transformed) { _rotationSource?.Dispose(); _rotationSource = null; }
            if (!_shown) { ShowWindow(Handle, 4); _shown = true; } // SW_SHOWNOACTIVATE
        }
        else if (_position != (x, y)) positions.Add(this, x, y);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; if (Handle != 0) DestroyHandle();
        _rotationSource?.Dispose(); GC.SuppressFinalize(this);
    }
}
