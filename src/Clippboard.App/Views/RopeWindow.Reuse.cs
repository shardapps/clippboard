using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Clippboard.Core;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    private CancellationTokenSource? _editorLoads;
    private bool _snippetsOnly;
    private string? _label;
    private void OnSnippets(object sender, RoutedEventArgs e) { _snippetsOnly = !_snippetsOnly; RebuildNotes(); ScrollTo(0); }
    private async void OnNewSnippet(object sender, RoutedEventArgs e) => await OpenTextEditorAsync(null);
    private void OnLabelFilter(object sender, RoutedEventArgs e)
    {
        var labels = _entries.SelectMany(x => x.Labels ?? []).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.InvariantCulture).ToArray();
        var index = Array.FindIndex(labels, x => string.Equals(x, _label, StringComparison.OrdinalIgnoreCase));
        _label = index + 1 < labels.Length ? labels[index + 1] : null;
        LabelFilterButton.Content = _label ?? "All labels"; RebuildNotes(); ScrollTo(0);
    }
    internal async Task OpenTextEditorAsync(ClipSummary? entry)
    {
        if (Retired || _closing) return;
        DismissSettings(); DismissDetail();
        var source = _editorLoads = new CancellationTokenSource(); var cancellation = source.Token;
        DetailHost.Width = Math.Min(880, Math.Max(200, ActualWidth - 48));
        DetailHost.Height = Math.Min(620, Math.Max(180, AvailableHeight - 48));
        DetailHost.Visibility = Visibility.Visible; DetailHost.Child = new TextBlock { Text = "Opening editor…", Margin = new Thickness(24) }; Reveal(DetailHost, 12);
        Clip? clip = null;
        try
        {
            clip = entry is null ? new Clip { Kind = ClipKind.Text }
                : _preview ? new Clip { Id = entry.Id, Kind = ClipKind.Text, Text = entry.Preview, IsSnippet = entry.IsSnippet, Title = entry.Title, Labels = entry.Labels ?? [] }
                : await Task.Run(() => _store.GetAsync(entry.Id), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (clip is null || clip.Kind != ClipKind.Text) throw new IOException("This text note expired or was deleted.");
            var view = new TextEditorView(clip.Text ?? "", clip.IsSnippet ? clip.Title ?? "" : "", clip.IsSnippet ? clip.Labels : [], clip.IsSnippet, _settings.Animate);
            // Keep only text/metadata in the editor; no captured image buffers.
            var snippetId = clip.IsSnippet ? clip.Id : null;
            view.Dismissed += DismissDetail;
            view.CopyRequested += async text => { if (EditedTextSelected is not null) await EditedTextSelected(text); else view.SetNotice("Copy is unavailable in this preview."); };
            view.SaveRequested += async (text, title, labels, newCopy) =>
            {
                try
                {
                    var saved = _preview || await Task.Run(() => _store.SaveSnippetAsync(newCopy ? null : snippetId, text, title, labels));
                    if (!saved) return "Starred notes fill the storage budget. Free space or increase the limit.";
                    if (!_preview) await RefreshAsync();
                    if (!Retired && !_closing) { SetStatus("Snippet saved · follows your retention limit"); DismissDetail(); }
                    return null;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
                { return ex is ArgumentException ? ex.Message : "Could not save this snippet. It may have expired, been deleted, or exceeded the storage budget."; }
            };
            if (cancellation.IsCancellationRequested || Retired || _closing || !ReferenceEquals(_editorLoads, source)) return;
            DetailHost.Child = view; view.FocusText();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { if (!cancellation.IsCancellationRequested && !Retired) DetailHost.Child = new TextBlock { Text = "This text note could not be opened. Copy the original again.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) }; }
        finally { if (clip?.ImagePng is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(clip.ImagePng); }
    }
}
