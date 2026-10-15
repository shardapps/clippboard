using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Clippboard.Core;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    private readonly List<string> _combinationIds = new();
    private CancellationTokenSource? _combineLoad;
    internal int CombinationCount => _combinationIds.Count;
    internal async Task PreviewCombinationAsync()
    {
        if (!_preview) throw new InvalidOperationException("Synthetic combination is available only in preview mode.");
        Clip[] notes = [new() { Text = "One small idea worth keeping.", Title = "The idea", Labels = ["work"] },
            new() { Text = "A little more context.\nMake room for what comes next.", Title = "The context", Labels = ["work"] },
            new() { Text = "Bring the useful pieces together.", Title = "The next step", Labels = ["ideas"] }];
        _entries = notes.Select(x => x.Summary(0)).ToArray(); _combinationIds.Clear(); _combinationIds.AddRange(notes.Select(x => x.Id));
        RebuildNotes(); UpdateCombinationButton(); await OpenCombinationAsync();
    }
    internal void ToggleCombination(ClipSummary entry)
    {
        if (entry.Kind != ClipKind.Text) { SetStatus("Combine supports text notes. Restore files and images individually."); return; }
        if (!_combinationIds.Remove(entry.Id))
        {
            if (_combinationIds.Count >= TextCombination.MaxNotes) { SetStatus("The selection reached the history entry limit."); return; }
            _combinationIds.Add(entry.Id);
        }
        UpdateCombinationButton(); UpdateSelectionVisuals();
        SetStatus($"{_combinationIds.Count} selected · Ctrl-click / Ctrl+Space to change selection · Combine to preview");
    }
    private void UpdateCombinationButton()
    {
        CombineButton.Visibility = _combinationIds.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CombineButton.Content = $"Combine ({_combinationIds.Count})";
    }
    private async void OnCombine(object sender, RoutedEventArgs e) => await OpenCombinationAsync();
    internal async Task OpenCombinationAsync()
    {
        if (_combinationIds.Count == 0 || Retired || _closing) return;
        DismissSettings(); DismissDetail();
        var source = _combineLoad = new CancellationTokenSource();
        var entries = _combinationIds.Select(id => _entries.FirstOrDefault(x => x.Id == id)).Where(x => x is not null).Cast<ClipSummary>().ToArray();
        if (entries.Length != _combinationIds.Count) { SetStatus("A selected note is unavailable. Select your notes again."); return; }
        var view = new CombinationView(entries, async (ids, separator, cancellation) =>
        {
            if (_preview) return string.Join(separator, ids.Select(id => entries.Single(x => x.Id == id).Preview));
            return await Task.Run(() => TextCombination.BuildAsync(_store, ids, separator, cancellation), cancellation);
        }, source.Token);
        view.Dismissed += DismissDetail;
        view.ClearRequested += () => { _combinationIds.Clear(); UpdateCombinationButton(); UpdateSelectionVisuals(); DismissDetail(); };
        view.CopyRequested += async text => { if (EditedTextSelected is not null) await EditedTextSelected(text); };
        view.EditRequested += async text => await OpenTextEditorAsync(null, text);
        DetailHost.Width = Math.Min(880, Math.Max(200, ActualWidth - 48));
        DetailHost.Height = Math.Min(620, Math.Max(180, AvailableHeight - 48));
        DetailHost.Child = view; DetailHost.Visibility = Visibility.Visible; Reveal(DetailHost, 12);
        await view.RebuildAsync();
    }
}
