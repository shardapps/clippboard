using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Clippboard.App.Design;
using Clippboard.App.Services;
using Clippboard.Core;

namespace Clippboard.App.Views;

public partial class RopeWindow : Window
{
    private readonly HistoryStore _store;
    private readonly PreviewCache _cache;
    private readonly bool _preview;
    private AppSettings _settings;
    private IReadOnlyList<ClipSummary> _entries = [];
    private List<ClipSummary> _visible = [];
    private readonly Dictionary<int, Grid> _cards = new();
    private readonly Dictionary<string, BitmapSource> _demoImages = new();
    private CancellationTokenSource _loads = new();
    private int _renderVersion;
    private bool _closing, _starredOnly, _scrollSync;
    private long _scrollVersion;
    public event Func<ClipSummary, Task>? Selected;
    public event Action? PauseRequested;
    public event Action? SettingsRequested;
    public event Action<ClipSummary>? ExpandRequested;
    internal int RenderedNoteCount => _cards.Count;
    internal long CachedImageBytes => _cache.RetainedBytes;
    private double Pitch => _settings.NoteWidth + _settings.NoteSpacing;
    private bool Animated => _settings.Animate && SystemParameters.ClientAreaAnimation;

    public RopeWindow(HistoryStore store, bool preview = false, AppSettings? settings = null)
    {
        _store = store; _preview = preview; _settings = (settings ?? new()).Normalize();
        _cache = new PreviewCache(store);
        InitializeComponent();
        ApplyAppearance(_settings);
        Rope.ShapeChanged += PositionNotes;
        Closed += (_, _) => { _loads.Cancel(); _loads.Dispose(); _cache.Clear(); };
    }
    public void ApplyAppearance(AppSettings settings)
    {
        _settings = settings.Normalize();
        ThemeManager.Apply(_settings.Theme, Resources);
        _cache.BudgetBytes = _settings.PreviewCacheMiB * 1024L * 1024;
        _cache.Clear(); SetPaused(_settings.CapturePaused);
    }
    public void SetStatus(string message) => Status.Text = message;
    public void SetPaused(bool paused)
    {
        PauseButton.Content = new IconView { Symbol = paused ? "play" : "pause" };
        PauseButton.ToolTip = paused ? "Resume capture" : "Pause capture";
        System.Windows.Automation.AutomationProperties.SetName(PauseButton, paused ? "Resume capture" : "Pause capture");
    }
    public async Task OpenAsync(nint target)
    {
        if (_closing) return;
        var bounds = System.Windows.Forms.Screen.FromHandle(target).WorkingArea;
        Slide.BeginAnimation(TranslateTransform.YProperty, null); Slide.Y = -600;
        _starredOnly = _settings.OpenStarredOnly;
        Show();
        SetWindowPos(new WindowInteropHelper(this).Handle, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0014);
        Activate(); UpdateLayout(); await RefreshAsync();
        if (!IsVisible || _closing) return;
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Animated ? 680 : 1))
        { EasingFunction = new SpringEase { Bounce = false } });
        Rope.BeginAnimation(RopeVisual.SagProperty, new DoubleAnimation(52, 24, TimeSpan.FromMilliseconds(Animated ? 880 : 1))
        { EasingFunction = new SpringEase { Bounce = _settings.PlayfulMotion } });
        if (Animated && _settings.PlayfulMotion) AnimateNotes();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    public async Task CloseRopeAsync()
    {
        if (!IsVisible || _closing) return;
        _closing = true; ++_renderVersion; CancelLoads();
        SidebarHost.Visibility = DetailHost.Visibility = Visibility.Collapsed;
        SidebarHost.Child = DetailHost.Child = null;
        var duration = Animated ? 280 : 1;
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-600, TimeSpan.FromMilliseconds(duration))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        await Task.Delay(duration);
        Hide(); Notes.Children.Clear(); _cards.Clear(); _cache.Clear();
        Slide.BeginAnimation(TranslateTransform.YProperty, null); Slide.Y = -600;
        Rope.BeginAnimation(RopeVisual.SagProperty, null); _closing = false;
    }
    private void CancelLoads() { _loads.Cancel(); _loads.Dispose(); _loads = new(); }
    public async Task RefreshAsync()
    {
        var version = ++_renderVersion;
        var entries = await Task.Run(() => _store.ListAsync());
        if (version != _renderVersion || _closing) return;
        _entries = entries; RebuildNotes();
    }
    private void RebuildNotes()
    {
        CancelLoads(); Notes.Children.Clear(); _cards.Clear();
        _visible = _entries.Where(x => !_starredOnly || x.Pinned)
            .OrderByDescending(x => _settings.StarredFirst && x.Pinned).ThenByDescending(x => x.CopiedAt).ToList();
        AllButton.SetResourceReference(BackgroundProperty, !_starredOnly ? "SelectedSurface" : "Chrome");
        StarredButton.SetResourceReference(BackgroundProperty, _starredOnly ? "SelectedSurface" : "Chrome");
        EmptyState.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _starredOnly ? "Your favourites live here." : "Room for your next idea.";
        EmptyHint.Text = _starredOnly ? "Star a note to find it quickly." : "Copy something. We'll hang it here.";
        Notes.Width = Math.Max(NoteScroll.ViewportWidth, 48 + _visible.Count * Pitch - (_visible.Count > 0 ? _settings.NoteSpacing : 0));
        SetStatus($"{_visible.Count} {(_starredOnly ? "starred clips" : "clips")} · {_settings.RetentionDays}-day history · Esc to tuck away");
        UpdateLayout(); RenderVisible(); SyncScroll();
    }
    private void RenderVisible()
    {
        if (!IsVisible || _closing || NoteScroll.ViewportWidth <= 0) return;
        var range = MotionMath.VisibleRange(_visible.Count, Math.Max(0, NoteScroll.HorizontalOffset - 24), NoteScroll.ViewportWidth, Pitch);
        foreach (var index in _cards.Keys.Where(i => i < range.First || i > range.Last).ToArray())
        { Notes.Children.Remove(_cards[index]); _cards.Remove(index); }
        for (var i = range.First; i <= range.Last; i++)
        {
            if (_cards.ContainsKey(i)) continue;
            var card = BuildNote(_visible[i]); _cards[i] = card; Notes.Children.Add(card);
            Canvas.SetLeft(card, 24 + i * Pitch);
            if (_visible[i].Kind == ClipKind.Image) _ = LoadThumbnailAsync(_visible[i], card, _loads.Token);
        }
        PositionNotes();
    }
    private void PositionNotes()
    {
        foreach (var (index, card) in _cards)
        {
            var center = 24 + index * Pitch + _settings.NoteWidth / 2 - NoteScroll.HorizontalOffset;
            Canvas.SetTop(card, Rope.HeightAt(center) - 8);
        }
    }
    private async Task LoadThumbnailAsync(ClipSummary entry, Grid card, CancellationToken cancellation)
    {
        try
        {
            var bitmap = _demoImages.GetValueOrDefault(entry.Id) ?? await _cache.GetAsync(entry.Id, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!Notes.Children.Contains(card)) return;
            if (card.Tag is Button select && bitmap is not null) select.Content = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 215 };
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or System.Security.Cryptography.CryptographicException)
        { if (!cancellation.IsCancellationRequested) SetStatus("An image preview is unavailable. Expand it to try again."); }
    }
    private Grid BuildNote(ClipSummary entry)
    {
        var wrapper = new Grid { Width = _settings.NoteWidth, Height = 330, RenderTransformOrigin = new Point(.5, 0) };
        wrapper.MouseDown += OnInsideClick;
        var paper = new Border { CornerRadius = new CornerRadius(22), Margin = new Thickness(0, 23, 0, 0), Padding = new Thickness(21, 25, 21, 16), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Opacity = .12 } };
        paper.SetResourceReference(Border.BackgroundProperty, "NoteSurface"); paper.SetResourceReference(Border.BorderBrushProperty, "Outline");
        var body = new Grid(); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new()); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var label = new TextBlock { Text = entry.Kind.ToString().ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(label);
        var select = new Button { Padding = new Thickness(0), Margin = new Thickness(0, 14, 0, 14), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top, ToolTip = "Restore this clip · Shift-click to copy without pasting",
            Content = new TextBlock { Text = entry.Preview, FontSize = _settings.TextSize, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 210 } };
        System.Windows.Automation.AutomationProperties.SetName(select, $"Restore {entry.Kind}: {entry.Preview}");
        select.Click += async (_, _) => { if (Selected is not null) await Selected(entry); };
        Grid.SetRow(select, 1); body.Children.Add(select); wrapper.Tag = select;
        var footer = new Grid(); footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var date = new TextBlock { Text = entry.CopiedAt.ToLocalTime().ToString("MMM d · HH:mm"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        date.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); footer.Children.Add(date);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        var star = IconButton(entry.Pinned ? "star" : "star-outline", entry.Pinned ? "Unstar clip" : "Star clip");
        if (entry.Pinned) ((IconView)star.Content).SetResourceReference(IconView.InkProperty, "StarColor");
        star.Click += async (_, _) => await ChangeNoteAsync(entry, false); actions.Children.Add(star);
        var expand = IconButton("expand", "Expand and resize note"); expand.Click += (_, _) => ExpandRequested?.Invoke(entry); actions.Children.Add(expand);
        var delete = IconButton("close", "Delete clip"); delete.Click += async (_, _) => await ChangeNoteAsync(entry, true); actions.Children.Add(delete);
        Grid.SetRow(footer, 2); body.Children.Add(footer); paper.Child = body; wrapper.Children.Add(paper);
        var peg = new Border { Width = 17, Height = 47, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromRgb(189, 151, 108)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(139, 103, 68)), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
            Child = new Border { Height = 2, Background = new SolidColorBrush(Color.FromRgb(139, 103, 68)), Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center } };
        wrapper.Children.Add(peg); return wrapper;
    }
    internal static Button IconButton(string symbol, string name)
    {
        var button = new Button { Content = new IconView { Symbol = symbol, Width = 18, Height = 18 }, ToolTip = name, Padding = new Thickness(7), Margin = new Thickness(0), MinWidth = 32, MinHeight = 34 };
        System.Windows.Automation.AutomationProperties.SetName(button, name); return button;
    }
    private async Task ChangeNoteAsync(ClipSummary entry, bool delete)
    {
        try
        {
            if (_preview) { _entries = _entries.Where(x => !delete || x.Id != entry.Id).Select(x => x.Id == entry.Id ? x with { Pinned = !x.Pinned } : x).ToArray(); RebuildNotes(); return; }
            await Task.Run(async () => { if (delete) await _store.DeleteAsync(entry.Id); else await _store.SetPinnedAsync(entry.Id, !entry.Pinned); });
            _cache.Remove(entry.Id); await RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { SetStatus("Could not update this note. Check disk space and try again."); }
    }
    private void AnimateNotes()
    {
        foreach (var (index, card) in _cards)
        {
            var rotation = new RotateTransform(); var vertical = new TranslateTransform();
            var group = new TransformGroup(); group.Children.Add(rotation); group.Children.Add(vertical); card.RenderTransform = group;
            var angles = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(index % 8 * 25) };
            var direction = index % 2 == 0 ? 1 : -1;
            foreach (var (ms, angle) in new[] { (0, -2.8), (180, 2.2), (400, -1.1), (620, .5), (870, 0.0) })
                angles.KeyFrames.Add(new SplineDoubleKeyFrame(angle * direction, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), new KeySpline(.3, 0, .3, 1)));
            rotation.BeginAnimation(RotateTransform.AngleProperty, angles);
            vertical.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(850)) { EasingFunction = new SpringEase { Bounce = true } });
        }
    }
    public static readonly DependencyProperty ScrollPositionProperty = DependencyProperty.Register("ScrollPosition", typeof(double), typeof(RopeWindow),
        new PropertyMetadata(0.0, (d, e) => ((RopeWindow)d).NoteScroll.ScrollToHorizontalOffset((double)e.NewValue)));
    private void ScrollTo(double value)
    {
        var version = ++_scrollVersion; var start = NoteScroll.HorizontalOffset;
        BeginAnimation(ScrollPositionProperty, null); SetValue(ScrollPositionProperty, start);
        var animation = new DoubleAnimation(Math.Clamp(value, 0, NoteScroll.ScrollableWidth), TimeSpan.FromMilliseconds(Animated ? 230 : 1)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) => { if (version != _scrollVersion) return; var end = (double)GetValue(ScrollPositionProperty); BeginAnimation(ScrollPositionProperty, null); SetValue(ScrollPositionProperty, end); };
        BeginAnimation(ScrollPositionProperty, animation);
    }
    private void SyncScroll()
    {
        _scrollSync = true; HistoryScroll.Maximum = NoteScroll.ScrollableWidth; HistoryScroll.ViewportSize = NoteScroll.ViewportWidth;
        HistoryScroll.LargeChange = NoteScroll.ViewportWidth; HistoryScroll.Value = NoteScroll.HorizontalOffset; _scrollSync = false;
        Navigation.Visibility = NoteScroll.ScrollableWidth > 1 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) { RenderVisible(); SyncScroll(); }
    private void OnBoardSizeChanged(object sender, SizeChangedEventArgs e) { if (Notes is null) return; Notes.Width = Math.Max(e.NewSize.Width, 48 + _visible.Count * Pitch - (_visible.Count > 0 ? _settings.NoteSpacing : 0)); RenderVisible(); }
    private void OnScrollbarChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (_scrollSync || NoteScroll is null) return; ++_scrollVersion; BeginAnimation(ScrollPositionProperty, null); NoteScroll.ScrollToHorizontalOffset(e.NewValue); }
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    { if (SidebarHost.Visibility == Visibility.Visible || DetailHost.Visibility == Visibility.Visible) return; ScrollTo(NoteScroll.HorizontalOffset - e.Delta * 2.5); e.Handled = true; }
    private void OnPrevious(object sender, RoutedEventArgs e) => ScrollTo(NoteScroll.HorizontalOffset - Pitch);
    private void OnNext(object sender, RoutedEventArgs e) => ScrollTo(NoteScroll.HorizontalOffset + Pitch);
    private void OnAll(object sender, RoutedEventArgs e) { _starredOnly = false; RebuildNotes(); ScrollTo(0); }
    private void OnStarred(object sender, RoutedEventArgs e) { _starredOnly = true; RebuildNotes(); ScrollTo(0); }
    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private async void OnOutsideClick(object sender, MouseButtonEventArgs e) => await CloseRopeAsync();
    private void OnInsideClick(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private async void OnClose(object sender, RoutedEventArgs e) => await CloseRopeAsync();
    private async void OnDeactivated(object? sender, EventArgs e) { if (!_preview) await CloseRopeAsync(); }
    private void OnPause(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; if (DetailHost.Visibility == Visibility.Visible) { DetailHost.Visibility = Visibility.Collapsed; DetailHost.Child = null; } else if (SidebarHost.Visibility == Visibility.Visible) { SidebarHost.Visibility = Visibility.Collapsed; SidebarHost.Child = null; } else await CloseRopeAsync(); }
        else if (SidebarHost.Visibility != Visibility.Visible && DetailHost.Visibility != Visibility.Visible && e.Key is Key.Left or Key.Right)
        { ScrollTo(NoteScroll.HorizontalOffset + (e.Key == Key.Left ? -Pitch : Pitch)); e.Handled = true; }
    }
    internal void RenderDemo(int count = 8)
    {
        Slide.BeginAnimation(TranslateTransform.YProperty, null); Slide.Y = 0;
        var samples = new List<ClipSummary>();
        samples.Add(new Clip { Kind = ClipKind.Text, Text = "A little space for\nyour next big idea.\n\nCopy it. Keep it.\nFind it again.", Pinned = true }.Summary(0));
        samples.Add(new Clip { Kind = ClipKind.Files, FilePaths = ["brand-notes.pdf", "weekend-plans.txt"] }.Summary(0));
        var imageClip = new Clip { Kind = ClipKind.Image };
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(214, 224, 245)), null, new Rect(0, 0, 360, 220), 16, 16);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(239, 179, 106)), null, new Point(285, 58), 27, 27);
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(109, 127, 177)), null, Geometry.Parse("M0,220 L110,70 L220,220 L290,125 L360,220 Z"));
        }
        var picture = new RenderTargetBitmap(360, 220, 96, 96, PixelFormats.Pbgra32); picture.Render(drawing); picture.Freeze();
        _demoImages[imageClip.Id] = picture; samples.Add(imageClip.Summary(0));
        for (var i = 3; i < count; i++) samples.Add(new Clip { Kind = ClipKind.Text, Text = i == 3 ? "One shortcut.\nEverything you just copied.\n\nRight where you need it." : $"Idea {i + 1}\n\nSmall things worth keeping." }.Summary(0));
        _entries = samples.Select((x, i) => x with { CopiedAt = DateTimeOffset.UtcNow.AddMinutes(-i) }).ToArray(); RebuildNotes();
        SetStatus("Design preview · Sample notes · Ctrl + Shift + V");
    }
}
