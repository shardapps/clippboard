using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Clippboard.App.Services;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    private readonly LocalOcr _ocr = new();
    internal Task PreviewOcrAsync()
    {
        if (!_preview) throw new InvalidOperationException("Synthetic OCR is available only in preview mode.");
        return OpenOcrAsync("", new([new("en-US", "English (United States)"), new("de-DE", "German")]));
    }
    private async Task OpenOcrAsync(string id, OcrAvailability? previewAvailability = null)
    {
        if (Retired || _closing) return;
        DismissSettings(); DismissDetail();
        var source = _detailLoads = new CancellationTokenSource(); var cancellation = source.Token;
        DetailHost.Width = Math.Min(760, Math.Max(200, ActualWidth - 48));
        DetailHost.Height = Math.Min(500, Math.Max(180, AvailableHeight - 48));
        DetailHost.Child = new System.Windows.Controls.TextBlock { Text = "Checking local recognition…", Margin = new Thickness(24), FontSize = 18 };
        DetailHost.Visibility = Visibility.Visible; Reveal(DetailHost, 12);
        var availability = previewAvailability ?? await Task.Run(_ocr.Availability);
        if (Retired || _closing || cancellation.IsCancellationRequested || !ReferenceEquals(_detailLoads, source)) return;
        var view = new OcrView(availability, async (language, requestCancellation) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, requestCancellation);
            if (_preview) return "Synthetic text for preview only.";
            return await _ocr.ExtractAsync(() => _store.GetAsync(id), language, linked.Token);
        });
        view.Dismissed += DismissDetail;
        view.Recognized += text => ReferenceEquals(_detailLoads, source) && !cancellation.IsCancellationRequested ? OpenTextEditorAsync(null, text) : Task.CompletedTask;
        DetailHost.Child = view;
    }
}
