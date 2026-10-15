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
using Clippboard.App.Platform;
using Clippboard.Core;

namespace Clippboard.App.Views;

public partial class RopeWindow : Window
{
    private readonly IHistoryAccess _store;
    private readonly PreviewCache _cache;
    private readonly bool _preview;
    private AppSettings _settings;
    private AppSettings _appearance = new();
    internal AppSettings SavedSettings => _settings;
    internal AppSettings Appearance => _appearance;
    private IReadOnlyList<ClipSummary> _entries = [];
    private List<ClipSummary> _visible = [];
    private readonly Dictionary<int, Grid> _cards = new();
    private sealed record NoteBinding(TextBlock Label, TextBlock Date, Button Select, Button Star)
    {
        internal ClipSummary Entry { get; set; } = null!;
    }
    private readonly Dictionary<Grid, NoteBinding> _bindings = new();
    private readonly Stack<Grid> _spareCards = new();
    private (int First, int Last) _renderedRange = (-1, -1);
    private readonly Dictionary<Grid, CancellationTokenSource> _thumbnailLoads = new();
    private readonly Dictionary<string, BitmapSource> _demoImages = new();
    private CancellationTokenSource _loads = new();
    private int _renderVersion;
    private bool _closing, _starredOnly, _scrollSync;
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly System.Windows.Threading.DispatcherTimer _memoryTimer = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private string _query = "";
    private ClipKind? _kind;
    private double _scrollTarget;
    private bool _appearanceRefreshPending;
    private bool? _pausedVisual;
    public event Func<ClipSummary, Task>? Selected;
    public event Action? PauseRequested;
    public event Action? SettingsRequested;
    public event Action<ClipSummary>? ExpandRequested;
    internal int RenderedNoteCount => _cards.Count;
    internal long CachedImageBytes => _cache.RetainedBytes;
    internal long PreviewBudgetBytes => _cache.BudgetBytes;
    internal bool Retired { get; private set; }
    internal int VisibleNoteCount => _visible.Count;
    internal int PendingThumbnailCount => _thumbnailLoads.Count;
    internal int CreatedNoteCount { get; private set; }
    private double Pitch => _appearance.NoteWidth + _appearance.NoteSpacing;
    private bool Animated => _appearance.Animate && SystemParameters.ClientAreaAnimation;

