using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Clippboard.Core;
using Clippboard.App.Design;
using Clippboard.App.Platform;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    private HwndSource? _inputSource;
    private bool _scrollRunning;
    private long _scrollFrameStamp, _gestureStamp;
    private TimeSpan? _scrollRenderTime;
    private double _scrollVelocity, _scrollResponse = .24, _frameSeconds, _gestureSeconds;
    private double? _committedScroll;
    internal bool ScrollRunning => _scrollRunning;
    internal double ScrollOffset => Math.Clamp((double)GetValue(ScrollPositionProperty), 0, NoteScroll?.ScrollableWidth ?? 0);
    public static readonly DependencyProperty ScrollPositionProperty = DependencyProperty.Register("ScrollPosition", typeof(double), typeof(RopeWindow),
        new PropertyMetadata(0.0, (d, _) => ((RopeWindow)d).ApplyScrollPosition()));

    private void AttachScrollInput(object? sender, EventArgs e)
    {
        _inputSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _inputSource?.AddHook(OnNativeScroll);
    }
    private void DetachScrollInput()
    {
        StopScroll();
        _inputSource?.RemoveHook(OnNativeScroll); _inputSource = null;
    }
    private nint OnNativeScroll(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != HorizontalWheel.Message || Retired || _closing || !IsVisible || HorizontalWheel.ControlDown(wParam)
            || SidebarHost.Visibility == Visibility.Visible || DetailHost.Visibility == Visibility.Visible) return 0;
        var point = Board.PointFromScreen(new Point(HorizontalWheel.ScreenX(lParam), HorizontalWheel.ScreenY(lParam)));
        if (!new Rect(Board.RenderSize).Contains(point)) return 0;
        var distance = HorizontalWheel.Distance(HorizontalWheel.Delta(wParam), HorizontalWheel.ScrollCharacters(), NoteScroll.ViewportWidth);
        if (distance == 0) return 0;
        GestureScroll(distance);
        handled = true; return 0;
    }

    private void ApplyScrollPosition()
    {
        if (NotesMotion is null || Retired) return;
        // Animate only rendering. ScrollViewer layout is committed once at rest,
        // while its native scrolling/accessibility support remains available.
        NotesMotion.X = NoteScroll.HorizontalOffset - ScrollOffset;
        RenderVisible(); SyncScroll();
    }
    private void ScrollTo(double value, int milliseconds = 240)
    {
        if (Retired || _closing || !double.IsFinite(value)) return;
        _scrollTarget = Math.Clamp(value, 0, NoteScroll.ScrollableWidth);
        if (!Animated)
        {
            StopScroll();
            SetCurrentValue(ScrollPositionProperty, _scrollTarget);
            CommitScroll(); return;
        }
        _scrollResponse = milliseconds / 1000.0;
        if (_scrollRunning) return;
        _scrollRunning = true; _scrollFrameStamp = Stopwatch.GetTimestamp(); _scrollRenderTime = null;
        CompositionTarget.Rendering += OnScrollFrame;
    }
    private void GestureScroll(double distance)
    {
        var now = Stopwatch.GetTimestamp();
        if (_gestureStamp != 0 && _scrollRunning)
        {
            var interval = Stopwatch.GetElapsedTime(_gestureStamp, now).TotalSeconds;
            _gestureSeconds = _gestureSeconds == 0 ? interval : (_gestureSeconds + interval) / 2;
        }
        else _gestureSeconds = 0;
        _gestureStamp = now;
        ScrollTo(_scrollTarget + distance);
        // Track the device's actual input cadence and observed render cadence.
        // OS momentum is already in the deltas; do not add another fling.
        if (_gestureSeconds > 0) _scrollResponse = Math.Min(_scrollResponse, Math.Max(_gestureSeconds, _frameSeconds) * 4);
    }
    private void OnScrollFrame(object? sender, EventArgs e)
    {
        if (Retired || _closing || !IsVisible) { StopScroll(); return; }
        var renderTime = ((RenderingEventArgs)e).RenderingTime;
        if (_scrollRenderTime == renderTime) return;
        _scrollRenderTime = renderTime;
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(_scrollFrameStamp, now).TotalSeconds;
        if (seconds <= 0) return;
        _scrollFrameStamp = now;
        _frameSeconds = _frameSeconds == 0 ? seconds : (_frameSeconds + seconds) / 2;
        var next = MotionMath.Spring(ScrollOffset, _scrollVelocity, _scrollTarget, seconds, 6 / _scrollResponse);
        var position = Math.Clamp(next.Position, 0, NoteScroll.ScrollableWidth);
        _scrollVelocity = position == next.Position ? next.Velocity : 0;
        // Commit at physical-pixel rest, rather than a wall-clock deadline which
        // can snap midway through motion after a delayed render frame.
        var pixel = 1 / VisualTreeHelper.GetDpi(this).DpiScaleX;
        var settled = Math.Abs(position - _scrollTarget) < pixel / 2 && Math.Abs(_scrollVelocity * _frameSeconds) < pixel / 2;
        SetCurrentValue(ScrollPositionProperty, settled ? _scrollTarget : position);
        if (!settled) return;
        StopScroll(); CommitScroll();
    }
    private void CommitScroll() { _committedScroll = _scrollTarget; NoteScroll.ScrollToHorizontalOffset(_scrollTarget); }
    private void StopScroll()
    {
        if (_scrollRunning) CompositionTarget.Rendering -= OnScrollFrame;
        _scrollRunning = false; _scrollVelocity = 0;
    }
    private void SyncScroll()
    {
        _scrollSync = true;
        try
        {
            HistoryScroll.Maximum = NoteScroll.ScrollableWidth; HistoryScroll.ViewportSize = NoteScroll.ViewportWidth;
            HistoryScroll.LargeChange = NoteScroll.ViewportWidth; HistoryScroll.Value = ScrollOffset;
        }
        finally { _scrollSync = false; }
        Navigation.Visibility = NoteScroll.ScrollableWidth > 1 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.HorizontalChange != 0)
        {
            // A queued commit may arrive after the next gesture has begun.
            // It updates the layout origin without interrupting new motion.
            if (_committedScroll is double committed && Math.Abs(NoteScroll.HorizontalOffset - committed) < .001) _committedScroll = null;
            else
            {
                _committedScroll = null; StopScroll(); _scrollTarget = NoteScroll.HorizontalOffset;
                SetCurrentValue(ScrollPositionProperty, _scrollTarget);
            }
        }
        if ((double)GetValue(ScrollPositionProperty) > NoteScroll.ScrollableWidth || _scrollTarget > NoteScroll.ScrollableWidth)
        {
            StopScroll(); _scrollTarget = NoteScroll.ScrollableWidth;
            SetCurrentValue(ScrollPositionProperty, _scrollTarget);
        }
        ApplyScrollPosition();
    }
    private void OnBoardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Notes is null) return;
        Notes.Width = Math.Max(e.NewSize.Width, 48 + _visible.Count * Pitch - (_visible.Count > 0 ? _appearance.NoteSpacing : 0));
        RenderVisible();
    }
    private void OnScrollbarChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_scrollSync || NoteScroll is null || Retired || _closing) return;
        StopScroll(); _scrollTarget = e.NewValue;
        SetCurrentValue(ScrollPositionProperty, e.NewValue);
        CommitScroll();
    }
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (SidebarHost.Visibility == Visibility.Visible || DetailHost.Visibility == Visibility.Visible || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var lines = SystemParameters.WheelScrollLines;
        var distance = e.Delta / 120.0 * (lines < 0 ? NoteScroll.ViewportWidth : lines * Pitch / 3);
        if (Math.Abs(e.Delta) < 120) GestureScroll(-distance); else ScrollTo(_scrollTarget - distance, 180);
        e.Handled = true;
    }
    private void OnPrevious(object sender, RoutedEventArgs e) => ScrollTo(_scrollTarget - Pitch);
    private void OnNext(object sender, RoutedEventArgs e) => ScrollTo(_scrollTarget + Pitch);
}
