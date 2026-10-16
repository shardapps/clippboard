using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Clippboard.App.Design;
using Clippboard.App.Services;
using Clippboard.Core;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    private CancellationTokenSource? _detailLoads;
    private int _sidebarMotion, _detailMotion;
    private int NewPanelMotion(UIElement panel) => ReferenceEquals(panel, SidebarHost) ? ++_sidebarMotion : ++_detailMotion;
    private int PanelMotion(UIElement panel) => ReferenceEquals(panel, SidebarHost) ? _sidebarMotion : _detailMotion;
    public event Func<AppSettings, Task<string?>>? SettingsSaveRequested;
    public bool StartupAvailable { get; set; }
    internal TextBox? DetailText { get; private set; }
    private Clip? _previewDetail;
    internal async Task PreviewDetailAsync(Clip clip)
    {
        if (!_preview) throw new InvalidOperationException("Synthetic payloads are available only in preview mode.");
        _previewDetail = clip;
        try { await OpenDetailAsync(clip.Summary(0)); }
        finally { _previewDetail = null; }
    }

    internal void OpenSettings(int page = 0)
    {
        if (Retired) return;
        DismissDetail();
        SidebarHost.Width = Math.Min(620, Math.Max(220, ActualWidth - 36));
        SidebarHost.MaxHeight = Math.Max(180, AvailableHeight - 100);
        var view = new SettingsView(_settings, StartupAvailable);
        if (!_preview && _store is OverlayClient client)
        {
            view.DiagnosticsRequested = async cancellation => (await client.CallAsync(new("diagnostics"), cancellation)).Diagnostics ?? throw new IOException("Diagnostics unavailable.");
            view.SupportCopyRequested = async (text, cancellation) =>
            {
                await client.CallAsync(new("restore", Text: text), cancellation);
                await client.CallAsync(new("paste", Flag: true), cancellation);
            };
        }
        view.AppearancePreview += PreviewAppearance;
        view.Dismissed += DismissSettings;
        view.TransferRequested += OpenBackup;
        view.NoticesRequested += OpenNotices;
        view.SaveRequested += async settings =>
        {
            var error = _preview ? null : SettingsSaveRequested is null ? "Settings are unavailable." : await SettingsSaveRequested(settings);
            if (error is null) { ApplyAppearance(settings); RebuildNotes(); }
            else PreviewAppearance(_settings);
            return error;
        };
        view.ClearRequested += async () => { if (_preview) { _entries = []; RebuildNotes(); } else { await Task.Run(() => _store.ClearAsync()); await RefreshAsync(); } };
        view.ShowPage(page); SidebarHost.Child = view; SidebarHost.Visibility = Visibility.Visible; Reveal(SidebarHost, 26); view.Focus();
    }
    private void DismissSettings()
    {
        HidePanel(SidebarHost);
        PreviewAppearance(_settings);
    }
    private void DismissDetail()
    {
        _combineLoad?.Cancel(); _combineLoad?.Dispose(); _combineLoad = null;
        _editorLoads?.Cancel(); _editorLoads?.Dispose(); _editorLoads = null;
        _detailLoads?.Cancel(); _detailLoads?.Dispose(); _detailLoads = null;
        HidePanel(DetailHost); DetailText = null;
    }
    internal async Task OpenDetailAsync(ClipSummary entry)
    {
        if (Retired) return;
        DismissSettings(); DismissDetail();
        var source = _detailLoads = new CancellationTokenSource(); var cancellation = source.Token;
        DetailHost.Width = Math.Min(820, Math.Max(200, ActualWidth - 48));
        DetailHost.Height = Math.Min(560, Math.Max(180, AvailableHeight - 48));
        DetailHost.Child = new TextBlock { Text = "Opening note…", Margin = new Thickness(24), FontSize = 20 };
        DetailHost.Visibility = Visibility.Visible; Reveal(DetailHost, 12);
        Clip? loaded = null;
        try
        {
            var clip = loaded = _preview && _previewDetail?.Id == entry.Id ? _previewDetail : await Task.Run(() => _store.GetAsync(entry.Id), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (_preview && clip is null) clip = new Clip { Id = entry.Id, Kind = entry.Kind, Text = entry.Preview, FilePaths = entry.Kind == ClipKind.Files ? entry.Preview.Split('\n') : null };
            if (clip is null) { SetStatus("This note has expired or was deleted."); DismissDetail(); return; }
            System.Windows.Media.Imaging.BitmapSource? image = null;
            if (clip.ImagePng is not null) image = await Task.Run(() => PreviewCache.Decode(clip.ImagePng, 1600, 1000), cancellation);
            else if (_preview && _demoImages.TryGetValue(entry.Id, out var demo)) image = demo;
            cancellation.ThrowIfCancellationRequested();
            if (!IsVisible || _closing || !ReferenceEquals(_detailLoads, source)) return;
            var body = new Grid { Margin = new Thickness(24) };
            body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new()); body.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
            var close = IconButton("close", "Close expanded note"); close.Click += (_, _) => DismissDetail(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
            header.Children.Add(new TextBlock { Text = $"{entry.Kind} · {entry.CopiedAt.ToLocalTime():MMM d · HH:mm}", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }); body.Children.Add(header);
            FrameworkElement content;
            if (image is not null) content = new Image { Source = image, Stretch = Stretch.Uniform };
            else
            {
                DetailText = new TextBox { Text = clip.Kind == ClipKind.Files ? string.Join("\n\n", clip.FilePaths ?? []) : clip.Text ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, FontSize = _settings.TextSize + 1 };
                System.Windows.Automation.AutomationProperties.SetName(DetailText, "Full note contents"); content = DetailText;
            }
            Grid.SetRow(content, 1); body.Children.Add(content);
            var footer = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
            var grip = new Thumb { Width = 28, Height = 28, Cursor = System.Windows.Input.Cursors.SizeNWSE,
                Template = GripTemplate() };
            System.Windows.Automation.AutomationProperties.SetName(grip, "Resize expanded note");
            grip.DragDelta += (_, e) => { Interaction.Move(DetailHost, WidthProperty, Math.Clamp(DetailHost.Width + e.HorizontalChange * 2, Math.Min(360, ActualWidth - 32), ActualWidth - 32), false); Interaction.Move(DetailHost, HeightProperty, Math.Clamp(DetailHost.Height + e.VerticalChange * 2, Math.Min(260, ActualHeight - 32), ActualHeight - 32), false); };
            var larger = new Button { Content = "Larger", ToolTip = "Enlarge note" };
            larger.Click += (_, _) => { Interaction.Move(DetailHost, WidthProperty, Math.Min(ActualWidth - 32, DetailHost.Width + 160), Animated); Interaction.Move(DetailHost, HeightProperty, Math.Min(ActualHeight - 32, DetailHost.Height + 100), Animated); };
            var paste = new Button { Content = _settings.AutoPaste ? "Restore & paste" : "Copy to clipboard", Padding = new Thickness(16, 9, 16, 9) };
            paste.SetResourceReference(BackgroundProperty, "SelectedSurface"); paste.Click += async (_, _) => { if (Selected is not null) await Selected(entry); };
            footer.Children.Add(paste);
            if (clip.Kind == ClipKind.Text)
            {
                if (clip.HasRichText)
                {
                    var plainText = clip.Text ?? "";
                    var plain = FileActionButton("Copy plain text", async () =>
                    {
                        if (EditedTextSelected is not null) await EditedTextSelected(plainText);
                    });
                    footer.Children.Add(plain);
                }
                var edit = new Button { Content = clip.IsSnippet ? "Edit snippet" : "Edit a copy" };
                edit.Click += async (_, _) => await OpenTextEditorAsync(entry); footer.Children.Add(edit);
            }
            else if (clip.Kind == ClipKind.Files) AddFileActions(footer, (string[])(clip.FilePaths ?? []).Clone());
            else if (clip.Kind == ClipKind.Image)
            {
                var saveImage = FileActionButton("Save PNG…", () => SaveImageAsync(entry.Id)); footer.Children.Add(saveImage);
                var extract = FileActionButton("Extract text…", () => OpenOcrAsync(entry.Id)); footer.Children.Add(extract);
            }
            footer.Children.Add(larger); footer.Children.Add(grip);
            var hint = new TextBlock { Text = "Drag the corner to resize", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) }; hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); footer.Children.Add(hint);
            Grid.SetRow(footer, 2); body.Children.Add(footer); DetailHost.Child = body; content.Focus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Security.Cryptography.CryptographicException)
        { if (!cancellation.IsCancellationRequested) { DetailHost.Child = new TextBlock { Text = "This note could not be opened. Try copying the original again.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), FontSize = 18 }; } }
        finally { if (loaded?.ImagePng is byte[] png) System.Security.Cryptography.CryptographicOperations.ZeroMemory(png); }
    }
    private static ControlTemplate GripTemplate()
    {
        // Small original vector grip; avoid a platform size-grip bitmap.
        var factory = new FrameworkElementFactory(typeof(IconView)); factory.SetValue(IconView.SymbolProperty, "expand"); factory.SetValue(FrameworkElement.WidthProperty, 20.0); factory.SetValue(FrameworkElement.HeightProperty, 20.0);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = factory };
    }
    private void Reveal(UIElement element, double from)
    {
        var continuing = element.HasAnimatedProperties || element.RenderTransform is TranslateTransform moving && moving.HasAnimatedProperties;
        NewPanelMotion(element);
        element.IsHitTestVisible = true;
        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform(); element.RenderTransform = transform;
        if (!continuing) { element.Opacity = Animated ? 0 : 1; transform.X = Animated ? from : 0; }
        SmoothMotion.Move(element, OpacityProperty, 1, Animated, 180);
        SmoothMotion.Move(transform, TranslateTransform.XProperty, 0, Animated, 260);
    }
    private async void HidePanel(Border panel)
    {
        if (panel.Visibility != Visibility.Visible) return;
        var version = NewPanelMotion(panel);
        panel.IsHitTestVisible = false;
        if (Animated && !_closing && !Retired)
        {
            Interaction.Move(panel, OpacityProperty, 0, true, 180);
            await Task.Delay(360);
            if (version != PanelMotion(panel)) return; // A newer open or close owns this panel.
        }
        SmoothMotion.Move(panel, OpacityProperty, 1, false, 1);
        panel.Visibility = Visibility.Collapsed; panel.Child = null;
    }
}