    public RopeWindow(IHistoryAccess store, bool preview = false, AppSettings? settings = null)
    {
        _store = store; _preview = preview; _settings = (settings ?? new()).Normalize();
        _cache = new PreviewCache(store);
        InitializeComponent();
        SourceInitialized += AttachScrollInput;
        Closed += (_, _) => DetachScrollInput();
        ApplyAppearance(_settings);
        SystemParameters.StaticPropertyChanged += OnSystemAppearanceChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemAppearanceChanged;
        Rope.ShapeChanged += PositionNotes;
        _memoryTimer.Tick += (_, _) => { if (!_scrollRunning) UpdatePreviewBudget(); };
        IsVisibleChanged += (_, _) => { if (IsVisible && !_closing) _memoryTimer.Start(); else _memoryTimer.Stop(); };
        Closed += (_, _) => _memoryTimer.Stop();
        SettingsRequested += () => OpenSettings();
        ExpandRequested += async entry => await OpenDetailAsync(entry);
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await RunSearchAsync(); ScrollTo(0); };
        Closed += (_, _) => { _searchLoads?.Cancel(); _searchLoads?.Dispose(); _searchLoads = null; };
        Closed += (_, _) => { Retired = true; SmoothMotion.CancelTree(this); _searchTimer.Stop(); DismissDetail(); SidebarHost.Child = null; _loads.Cancel(); _loads.Dispose(); _cache.Clear(); Notes.Children.Clear(); _cards.Clear(); _spareCards.Clear(); _bindings.Clear(); _entries = []; _visible.Clear(); _demoImages.Clear(); };
    }
    public void ApplyAppearance(AppSettings settings)
    {
        _settings = settings.Normalize();
        PreviewAppearance(_settings);
        UpdatePreviewBudget();
        _cache.Clear(); SetPaused(_settings.IsCapturePaused);
    }
    internal void PreviewAppearance(AppSettings draft)
    {
        var next = draft.Normalize();
        if (_appearance.Theme != next.Theme || !Resources.Contains("Foreground")) ThemeManager.Apply(next.Theme, Resources);
        _appearance = next; Interaction.SetEnabled(this, _appearance.Animate);
        if (Notes is null || Retired) return;
        if (!Animated && IsVisible && !_closing)
        {
            if (_scrollRunning) ScrollTo(_scrollTarget);
            SmoothMotion.Move(Slide, TranslateTransform.YProperty, 0, false, 1);
            SmoothMotion.Move(Rope, RopeVisual.SagProperty, 24, false, 1);
            foreach (var card in _cards.Values)
                if (card.RenderTransform is TransformGroup group) { SmoothMotion.Move(group.Children[0], RotateTransform.AngleProperty, 0, false, 1); SmoothMotion.Move(group.Children[1], TranslateTransform.YProperty, 0, false, 1); }
        }
        Notes.Width = Math.Max(NoteScroll.ViewportWidth, 48 + _visible.Count * Pitch - (_visible.Count > 0 ? _appearance.NoteSpacing : 0));
        foreach (var (index, card) in _cards)
        {
            // Slider input is already continuous. Animate the position with a
            // render transform, and lay out width/text once per draft edit.
            card.Width = _appearance.NoteWidth;
            PlaceCard(card, 24 + index * Pitch, Animated);
            if (card.Tag is Button select && select.Content is TextBlock text) text.FontSize = _appearance.TextSize;
        }
        if (!_appearanceRefreshPending)
        {
            _appearanceRefreshPending = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() => { _appearanceRefreshPending = false; if (!Retired && !_closing) { RenderVisible(); SyncScroll(); } }));
        }
    }
    public void SetStatus(string message)
    {
        if (Status.Text == message) return;
        Status.Text = message;
        if (System.Windows.Automation.Peers.AutomationPeer.ListenerExists(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged))
            System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(Status)?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }
    private void OnSystemAppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SystemParameters.HighContrast) or nameof(SystemParameters.ClientAreaAnimation))) return;
        Dispatcher.BeginInvoke(new Action(() => { if (!Retired) { ThemeManager.Apply(_appearance.Theme, Resources); PreviewAppearance(_appearance); } }));
    }
    public void SetPaused(bool paused)
    {
        if (_pausedVisual == paused) return;
        _pausedVisual = paused;
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
        Width = bounds.Width / VisualTreeHelper.GetDpi(this).DpiScaleX;
        PrepareSurface(bounds.Height);
        Show();
        SetWindowPos(new WindowInteropHelper(this).Handle, 0, bounds.Left, bounds.Top, bounds.Width, Math.Min(bounds.Height, (int)Math.Ceiling(AvailableHeight * VisualTreeHelper.GetDpi(this).DpiScaleY)), 0x0014);
        Activate(); UpdateLayout();
        // Respond visually before disk/IPC history work. Empty state is held
        // until the result arrives rather than flashing an empty-history claim.
        EmptyState.Visibility = Visibility.Collapsed; SetStatus("Loading your notes…");
        FitSurface(); AnimateEntrance();
        await RefreshAsync();
        if (!IsVisible || _closing) return;
        FitSurface();
    }
    internal void DiagnosticScroll(double offset) => ScrollTo(offset);
    internal void DiagnosticGesture(double distance) => GestureScroll(distance);
    internal void AnimateEntrance()
    {
        Slide.BeginAnimation(TranslateTransform.YProperty, null); Slide.Y = -600;
        SmoothMotion.Move(Slide, TranslateTransform.YProperty, 0, Animated, 650);
        Rope.Sag = 48;
        SmoothMotion.Move(Rope, RopeVisual.SagProperty, 24, Animated, 600, _appearance.PlayfulMotion ? .72 : 1);
        if (Animated && _appearance.PlayfulMotion) AnimateNotes();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    public async Task CloseRopeAsync()
    {
        if (!IsVisible || _closing) return;
        _closing = true; StopScroll(); _memoryTimer.Stop(); _searchTimer.Stop(); ++_renderVersion; CancelLoads();
        DismissSettings(); DismissDetail();
        var duration = Animated ? 650 : 1;
        // Retarget the current entrance position/velocity; retract with the
        // same response as opening rather than snapping up at twice the speed.
        SmoothMotion.Move(Slide, TranslateTransform.YProperty, -Math.Max(600, Panel.ActualHeight + 24), Animated, 650);
        await Task.Delay(duration);
        Hide(); SmoothMotion.CancelTree(this); Notes.Children.Clear(); _cards.Clear(); _cache.Clear();
        Slide.BeginAnimation(TranslateTransform.YProperty, null); Slide.Y = -600;
        Rope.BeginAnimation(RopeVisual.SagProperty, null); _closing = false;
        Close(); // Destroy the transparent native surface; the tray creates a fresh overlay on next open.
    }
    private void CancelLoads()
    {
        _loads.Cancel(); _loads.Dispose(); _loads = new();
        foreach (var source in _thumbnailLoads.Values) source.Dispose();
        _thumbnailLoads.Clear();
    }
    public async Task RefreshAsync()
    {
        if (Retired) return;
        var version = ++_renderVersion;
        var entries = await Task.Run(() => _store.ListAsync());
        if (version != _renderVersion || _closing || Retired) return;
        _entries = entries;
        if (SearchBox.Text.Trim().Length > 0) await RunSearchAsync(); else RebuildNotes();
    }
    private void RebuildNotes()
    {
        if (Retired || _closing) return;
        var reusable = _cards.ToDictionary(pair => _visible[pair.Key].Id, pair => (Entry: _visible[pair.Key], Card: pair.Value));
        _cards.Clear();
        _visible = _entries.Where(x => !_starredOnly || x.Pinned)
            .Where(x => (_kind is null || x.Kind == _kind) && (_dateDays is null || x.CopiedAt >= DateTimeOffset.UtcNow.AddDays(-_dateDays.Value))
                && (_query.Length == 0 || _searchMatches.Contains(x.Id)))
            .OrderByDescending(x => _settings.StarredFirst && x.Pinned).ThenByDescending(x => x.CopiedAt).ToList();
        AllButton.SetResourceReference(BackgroundProperty, !_starredOnly ? "SelectedSurface" : "Chrome");
        StarredButton.SetResourceReference(BackgroundProperty, _starredOnly ? "SelectedSurface" : "Chrome");
        EmptyState.Visibility = _visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _searchRunning ? "Searching saved text…" : _query.Length > 0 || _kind is not null || _dateDays is not null ? "No matching notes." : _starredOnly ? "Your favourites live here." : "Room for your next idea.";
        EmptyHint.Text = _query.Length > 0 || _kind is not null ? "Try a different search or choose All types." : _starredOnly ? "Star a note to find it quickly." : "Copy something. We'll hang it here.";
        Notes.Width = Math.Max(NoteScroll.ViewportWidth, 48 + _visible.Count * Pitch - (_visible.Count > 0 ? _appearance.NoteSpacing : 0));
        var range = MotionMath.VisibleRange(_visible.Count, Math.Max(0, ScrollOffset - 24), NoteScroll.ViewportWidth, Pitch);
        for (var i = range.First; i <= range.Last; i++)
        {
            if (!reusable.TryGetValue(_visible[i].Id, out var prior) || prior.Entry != _visible[i]) continue;
            // Preserve unchanged visuals and decoded sources when capture/pinning
            // changes the list. Only changed/new summaries need new controls.
            _cards[i] = prior.Card; reusable.Remove(_visible[i].Id);
            PlaceCard(prior.Card, 24 + i * Pitch, Animated);
        }
        foreach (var prior in reusable.Values) RetireCard(prior.Card);
        _renderedRange = (-1, -1);
        if (_selectedId is not null && !_visible.Any(x => x.Id == _selectedId)) { _selectedId = null; _focusSelection = false; }
        SetStatus($"{_visible.Count} {(_starredOnly ? "starred clips" : "clips")} · {(_searchRunning ? "Searching…" : "Arrows to select · Enter to paste · Shift + Enter to copy")} · Esc to tuck away");
        RenderVisible(); UpdateSelectionVisuals(); SyncScroll();
    }
    private void RenderVisible()
    {
        if (!IsVisible || _closing || NoteScroll.ViewportWidth <= 0) return;
        var range = MotionMath.VisibleRange(_visible.Count, Math.Max(0, ScrollOffset - 24), NoteScroll.ViewportWidth, Pitch);
        if (range == _renderedRange) { PositionNotes(); return; }
        _renderedRange = range;
        UpdatePreviewBudget();
        var spare = new Queue<Grid>();
        foreach (var index in _cards.Keys.Where(i => i < range.First || i > range.Last).ToArray())
        { var card = _cards[index]; ResetCard(card); spare.Enqueue(card); _cards.Remove(index); }
        for (var i = range.First; i <= range.Last; i++)
        {
            if (_cards.ContainsKey(i)) continue;
            var card = spare.Count > 0 ? spare.Dequeue() : _spareCards.Count > 0 ? _spareCards.Pop() : BuildNote(_visible[i]);
            BindNote(card, _visible[i]); _cards[i] = card;
            if (!Notes.Children.Contains(card)) Notes.Children.Add(card);
            Canvas.SetLeft(card, 24 + i * Pitch);
            if (_visible[i].Kind == ClipKind.Image) StartThumbnail(_visible[i], card);
        }
        // Entering/leaving range boundaries do not happen at the same pixel.
        // Keep that small difference warm, bounded by viewport geometry, so
        // the next boundary does not allocate a new control tree.
        var capacity = Math.Min(_visible.Count, (int)Math.Ceiling(NoteScroll.ViewportWidth / Pitch) + 4);
        foreach (var card in spare)
        {
            Notes.Children.Remove(card);
            if (_cards.Count + _spareCards.Count < capacity) _spareCards.Push(card);
            else _bindings.Remove(card);
        }
        while (_spareCards.Count > 0 && _cards.Count + _spareCards.Count > capacity) _bindings.Remove(_spareCards.Pop());
        PositionNotes();
    }
    private void UpdatePreviewBudget()
    {
        var cards = (int)Math.Ceiling(Math.Max(0, NoteScroll?.ViewportWidth ?? 0) / Pitch) + 2;
        _cache.BudgetBytes = MemoryAvailability.PreviewBudget(_settings.PreviewCacheMiB * 1024L * 1024, cards, 360L * 230 * 4, MemoryAvailability.AvailableBytes());
    }
    private void PositionNotes()
    {
        foreach (var (index, card) in _cards)
        {
            var center = 24 + index * Pitch + _appearance.NoteWidth / 2 - ScrollOffset;
            if (card.RenderTransform is TransformGroup transforms && transforms.Children[2] is TranslateTransform anchor) anchor.Y = Rope.HeightAt(center) - 8;
        }
    }
    private void PlaceCard(Grid card, double left, bool animate)
    {
        var prior = Canvas.GetLeft(card);
        var shift = (TranslateTransform)((TransformGroup)card.RenderTransform).Children[3];
        var position = double.IsFinite(prior) ? prior + shift.X : left;
        SmoothMotion.Move(shift, TranslateTransform.XProperty, position - left, false, 1);
        Canvas.SetLeft(card, left);
        SmoothMotion.Move(shift, TranslateTransform.XProperty, 0, animate, 240);
    }
    private void ResetCard(Grid card)
    {
        if (_thumbnailLoads.Remove(card, out var source)) { source.Cancel(); source.Dispose(); }
        SmoothMotion.CancelTree(card);
        if (card.Tag is Button button && button.Content is Image image) image.Source = null;
        if (card.RenderTransform is TransformGroup transforms)
        {
            ((RotateTransform)transforms.Children[0]).Angle = 0;
            ((TranslateTransform)transforms.Children[1]).Y = 0;
            ((TranslateTransform)transforms.Children[3]).X = 0;
        }
        if (card.Children[1] is Border paper && paper.RenderTransform is TranslateTransform hover) hover.Y = 0;
        card.Opacity = 1;
    }
    private void RetireCard(Grid card)
    {
        ResetCard(card); Notes.Children.Remove(card); _bindings.Remove(card);
    }
    private void BindNote(Grid card, ClipSummary entry)
    {
        var binding = _bindings[card]; binding.Entry = entry;
        if (card.Children[1] is Border paper) paper.SetResourceReference(Border.BorderBrushProperty, entry.Id == _selectedId ? "Accent" : "Outline");
        binding.Label.Text = entry.Kind.ToString().ToUpperInvariant();
        binding.Date.Text = entry.CopiedAt.ToLocalTime().ToString("MMM d · HH:mm");
        if (binding.Select.Content is TextBlock text) { text.Text = entry.Preview; text.FontSize = _appearance.TextSize; }
        else binding.Select.Content = new TextBlock { Text = entry.Preview, FontSize = _appearance.TextSize, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 210 };
        System.Windows.Automation.AutomationProperties.SetName(binding.Select, $"Restore {entry.Kind}: {entry.Preview}");
        var name = entry.Pinned ? "Unstar clip" : "Star clip";
        ((IconView)binding.Star.Content).Symbol = entry.Pinned ? "star" : "star-outline";
        ((IconView)binding.Star.Content).SetResourceReference(IconView.InkProperty, entry.Pinned ? "StarColor" : "Foreground");
        binding.Star.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(binding.Star, name);
        card.Width = _appearance.NoteWidth;
    }
    private void StartThumbnail(ClipSummary entry, Grid card)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(_loads.Token);
        _thumbnailLoads[card] = source;
        _ = LoadThumbnailAsync(entry, card, source, source.Token);
    }
    private async Task LoadThumbnailAsync(ClipSummary entry, Grid card, CancellationTokenSource source, CancellationToken cancellation)
    {
        try
        {
            var bitmap = _demoImages.GetValueOrDefault(entry.Id) ?? await _cache.GetAsync(entry.Id, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!Notes.Children.Contains(card)) return;
            if (card.Tag is Button select && bitmap is not null) select.Content = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 215 };
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or NotSupportedException or ArgumentException or System.Security.Cryptography.CryptographicException)
        { if (!cancellation.IsCancellationRequested) SetStatus("An image preview is unavailable. Expand it to try again."); }
        finally { if (_thumbnailLoads.TryGetValue(card, out var active) && ReferenceEquals(active, source)) _thumbnailLoads.Remove(card); source.Dispose(); }
    }
    private Grid BuildNote(ClipSummary entry)
    {
        ++CreatedNoteCount;
        var wrapper = new Grid { Width = _appearance.NoteWidth, Height = 330, RenderTransformOrigin = new Point(.5, 0) };
        ClipSummary Current() => _bindings[wrapper].Entry;
        var attachment = new TransformGroup(); attachment.Children.Add(new RotateTransform()); attachment.Children.Add(new TranslateTransform()); attachment.Children.Add(new TranslateTransform()); attachment.Children.Add(new TranslateTransform()); wrapper.RenderTransform = attachment;
        var hover = new TranslateTransform();
        wrapper.MouseDown += OnInsideClick;
        wrapper.GotKeyboardFocus += (_, _) => { _selectedId = Current().Id; UpdateSelectionVisuals(); };
        // A small ambient border avoids a per-card pixel-shader blur surface on every moving frame.
        wrapper.Children.Add(new Border { CornerRadius = new CornerRadius(23), Margin = new Thickness(0, 26, 0, -3), Background = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)), IsHitTestVisible = false });
        var paper = new Border { CornerRadius = new CornerRadius(22), Margin = new Thickness(0, 23, 0, 0), Padding = new Thickness(21, 25, 21, 16), BorderThickness = new Thickness(1) };
        paper.RenderTransform = hover;
        wrapper.MouseEnter += (_, _) => Interaction.Move(hover, TranslateTransform.YProperty, -5, Animated, 300);
        wrapper.MouseLeave += (_, _) => Interaction.Move(hover, TranslateTransform.YProperty, 0, Animated, 350);
        paper.SetResourceReference(Border.BackgroundProperty, "NoteSurface"); paper.SetResourceReference(Border.BorderBrushProperty, "Outline");
        var body = new Grid(); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new()); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var label = new TextBlock { Text = entry.Kind.ToString().ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); body.Children.Add(label);
        var select = new Button { Padding = new Thickness(0), Margin = new Thickness(0, 14, 0, 14), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top, ToolTip = "Restore this clip · Shift-click to copy without pasting",
            Content = new TextBlock { Text = entry.Preview, FontSize = _appearance.TextSize, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 210 } };
        System.Windows.Automation.AutomationProperties.SetName(select, $"Restore {entry.Kind}: {entry.Preview}");
        select.Click += async (_, _) => { if (Selected is not null) await Selected(Current()); };
        Grid.SetRow(select, 1); body.Children.Add(select); wrapper.Tag = select;
        var footer = new Grid(); footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var date = new TextBlock { Text = entry.CopiedAt.ToLocalTime().ToString("MMM d · HH:mm"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        date.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); footer.Children.Add(date);
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        var star = IconButton(entry.Pinned ? "star" : "star-outline", entry.Pinned ? "Unstar clip" : "Star clip");
        _bindings.Add(wrapper, new NoteBinding(label, date, select, star) { Entry = entry });
        if (entry.Pinned) ((IconView)star.Content).SetResourceReference(IconView.InkProperty, "StarColor");
        star.Click += async (_, _) => await ChangeNoteAsync(Current(), false); actions.Children.Add(star);
        var expand = IconButton("expand", "Expand and resize note"); expand.Click += (_, _) => ExpandRequested?.Invoke(Current()); actions.Children.Add(expand);
        var delete = IconButton("close", "Delete clip"); delete.Click += async (_, _) => await ChangeNoteAsync(Current(), true); actions.Children.Add(delete);
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
            if (delete)
            {
                var card = _cards.FirstOrDefault(x => _visible[x.Key].Id == entry.Id).Value;
                if (card is not null) { Interaction.Move(card, OpacityProperty, 0, Animated, 160); if (Animated) await Task.Delay(160); }
                if (Retired || _closing) return;
            }
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
            var group = (TransformGroup)card.RenderTransform;
            var rotation = (RotateTransform)group.Children[0]; var vertical = (TranslateTransform)group.Children[1];
            var direction = index % 2 == 0 ? 1 : -1;
            rotation.Angle = -1.8 * direction; vertical.Y = -6;
            SmoothMotion.Move(rotation, RotateTransform.AngleProperty, 0, Animated, 500 + index % 5 * 25, .55);
            SmoothMotion.Move(vertical, TranslateTransform.YProperty, 0, Animated, 450, .8);
        }
    }
    private void OnAll(object sender, RoutedEventArgs e) { _starredOnly = false; RebuildNotes(); ScrollTo(0); }
    private void OnStarred(object sender, RoutedEventArgs e) { _starredOnly = true; RebuildNotes(); ScrollTo(0); }
    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnFeedback(object sender, RoutedEventArgs e) => OpenSettings(4);
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (Notes is null || Retired) return; _searchLoads?.Cancel(); _searchTimer.Stop(); _searchTimer.Start(); }
    private void OnTypeFilter(object sender, RoutedEventArgs e)
    {
        _kind = _kind switch { null => ClipKind.Text, ClipKind.Text => ClipKind.Image, ClipKind.Image => ClipKind.Files, _ => null };
        TypeFilterButton.Content = _kind?.ToString() ?? "All types"; RebuildNotes(); ScrollTo(0);
    }
    private async void OnOutsideClick(object sender, MouseButtonEventArgs e) => await CloseRopeAsync();
    private void OnInsideClick(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private async void OnClose(object sender, RoutedEventArgs e) => await CloseRopeAsync();
    private async void OnDeactivated(object? sender, EventArgs e) { if (!_preview) await CloseRopeAsync(); }
    private void OnPause(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { e.Handled = true; if (DetailHost.Visibility == Visibility.Visible) DismissDetail(); else if (SidebarHost.Visibility == Visibility.Visible) DismissSettings(); else await CloseRopeAsync(); }
        else if (SidebarHost.Visibility == Visibility.Visible || DetailHost.Visibility == Visibility.Visible) return;
        else if (e.OriginalSource is TextBox)
        { if (e.Key == Key.Down && ReferenceEquals(e.OriginalSource, SearchBox)) { SelectNote(0); e.Handled = true; } }
        else if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            var current = _visible.FindIndex(x => x.Id == _selectedId);
            var page = Math.Max(1, (int)(NoteScroll.ViewportWidth / Pitch));
            var next = e.Key switch { Key.Home => 0, Key.End => _visible.Count - 1, Key.PageUp => current - page,
                Key.PageDown => current + page, Key.Left => current - 1, _ => current + 1 };
            SelectNote(next); e.Handled = true;
        }
        else if (e.Key == Key.Enter && _selectedId is not null
            && (e.OriginalSource is not Button focused || _bindings.Values.Any(x => ReferenceEquals(x.Select, focused))))
        { e.Handled = true; var entry = _visible.FirstOrDefault(x => x.Id == _selectedId); if (entry is not null && Selected is not null) await Selected(entry); }
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
