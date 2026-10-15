using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Clippboard.App.Services;

namespace Clippboard.App.Views;

internal sealed class OcrView : UserControl
{
    private readonly Func<string, CancellationToken, Task<string>> _extract;
    private readonly ListBox _languages = new() { DisplayMemberPath = "Name", SelectedValuePath = "Tag", MinHeight = 38, MaxHeight = 140, BorderThickness = new Thickness(0), Margin = new Thickness(0, 12, 0, 12) };
    private readonly Button _start = new() { Content = "Extract text", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button _cancel = new() { Content = "Cancel extraction", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12), FontSize = 15 };
    private CancellationTokenSource? _work;
    private bool _running, _unloaded;
    internal event Action? Dismissed;
    internal event Func<string, Task>? Recognized;
    internal OcrView(OcrAvailability availability, Func<string, CancellationToken, Task<string>> extract)
    {
        _extract = extract;
        var body = new StackPanel { Margin = new Thickness(24) };
        var header = new DockPanel(); var close = RopeWindow.IconButton("close", "Close text extraction");
        close.Click += (_, _) => Dismissed?.Invoke(); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "Read text from this image", TextWrapping = TextWrapping.Wrap, FontSize = 23, FontWeight = FontWeights.SemiBold }); body.Children.Add(header);
        body.Children.Add(new TextBlock { Text = "Runs locally, only when you ask. Review and edit the result before copying or saving it. Recognition can make mistakes.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 6), FontSize = 15 });
        _languages.ItemsSource = availability.Languages; _languages.SelectedIndex = availability.Languages.Count > 0 ? 0 : -1;
        _languages.SetResourceReference(BackgroundProperty, "Chrome"); _languages.SetResourceReference(ForegroundProperty, "Foreground");
        _languages.ItemContainerStyle = LabelPickerView.ChoiceItemStyle();
        VirtualizingPanel.SetIsVirtualizing(_languages, true); VirtualizingPanel.SetVirtualizationMode(_languages, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_languages, ScrollBarVisibility.Disabled);
        System.Windows.Automation.AutomationProperties.SetName(_languages, "Installed OCR language");
        body.Children.Add(_languages); body.Children.Add(_start); body.Children.Add(_cancel); body.Children.Add(_notice);
        _notice.Text = availability.Message ?? "Choose the language used in this image.";
        _start.IsEnabled = availability.Message is null; _languages.IsEnabled = _start.IsEnabled;
        var settings = new Button { Content = "Windows language settings", HorizontalAlignment = HorizontalAlignment.Left };
        settings.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { _notice.Text = "Open Windows Settings → Time & language → Language & region."; } };
        body.Children.Add(settings);
        _start.Click += async (_, _) => await ExtractAsync();
        _cancel.Click += (_, _) => { _work?.Cancel(); _notice.Text = "Canceled. Native recognition may finish its current bounded image before another extraction can start."; _cancel.IsEnabled = false; };
        Unloaded += (_, _) => { _unloaded = true; _work?.Cancel(); };
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    internal async Task ExtractAsync()
    {
        if (_running || _unloaded || _languages.SelectedValue is not string language) return;
        _running = true; var source = _work = new(); var cancellation = source.Token;
        _start.IsEnabled = false; _languages.IsEnabled = false; _cancel.Visibility = Visibility.Visible; _cancel.IsEnabled = true; _notice.Text = "Reading text locally…";
        try
        {
            var text = await Task.Run(() => _extract(language, cancellation), cancellation);
            cancellation.ThrowIfCancellationRequested();
            // WPF queues Unloaded; a fast completion can arrive after the view
            // detaches but before that event. Check the live presentation tree.
            if (!_unloaded && IsVisible && PresentationSource.FromVisual(this) is not null && Recognized is not null) await Recognized(text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is OcrUnavailableException or System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { if (!_unloaded && !cancellation.IsCancellationRequested) _notice.Text = ex is OcrUnavailableException ? ex.Message : "The saved image could not be loaded. Try copying the original again."; }
        finally
        {
            _running = false; if (ReferenceEquals(_work, source)) _work = null; source.Dispose();
            if (!_unloaded) { _start.IsEnabled = true; _languages.IsEnabled = true; _cancel.Visibility = Visibility.Collapsed; }
        }
    }
}
