using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Clippboard.Core;

namespace Clippboard.App.Views;

internal sealed record NoteView(ClipSummary Summary, ImageSource? Thumbnail = null);

public partial class RopeWindow : Window
{
    private readonly HistoryStore _store;
    private int _renderVersion;
    private bool _closing;
    private bool _dialogOpen;
    private readonly bool _preview;
    public event Func<ClipSummary, Task>? Selected;
    public event Action? PauseRequested;

    public RopeWindow(HistoryStore store, bool preview = false)
    {
        _store = store;
        _preview = preview;
        InitializeComponent();
    }

    public void SetStatus(string message) => Status.Text = message;
    public void SetPaused(bool paused) => PauseButton.Content = paused ? "Resume" : "Pause";

    public async Task OpenAsync(nint target)
    {
        if (_closing) return;
        var screen = System.Windows.Forms.Screen.FromHandle(target);
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        var bounds = screen.WorkingArea;
        // Physical pixels let Windows handle negative monitor origins and per-monitor DPI.
        SetWindowPos(hwnd, 0, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0014);
        Activate();
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Slide.Y = -600;
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 360 : 1))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        await RefreshAsync();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    public async Task CloseRopeAsync()
    {
        if (!IsVisible || _closing) return;
        _closing = true;
        ++_renderVersion;
        var duration = SystemParameters.ClientAreaAnimation ? 200 : 1;
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-600, TimeSpan.FromMilliseconds(duration))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        await Task.Delay(duration);
        Hide();
        Notes.Children.Clear(); // Release decoded thumbnails when closed.
        _closing = false;
    }

    public async Task RefreshAsync()
    {
        var version = ++_renderVersion;
        var entries = await _store.ListAsync();
        if (version != _renderVersion) return;
        Notes.Children.Clear();
        EmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus($"{entries.Count} clips · Showing newest 40, pinned first · Encrypted on this PC");
        foreach (var entry in entries.Take(40))
        {
            ImageSource? thumb = null;
            if (entry.Kind == ClipKind.Image)
            {
                try
                {
                    var clip = await _store.GetAsync(entry.Id);
                    if (clip?.ImagePng is not null)
                    {
                        using var stream = new MemoryStream(clip.ImagePng);
                        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                        image.DecodePixelWidth = 300; image.StreamSource = stream; image.EndInit(); image.Freeze(); thumb = image;
                    }
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException) { }
            }
            if (version != _renderVersion) return;
            AddNote(new NoteView(entry, thumb));
        }
    }

    private void AddNote(NoteView view)
    {
        var entry = view.Summary;
        string[] colors = ["#F4EDD9", "#DCE9D9", "#E8DFF0", "#F2DFCD"];
        var color = colors[Notes.Children.Count % colors.Length];
        var wrapper = new Grid { Width = 244, Height = 270, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(9, 0, 9, 0) };
        var paper = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString(color)!, CornerRadius = new CornerRadius(20),
            Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(19, 21, 19, 16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 5, Opacity = 0.18 }
        };
        var body = new Grid(); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition()); body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Text = entry.Kind.ToString().ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.DarkSlateGray };
        body.Children.Add(label);
        var select = new Button
        {
            Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(44, 54, 48)),
            Padding = new Thickness(0), Margin = new Thickness(0, 12, 0, 12), HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = "Restore and paste into your previous app"
        };
        System.Windows.Automation.AutomationProperties.SetName(select, $"Paste {entry.Kind}: {entry.Preview}");
        select.Content = view.Thumbnail is not null ? new Image { Source = view.Thumbnail, Stretch = Stretch.Uniform, MaxHeight = 145 }
            : new TextBlock { Text = entry.Preview, FontSize = 15, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 140 };
        select.Click += async (_, _) => { if (Selected is not null) await Selected(entry); };
        Grid.SetRow(select, 1); body.Children.Add(select);
        var footer = new DockPanel();
        var delete = new Button { Content = "×", Padding = new Thickness(8, 3, 8, 3), Background = Brushes.Transparent, Foreground = Brushes.DarkSlateGray, ToolTip = "Delete this clip" };
        System.Windows.Automation.AutomationProperties.SetName(delete, "Delete clip");
        delete.Click += async (_, _) => { try { await _store.DeleteAsync(entry.Id); await RefreshAsync(); } catch (IOException) { SetStatus("Could not delete this clip. Try again."); } };
        DockPanel.SetDock(delete, Dock.Right); footer.Children.Add(delete);
        var pin = new Button { Content = entry.Pinned ? "★" : "☆", Padding = new Thickness(8, 3, 8, 3), Background = Brushes.Transparent, Foreground = Brushes.DarkSlateGray, ToolTip = entry.Pinned ? "Unpin" : "Keep this clip" };
        System.Windows.Automation.AutomationProperties.SetName(pin, entry.Pinned ? "Unpin clip" : "Pin clip");
        pin.Click += async (_, _) => { try { await _store.SetPinnedAsync(entry.Id, !entry.Pinned); await RefreshAsync(); } catch (IOException) { SetStatus("Could not update this clip. Try again."); } };
        DockPanel.SetDock(pin, Dock.Right); footer.Children.Add(pin);
        footer.Children.Add(new TextBlock { Text = entry.CopiedAt.ToLocalTime().ToString("MMM d · HH:mm"), FontSize = 10, Foreground = Brushes.DarkSlateGray, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetRow(footer, 2); body.Children.Add(footer); paper.Child = body; wrapper.Children.Add(paper);
        var peg = new Border { Width = 14, Height = 46, Margin = new Thickness(0, -14, 0, 0), CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromRgb(181, 136, 90)), BorderBrush = new SolidColorBrush(Color.FromRgb(133, 98, 67)), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        wrapper.Children.Add(peg);
        Notes.Children.Add(wrapper);
    }

    internal void RenderDemo()
    {
        Slide.BeginAnimation(TranslateTransform.YProperty, null); Slide.Y = 0;
        EmptyState.Visibility = Visibility.Collapsed;
        AddNote(new(new Clip { Kind = ClipKind.Text, Text = "A tiny place for\nyour next big idea.\n\nCopy it. Keep it.\nFind it again." }.Summary(0)));
        AddNote(new(new Clip { Kind = ClipKind.Files, FilePaths = ["brand-notes.pdf", "weekend-plans.txt"], Pinned = true }.Summary(0)));
        AddNote(new(new Clip { Kind = ClipKind.Text, Text = "# A little less friction\n\nOne shortcut.\nEverything you just copied.\nRight where you need it." }.Summary(0)));
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(154, 192, 172)), null, new Rect(0, 0, 300, 180), 16, 16);
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(245, 223, 165)), null, new Point(239, 47), 23, 23);
            context.DrawGeometry(new SolidColorBrush(Color.FromRgb(80, 127, 99)), null, Geometry.Parse("M0,180 L88,58 L177,180 L243,100 L300,180 Z"));
        }
        var picture = new RenderTargetBitmap(300, 180, 96, 96, PixelFormats.Pbgra32); picture.Render(drawing); picture.Freeze();
        AddNote(new(new Clip { Kind = ClipKind.Image }.Summary(0), picture));
        Status.Text = "Design preview · Sample notes · Your clipboard stays local";
    }

    private async void OnOutsideClick(object sender, MouseButtonEventArgs e) => await CloseRopeAsync();
    private void OnPanelClick(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private async void OnClose(object sender, RoutedEventArgs e) => await CloseRopeAsync();
    private async void OnDeactivated(object? sender, EventArgs e) { if (!_dialogOpen && !_preview) await CloseRopeAsync(); }
    private void OnPause(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
    private async void OnClear(object sender, RoutedEventArgs e)
    {
        _dialogOpen = true;
        MessageBoxResult answer;
        try { answer = MessageBox.Show(this, "Delete all saved clips, including pinned notes?", "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Question); }
        finally { _dialogOpen = false; }
        if (answer != MessageBoxResult.Yes) return;
        try { await _store.ClearAsync(); await RefreshAsync(); } catch (IOException) { SetStatus("Could not clear history. Try again."); }
    }
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; await CloseRopeAsync(); }
    }
}
