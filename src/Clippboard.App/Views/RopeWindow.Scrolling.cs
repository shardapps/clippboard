using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Clippboard.App.Design;
using Clippboard.App.Platform;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    private HwndSource? _inputSource;
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
        ScrollTo(_scrollTarget + distance, 90);
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
        _scrollAnimating = true;
        var version = ++_scrollVersion;
        SmoothMotion.Move(this, ScrollPositionProperty, _scrollTarget, Animated, milliseconds);
        FinishScrollAsync(version, milliseconds);
    }
    private async void FinishScrollAsync(long version, int milliseconds)
    {
        await Task.Delay(Animated ? milliseconds * 2 + 40 : 1);
        if (version != _scrollVersion || Retired || _closing) return;
        SmoothMotion.Move(this, ScrollPositionProperty, _scrollTarget, false, 1);
        _scrollAnimating = false;
        NoteScroll.ScrollToHorizontalOffset(_scrollTarget);
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
        if (!_scrollAnimating && e.HorizontalChange != 0)
        {
            ++_scrollVersion; _scrollTarget = NoteScroll.HorizontalOffset;
            SmoothMotion.Move(this, ScrollPositionProperty, _scrollTarget, false, 1);
        }
        if (ScrollOffset > NoteScroll.ScrollableWidth)
        {
            ++_scrollVersion; _scrollAnimating = false; _scrollTarget = NoteScroll.ScrollableWidth;
            SmoothMotion.Move(this, ScrollPositionProperty, _scrollTarget, false, 1);
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
        ++_scrollVersion; _scrollAnimating = false; _scrollTarget = e.NewValue;
        SmoothMotion.Move(this, ScrollPositionProperty, e.NewValue, false, 1);
        NoteScroll.ScrollToHorizontalOffset(e.NewValue);
    }
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (SidebarHost.Visibility == Visibility.Visible || DetailHost.Visibility == Visibility.Visible || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var lines = SystemParameters.WheelScrollLines;
        var distance = e.Delta / 120.0 * (lines < 0 ? NoteScroll.ViewportWidth : lines * 100.0);
        ScrollTo(_scrollTarget - distance, Math.Abs(e.Delta) < 120 ? 90 : 180); e.Handled = true;
    }
    private void OnPrevious(object sender, RoutedEventArgs e) => ScrollTo(_scrollTarget - Pitch);
    private void OnNext(object sender, RoutedEventArgs e) => ScrollTo(_scrollTarget + Pitch);
}
