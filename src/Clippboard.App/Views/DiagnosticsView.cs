using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clippboard.App.Services;

namespace Clippboard.App.Views;

internal sealed class DiagnosticsView : UserControl
{
    private readonly Func<CancellationToken, Task<string>> _collect;
    private readonly Func<string, CancellationToken, Task> _copy;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _cancellation;
    private readonly TextBox _report = new() { Text = Feedback.SupportInfo(), IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap,
        FontSize = 13, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 8) };
    private readonly Button _collectButton = new() { Content = "Preview diagnostics" }, _copyButton = new() { Content = "Copy reviewed diagnostics", IsEnabled = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private bool _busy, _detached;
    internal string Report => _report.Text;
    internal DiagnosticsView(Func<CancellationToken, Task<string>> collect, Func<string, CancellationToken, Task> copy)
    {
        _collect = collect; _copy = copy; _cancellation = _lifetime.Token;
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "Preview a resource snapshot before copying it into a report. Collection reads app counters and configured limits, without reading history. Nothing is sent automatically.", TextWrapping = TextWrapping.Wrap });
        System.Windows.Automation.AutomationProperties.SetName(_report, "Diagnostics preview");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_status, System.Windows.Automation.AutomationLiveSetting.Polite);
        body.Children.Add(_report); var actions = new WrapPanel(); actions.Children.Add(_collectButton); actions.Children.Add(_copyButton); body.Children.Add(actions); body.Children.Add(_status); Content = body;
        _collectButton.Click += async (_, _) => await CollectAsync(); _copyButton.Click += async (_, _) => await CopyAsync();
        Unloaded += (_, _) => Detach();
    }
    internal async Task CollectAsync()
    {
        if (_busy || _detached) return; SetBusy(true); _status.Text = "Collecting app counters…";
        try
        {
            var text = await _collect(_cancellation);
            if (_detached || _cancellation.IsCancellationRequested) return;
            if (text.Length > 16384) throw new InvalidDataException("Unexpected diagnostics length.");
            _report.Text = text; _copyButton.IsEnabled = true; _status.Text = "Review the preview. Copy only if you want to share it.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (Recoverable(ex)) { if (!_detached) _status.Text = "Could not collect diagnostics. Your history was not read or changed."; }
        finally { SetBusy(false); }
    }
    internal async Task CopyAsync()
    {
        if (_busy || _detached || !_copyButton.IsEnabled) return; SetBusy(true);
        try { await _copy(_report.Text, _cancellation); if (!_detached) _status.Text = "Copied. You choose whether to submit it with your report."; }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (Recoverable(ex)) { if (!_detached) _status.Text = "Clipboard is unavailable. Select the preview and copy it manually."; }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool busy) { _busy = busy; if (!_detached) _collectButton.IsEnabled = !busy; }
    protected override void OnVisualParentChanged(DependencyObject oldParent)
    { base.OnVisualParentChanged(oldParent); if (oldParent is not null && VisualTreeHelper.GetParent(this) is null) Detach(); }
    private void Detach() { if (_detached) return; _detached = true; _lifetime.Cancel(); _lifetime.Dispose(); }
    private static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException;
}
