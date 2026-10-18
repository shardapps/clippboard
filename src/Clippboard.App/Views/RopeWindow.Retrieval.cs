using System;
using System.Collections.Generic;
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
    private CancellationTokenSource? _searchLoads;
    private HashSet<string> _searchMatches = [];
    private bool _searchRunning, _focusSelection, _focusScheduled;
    private int? _dateDays;
    private string? _selectedId;
    private sealed record SearchStamp(string Fingerprint, string? Title, string[] Labels, bool Found)
    {
        internal bool Matches(ClipSummary entry) => Fingerprint == entry.Fingerprint && Title == entry.Title
            && Labels.SequenceEqual(entry.Labels ?? [], StringComparer.Ordinal);
    }
    private readonly Dictionary<string, SearchStamp> _searchCache = new();
    private string _cacheQuery = "";
    internal string? SelectedNoteId => _selectedId;

    internal async Task SearchNowAsync(string query)
    {
        SearchBox.Text = query; _searchTimer.Stop(); await RunSearchAsync();
    }

    private async Task RunSearchAsync()
    {
        _searchLoads?.Cancel(); _searchLoads?.Dispose();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(_loads.Token);
        _searchLoads = load;
        var cancellation = load.Token;
        var query = _query = SearchBox.Text.Trim();
        var failed = false;
        if (query != _cacheQuery) { _searchCache.Clear(); _cacheQuery = query; }
        var entries = _entries.ToDictionary(x => x.Id);
        foreach (var id in _searchCache.Keys.Where(id => !entries.ContainsKey(id)).ToArray()) _searchCache.Remove(id);
        _searchMatches = [];
        foreach (var entry in _entries)
        {
            if (_searchCache.TryGetValue(entry.Id, out var cached) && cached.Matches(entry))
            { if (cached.Found) _searchMatches.Add(entry.Id); continue; }
            _searchCache.Remove(entry.Id);
            if (HistorySearch.Contains(entry.Preview, query) || HistorySearch.Contains(entry.Title, query)
                || (entry.Labels?.Any(label => HistorySearch.Contains(label, query)) ?? false))
            { _searchMatches.Add(entry.Id); _searchCache[entry.Id] = new(entry.Fingerprint, entry.Title, (entry.Labels ?? []).ToArray(), true); }
        }
        _searchRunning = query.Length > 0 && !_preview;
        RebuildNotes();
        try
        {
            if (!_searchRunning) return;
            var candidates = _entries.Where(x => x.Kind != ClipKind.Image && !_searchCache.ContainsKey(x.Id)).Select(x => x.Id).ToArray();
            for (var offset = 0; offset < candidates.Length; offset += HistorySearch.BatchSize)
            {
                cancellation.ThrowIfCancellationRequested();
                var batch = candidates.Skip(offset).Take(HistorySearch.BatchSize).ToArray();
                var matches = await Task.Run(() => _store.SearchBatchAsync(query, batch, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (Retired || _closing || !ReferenceEquals(_searchLoads, load)) return;
                var found = matches.ToHashSet();
                foreach (var id in batch)
                {
                    var entry = entries[id];
                    _searchCache[id] = new(entry.Fingerprint, entry.Title, (entry.Labels ?? []).ToArray(), found.Contains(id));
                }
                if (matches.Count > 0) { _searchMatches.UnionWith(matches); RebuildNotes(); }
                // Yield the serialized pipe to urgent state/restore/preview calls.
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { failed = true; }
        finally
        {
            if (ReferenceEquals(_searchLoads, load))
            {
                _searchLoads = null; _searchRunning = false;
                if (!cancellation.IsCancellationRequested && !Retired && !_closing)
                { RebuildNotes(); if (failed) SetStatus("Some saved text could not be searched. Try again."); }
            }
        }
    }

    private void OnDateFilter(object sender, RoutedEventArgs e)
    {
        _dateDays = _dateDays switch { null => 1, 1 => 7, _ => null };
        DateFilterButton.Content = _dateDays switch { 1 => "Last 24 hours", 7 => "Last 7 days", _ => "Any time" };
        RebuildNotes(); ScrollTo(0);
    }

    internal void SelectNote(int index)
    {
        if (Retired || _closing || _visible.Count == 0) return;
        index = Math.Clamp(index, 0, _visible.Count - 1);
        _selectedId = _visible[index].Id; _focusSelection = true;
        var left = 24 + index * Pitch; var right = left + _appearance.NoteWidth;
        var destination = left < ScrollOffset ? left - 24 : right > ScrollOffset + NoteScroll.ViewportWidth
            ? right - NoteScroll.ViewportWidth + 24 : ScrollOffset;
        UpdateSelectionVisuals();
        SetStatus($"Note {index + 1} of {_visible.Count} · {_visible[index].Kind} · Enter to paste · Shift + Enter to copy");
        if (Math.Abs(destination - ScrollOffset) < 1) FocusSelectedNote(); else ScrollTo(destination);
    }

    private void UpdateSelectionVisuals()
    {
        foreach (var card in _cards.Values)
            if (card.Children[1] is Border paper)
            {
                var binding = _bindings[card]; var combined = _combinationIds.Contains(binding.Entry.Id);
                var state = (binding.Entry.Id == _selectedId, combined);
                if (binding.AccentState == state) continue;
                binding.AccentState = state;
                paper.SetResourceReference(Border.BorderBrushProperty, binding.Entry.Id == _selectedId || combined ? "Accent" : "Outline");
                paper.BorderThickness = new Thickness(combined ? 2 : 1);
                InvalidateNativeNote(card);
                if (binding.Combine is not null)
                { binding.Combine.Content = combined ? "✓" : "+"; System.Windows.Automation.AutomationProperties.SetName(binding.Combine, combined ? "Remove text from combination" : "Select text for combination"); }
            }
    }

    private void FocusSelectedNote()
    {
        if (!_focusSelection || _focusScheduled || Retired || _closing || _scrollRunning) return;
        _focusScheduled = true;
        // ScrollViewer queues its layout commit. Focusing against the old origin
        // makes its automatic BringIntoView jump back toward the start.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            _focusScheduled = false;
            if (!_focusSelection || Retired || _closing || _scrollRunning
                || SidebarHost.Visibility == Visibility.Visible || DetailHost.Visibility == Visibility.Visible
                || Math.Abs(NoteScroll.HorizontalOffset - ScrollOffset) >= 1) return;
            var binding = _cards.Values.Select(card => _bindings[card]).FirstOrDefault(x => x.Entry.Id == _selectedId);
            if (binding is null) return;
            _focusSelection = false; binding.Select.Focus();
        }));
    }
}
