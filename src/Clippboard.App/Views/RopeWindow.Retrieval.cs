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
    private readonly SemaphoreSlim _searchPlanning = new(1, 1);
    private Dictionary<string, SearchStamp> _searchCache = new();
    private string _cacheQuery = "";
    internal string? SelectedNoteId => _selectedId;

    internal async Task SearchNowAsync(string query)
    {
        SearchBox.Text = query; _searchTimer.Stop(); await RunSearchAsync();
    }

    private sealed record SearchPlan(Dictionary<string, SearchStamp> Stamps,
        HashSet<string> Matches, List<ClipSummary> Candidates);

    private async Task<SearchPlan> PlanSearchAsync(IReadOnlyList<ClipSummary> entries,
        IReadOnlyDictionary<string, SearchStamp>? prior, string query, CancellationToken cancellation)
    {
        // Only one metadata scan may occupy a worker. Superseded plans cancel
        // before starting, and the worker never touches WPF or reads payloads.
        await _searchPlanning.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var plan = new SearchPlan(new(), new(), new());
                foreach (var entry in entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (prior is not null && prior.TryGetValue(entry.Id, out var cached) && cached.Matches(entry))
                    {
                        plan.Stamps.Add(entry.Id, cached);
                        if (cached.Found) plan.Matches.Add(entry.Id);
                    }
                    else if (HistorySearch.Contains(entry.Preview, query) || HistorySearch.Contains(entry.Title, query)
                        || (entry.Labels?.Any(label => HistorySearch.Contains(label, query)) ?? false))
                    {
                        plan.Matches.Add(entry.Id);
                        plan.Stamps.Add(entry.Id, new(entry.Fingerprint, entry.Title, (entry.Labels ?? []).ToArray(), true));
                    }
                    else if (entry.Kind != ClipKind.Image) plan.Candidates.Add(entry);
                }
                return plan;
            }, cancellation).ConfigureAwait(false);
        }
        finally { _searchPlanning.Release(); }
    }

    private async Task RunSearchAsync()
    {
        if (Retired || _closing) return;
        _searchLoads?.Cancel();
        using var load = CancellationTokenSource.CreateLinkedTokenSource(_loads.Token);
        _searchLoads = load;
        var cancellation = load.Token;
        var query = SearchBox.Text.Trim();
        var failed = false;
        try
        {
            if (query.Length == 0)
            {
                _query = _cacheQuery = "";
                _searchCache = new(); _searchMatches = []; _searchRunning = false;
                return;
            }
            // Catalog snapshots and earlier stamps are read-only during planning.
            // All later cache writes require this exact, uncanceled UI generation.
            var plan = await PlanSearchAsync(_entries, query == _cacheQuery ? _searchCache : null, query, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (Retired || _closing || !ReferenceEquals(_searchLoads, load)) return;
            _query = _cacheQuery = query;
            _searchCache = plan.Stamps;
            _searchMatches = plan.Matches;
            _searchRunning = !_preview && plan.Candidates.Count > 0;
            if (!_searchRunning) return;
            RebuildNotes();
            for (var offset = 0; offset < plan.Candidates.Count; offset += HistorySearch.BatchSize)
            {
                cancellation.ThrowIfCancellationRequested();
                var batch = new string[Math.Min(HistorySearch.BatchSize, plan.Candidates.Count - offset)];
                for (var index = 0; index < batch.Length; ++index) batch[index] = plan.Candidates[offset + index].Id;
                var matches = await Task.Run(() => _store.SearchBatchAsync(query, batch, cancellation), cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (Retired || _closing || !ReferenceEquals(_searchLoads, load)) return;
                var found = matches.ToHashSet();
                for (var index = 0; index < batch.Length; ++index)
                {
                    var entry = plan.Candidates[offset + index];
                    _searchCache[entry.Id] = new(entry.Fingerprint, entry.Title, (entry.Labels ?? []).ToArray(), found.Contains(entry.Id));
                }
                if (matches.Count > 0) { _searchMatches.UnionWith(matches); RebuildNotes(); }
                // Give input and rendering a turn before queuing more payload I/O.
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or System.Security.Cryptography.CryptographicException)
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
