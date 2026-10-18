using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Clippboard.App.Design;
using Clippboard.App.Platform;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    private const double NativeNotePadding = 8;
    private bool _nativeNotes, _snapshotting, _nativeFrame;
    private TimeSpan? _nativeRenderTime;
    internal int NativeSnapshotCount { get; private set; }
    private readonly Dictionary<Grid, NativeNote> _nativeSurfaces = new();
    private readonly Dictionary<DependencyObject, Grid?> _nativeMotions = new();
    private RenderTargetBitmap? _nativeSnapshot;
    private long? _syntheticNativeHeadroom;
    private long NativeHeadroom => _syntheticNativeHeadroom ?? MemoryAvailability.AvailableBytes();
    private sealed class NativeNote(LayeredSurface surface, double dpiX, double dpiY) : IDisposable
    {
        internal readonly LayeredSurface Surface = surface;
        internal readonly double DpiX = dpiX, DpiY = dpiY;
        internal readonly List<Action> Unsubscribe = new();
        internal bool Dirty = true;
        internal long Reservation => LayeredSurface.EstimatedBytes(Surface.SourceWidth, Surface.SourceHeight);
        public void Dispose() { foreach (var remove in Unsubscribe) remove(); Unsubscribe.Clear(); Surface.Dispose(); }
    }
    internal long NativeSurfaceBytes => _nativeSurfaces.Values.Sum(note => note.Surface.PixelBytes);
    internal long NativeReservedBytes => _nativeSurfaces.Values.Sum(note => note.Reservation) + (_nativeSnapshot is null ? 0 : (long)_nativeSnapshot.PixelWidth * _nativeSnapshot.PixelHeight * 4);
    internal int NativeSurfaceUploads => _nativeSurfaces.Values.Sum(note => note.Surface.Uploads);
    internal bool NativeFrameRunning => _nativeFrame;
    internal bool NativeNotesActive => _nativeNotes;
    internal string? NativeFailure { get; private set; }
    internal nint[] NativeSurfaceHandles => _nativeSurfaces.Values.Select(note => note.Surface.Handle).ToArray();
    internal void DiagnosticNativePressure(long available)
    {
        if (!_preview) throw new InvalidOperationException("Synthetic mode required.");
        _syntheticNativeHeadroom = available; CheckNativeBudget();
    }
    internal void DiagnosticFlushNativeNotes()
    {
        if (!_preview) throw new InvalidOperationException("Synthetic mode required.");
        UpdateLayout(); SyncNativeNotes();
    }
    internal uint DiagnosticNativePixel(Grid card, double x, double y)
    {
        if (!_preview) throw new InvalidOperationException("Synthetic mode required.");
        var note = _nativeSurfaces[card];
        var px = Math.Clamp((int)Math.Round((x + NativeNotePadding) * note.DpiX), 0, note.Surface.SourceWidth - 1);
        var py = Math.Clamp((int)Math.Round((y + NativeNotePadding) * note.DpiY), 0, note.Surface.SourceHeight - 1);
        return unchecked((uint)System.Runtime.InteropServices.Marshal.ReadInt32(note.Surface.Pixels, py * note.Surface.Stride + px * 4));
    }

    internal void EnableNativeNotes()
    {
        if (_nativeNotes) return;
        if (RenderOptions.ProcessRenderMode != RenderMode.SoftwareOnly) throw new InvalidOperationException("Retained notes require software WPF chrome.");
        _nativeNotes = true; Notes.Opacity = 0;
        SmoothMotion.Changed += OnNativeMotion;
        SizeChanged += NativeLayoutChanged; LocationChanged += NativeLocationChanged;
        var visibility = DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(Border));
        visibility.AddValueChanged(SidebarHost, NativePanelChanged); visibility.AddValueChanged(DetailHost, NativePanelChanged);
        IsVisibleChanged += NativeVisibilityChanged;
        Closed += (_, _) =>
        {
            SmoothMotion.Changed -= OnNativeMotion;
            visibility.RemoveValueChanged(SidebarHost, NativePanelChanged); visibility.RemoveValueChanged(DetailHost, NativePanelChanged);
            StopNativeFrames(); ClearNativeNotes(); _nativeMotions.Clear();
        };
        RequestNativeFrame();
    }
    private void NativeLayoutChanged(object sender, SizeChangedEventArgs e) => RequestNativeFrame();
    private void NativeLocationChanged(object? sender, EventArgs e) => RequestNativeFrame();
    private void NativeVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) { StopNativeFrames(); ClearNativeNotes(); _nativeMotions.Clear(); }
        else RequestNativeFrame();
    }
    private void NativePanelChanged(object? sender, EventArgs e)
    {
        if (!_nativeNotes) return;
        // Owned native windows must never sit above a settings/detail panel.
        if (SidebarHost.Visibility == Visibility.Visible || DetailHost.Visibility == Visibility.Visible)
        { ClearNativeNotes(); Notes.Opacity = 1; StopNativeFrames(); _nativeMotions.Clear(); }
        else { Notes.Opacity = 0; InvalidateNativeNotes(); RequestNativeFrame(); }
    }
    private void ClearNativeNotes()
    {
        foreach (var note in _nativeSurfaces.Values) note.Dispose();
        _nativeSurfaces.Clear();
        _nativeSnapshot?.Clear(); _nativeSnapshot = null;
    }
    private void StopNativeFrames()
    {
        if (_nativeFrame) CompositionTarget.Rendering -= OnNativeFrame;
        _nativeFrame = false;
        _nativeRenderTime = null;
        foreach (var note in _nativeSurfaces.Values) note.Surface.ReleaseRotation();
    }
    private void RequestNativeFrame()
    {
        if (!_nativeNotes || _snapshotting || _nativeFrame || Retired || !IsVisible || SidebarHost.Visibility == Visibility.Visible || DetailHost.Visibility == Visibility.Visible) return;
        _nativeFrame = true; CompositionTarget.Rendering += OnNativeFrame;
    }
    private static bool HasAnimation(DependencyObject target) => target switch { Animatable value => value.HasAnimatedProperties, UIElement element => element.HasAnimatedProperties, _ => false };
    private void OnNativeFrame(object? sender, EventArgs e)
    {
        if (!_nativeNotes || Retired || !IsVisible || SidebarHost.Visibility == Visibility.Visible || DetailHost.Visibility == Visibility.Visible) { StopNativeFrames(); return; }
        var time = ((RenderingEventArgs)e).RenderingTime;
        if (_nativeRenderTime == time) return;
        _nativeRenderTime = time;
        foreach (var (target, card) in _nativeMotions.ToArray())
        {
            if (card is not null) InvalidateNativeNote(card);
            if (!HasAnimation(target)) _nativeMotions.Remove(target);
        }
        SyncNativeNotes();
        if (_nativeMotions.Count == 0 && !_scrollRunning) StopNativeFrames();
    }
    private void OnNativeMotion(DependencyObject target)
    {
        if (!Dispatcher.CheckAccess() || !_nativeNotes || _snapshotting || Retired) return;
        if (ReferenceEquals(target, Slide) || ReferenceEquals(target, Rope)) { _nativeMotions[target] = null; RequestNativeFrame(); return; }
        foreach (var card in _cards.Values)
        {
            if (ContainsTransform(card.RenderTransform, target)) { _nativeMotions[target] = null; RequestNativeFrame(); return; }
            if (ReferenceEquals(target, card) || ContainsVisualMotion(card, target))
            { _nativeMotions[target] = card; InvalidateNativeNote(card); RequestNativeFrame(); return; }
        }
    }
    private static bool ContainsTransform(Transform transform, DependencyObject target)
        => ReferenceEquals(transform, target) || transform is TransformGroup group && group.Children.Any(child => ContainsTransform(child, target));
    private static bool ContainsVisualMotion(DependencyObject visual, DependencyObject target)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
        {
            var child = VisualTreeHelper.GetChild(visual, index);
            if (ReferenceEquals(child, target) || child is UIElement element && ContainsTransform(element.RenderTransform, target) || ContainsVisualMotion(child, target)) return true;
        }
        return false;
    }
    private void InvalidateNativeNote(Grid card)
    {
        if (_nativeSurfaces.TryGetValue(card, out var note)) note.Dirty = true;
        RequestNativeFrame();
    }
    private void InvalidateNativeNotes()
    {
        foreach (var note in _nativeSurfaces.Values) note.Dirty = true;
        RequestNativeFrame();
    }
    private void ReleaseNativeNote(Grid card)
    {
        if (_nativeSurfaces.Remove(card, out var note)) note.Dispose();
        foreach (var target in _nativeMotions.Where(pair => ReferenceEquals(pair.Value, card)).Select(pair => pair.Key).ToArray()) _nativeMotions.Remove(target);
    }
    private void FailNativeNotes()
    {
        _nativeNotes = false; StopNativeFrames(); ClearNativeNotes(); _nativeMotions.Clear();
        SmoothMotion.Changed -= OnNativeMotion; Notes.Opacity = 1;
        SetStatus("Using the compatible view for this session. Your notes are available.");
    }
    private void CheckNativeBudget()
    {
        if (!_nativeNotes || _nativeSurfaces.Count == 0) return;
        var demand = NativeReservedBytes;
        if (demand > MemoryAvailability.RetainedSurfaceBudget(demand, NativeHeadroom)) FailNativeNotes();
    }
    private NativeNote CreateNativeNote(Grid card, int width, int height, DpiScale dpi, nint owner)
    {
        var reservation = LayeredSurface.EstimatedBytes(width, height);
        var demand = checked(NativeReservedBytes + reservation + (_nativeSnapshot is null ? (long)width * height * 4 : 0));
        if (demand > MemoryAvailability.RetainedSurfaceBudget(demand, NativeHeadroom)) throw new InvalidOperationException("Note surface exceeds available headroom.");
        var surface = new LayeredSurface(width, height, owner);
        NativeNote? note = null;
        try
        {
            note = new NativeNote(surface, dpi.DpiScaleX, dpi.DpiScaleY);
            void Observe(DependencyObject target, DependencyProperty property)
            {
                var descriptor = DependencyPropertyDescriptor.FromProperty(property, target.GetType());
                EventHandler changed = (_, _) => InvalidateNativeNote(card);
                descriptor.AddValueChanged(target, changed); note.Unsubscribe.Add(() => descriptor.RemoveValueChanged(target, changed));
            }
            void ObserveButtons(DependencyObject visual)
            {
                if (visual is Button button) { Observe(button, IsMouseOverProperty); Observe(button, ButtonBase.IsPressedProperty); Observe(button, IsKeyboardFocusWithinProperty); }
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++) ObserveButtons(VisualTreeHelper.GetChild(visual, index));
            }
            ObserveButtons(card); Observe(card, OpacityProperty);
            return note;
        }
        catch { if (note is null) surface.Dispose(); else note.Dispose(); throw; }
    }
    private void CaptureNativeNote(Grid card, NativeNote note)
    {
        var transform = card.RenderTransform; card.RenderTransform = Transform.Identity;
        try
        {
            var viewbox = new Rect(-NativeNotePadding, -NativeNotePadding, note.Surface.SourceWidth / note.DpiX, note.Surface.SourceHeight / note.DpiY);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawRectangle(new VisualBrush(card) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = viewbox, Stretch = Stretch.Fill }, null, new Rect(viewbox.Size));
            if (_nativeSnapshot is null || _nativeSnapshot.PixelWidth != note.Surface.SourceWidth || _nativeSnapshot.PixelHeight != note.Surface.SourceHeight
                || _nativeSnapshot.DpiX != note.DpiX * 96 || _nativeSnapshot.DpiY != note.DpiY * 96)
            {
                _nativeSnapshot?.Clear();
                _nativeSnapshot = new RenderTargetBitmap(note.Surface.SourceWidth, note.Surface.SourceHeight, note.DpiX * 96, note.DpiY * 96, PixelFormats.Pbgra32);
            }
            _nativeSnapshot.Clear(); _nativeSnapshot.Render(visual);
            _nativeSnapshot.CopyPixels(Int32Rect.Empty, note.Surface.Pixels, note.Surface.SourceBytes, note.Surface.Stride);
            _nativeSnapshot.Clear();
            note.Surface.PixelsChanged(); note.Dirty = false;
            ++NativeSnapshotCount;
        }
        finally { card.RenderTransform = transform; }
    }
    private void SyncNativeNotes()
    {
        if (!_nativeNotes || _snapshotting || !IsVisible || Retired || SidebarHost.Visibility == Visibility.Visible || DetailHost.Visibility == Visibility.Visible) return;
        _snapshotting = true;
        try
        {
            foreach (var card in _nativeSurfaces.Keys.Where(card => !_cards.Values.Contains(card)).ToArray()) ReleaseNativeNote(card);
            var dpi = VisualTreeHelper.GetDpi(this); var owner = new WindowInteropHelper(this).Handle;
            var clipStart = NoteScroll.PointToScreen(new Point(0, 0));
            var clipEnd = NoteScroll.PointToScreen(new Point(NoteScroll.ActualWidth, NoteScroll.ActualHeight));
            var rootStart = PointToScreen(new Point(0, 0)); var rootEnd = PointToScreen(new Point(ActualWidth, ActualHeight));
            var left = (int)Math.Ceiling(Math.Max(clipStart.X, rootStart.X)); var top = (int)Math.Ceiling(Math.Max(clipStart.Y, rootStart.Y));
            var right = (int)Math.Floor(Math.Min(clipEnd.X, rootEnd.X)); var bottom = (int)Math.Floor(Math.Min(clipEnd.Y, rootEnd.Y));
            foreach (var card in _cards.Values)
            {
                if (card.ActualWidth <= 0 || card.ActualHeight <= 0) continue;
                var width = checked((int)Math.Ceiling((card.ActualWidth + 2 * NativeNotePadding) * dpi.DpiScaleX));
                var height = checked((int)Math.Ceiling((card.ActualHeight + 2 * NativeNotePadding) * dpi.DpiScaleY));
                if (_nativeSurfaces.TryGetValue(card, out var note) && (note.Surface.SourceWidth != width || note.Surface.SourceHeight != height || note.DpiX != dpi.DpiScaleX || note.DpiY != dpi.DpiScaleY)) { ReleaseNativeNote(card); note = null; }
                if (note is null) { note = CreateNativeNote(card, width, height, dpi, owner); _nativeSurfaces.Add(card, note); }
                if (note.Dirty) CaptureNativeNote(card, note);
                // PointToScreen rounds through native POINT coordinates. Taking
                // three separately rounded samples fabricates scale/shear during
                // fractional scrolling. Keep the transform in doubles and round
                // only the final window position in LayeredSurface.Present.
                var toWindow = card.TransformToAncestor(this);
                Point ToPhysical(Point point)
                {
                    var local = toWindow.Transform(point);
                    return new Point(rootStart.X + local.X * dpi.DpiScaleX, rootStart.Y + local.Y * dpi.DpiScaleY);
                }
                var origin = ToPhysical(new Point(-NativeNotePadding, -NativeNotePadding));
                var px = ToPhysical(new Point(-NativeNotePadding + width / dpi.DpiScaleX, -NativeNotePadding));
                var py = ToPhysical(new Point(-NativeNotePadding, -NativeNotePadding + height / dpi.DpiScaleY));
                note.Surface.Present(origin.X, origin.Y, (px.X - origin.X) / width, (px.Y - origin.Y) / width,
                    (py.X - origin.X) / height, (py.Y - origin.Y) / height, 255, left, top, right, bottom);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or OverflowException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        { if (_preview) NativeFailure = ex.GetType().Name + ": " + ex.Message; FailNativeNotes(); }
        finally { _snapshotting = false; }
    }
}
