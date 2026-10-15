using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Clippboard.Core;
using Clippboard.App.Platform;
using Clippboard.App.Views;

internal static partial class Program
{
    private static async Task CheckFullSearchAsync(string root)
    {
        var directory = Path.Combine(root, "full-search");
        var store = new HistoryStore(directory, new WindowsProtector()); await store.InitializeAsync();
        var text = new Clip { Kind = ClipKind.Text, Text = new string('x', 2000) + " Résumé\nاردو 🧵 tailNeedle" };
        var files = new Clip { Kind = ClipKind.Files, FilePaths = Enumerable.Range(0, 8).Select(i => "C:\\fixture\\item-" + i + ".txt").Append("C:\\originals\\deep\\hiddenNeedle.txt").ToArray() };
        await store.AddAsync(text); await store.AddAsync(files);
        IHistoryAccess access = store;
        Check((await access.SearchBatchAsync("TAILNEEDLE", [text.Id])).SequenceEqual([text.Id]), "search finds case-insensitive text beyond short preview");
        Check((await access.SearchBatchAsync("hiddenNeedle", [files.Id])).SequenceEqual([files.Id]), "search includes file names beyond the five previewed originals");
        Check((await access.SearchBatchAsync("originals\\deep", [files.Id])).Count == 1, "file search includes original paths without filesystem access");
        Check((await access.SearchBatchAsync("اردو 🧵", [text.Id])).Count == 1, "full-text search handles Urdu and emoji");
        Check(HistorySearch.Contains("Résumé", "Re\u0301sume\u0301"), "canonical Unicode equivalents match without rewriting original text");
        var rejected = false;
        try { await access.SearchBatchAsync(new string('q', 101), [text.Id]); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "oversized query is rejected before history reads");
        rejected = false;
        try { await access.SearchBatchAsync("needle", Enumerable.Repeat(text.Id, HistorySearch.BatchSize + 1).ToArray()); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "search batch has a bounded work size");
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); rejected = false;
        try { await access.SearchBatchAsync("needle", [text.Id], canceled.Token); } catch (OperationCanceledException) { rejected = true; }
        Check(rejected, "canceled search stops before reading a payload");
        await store.DeleteAsync(text.Id);
        Check((await access.SearchBatchAsync("needle", [text.Id])).Count == 0, "deletion during search does not create phantom matches");
        Check(Directory.GetFiles(directory).All(file => Path.GetExtension(file) is ".clip" or ".meta"), "full-text search creates no plaintext index or temporary corpus");
        CheckRetrievalVisuals();
    }

    private sealed class RetrievalHistory : IHistoryAccess
    {
        internal Clip Item = new() { Kind = ClipKind.Text, Text = new string('x', 2000) + " oldneedle" };
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        internal int Reads => Volatile.Read(ref _reads);
        public Task<IReadOnlyList<ClipSummary>> ListAsync() => Task.FromResult<IReadOnlyList<ClipSummary>>([Item.Summary(0)]);
        public async Task<Clip?> GetAsync(string id)
        {
            if (Interlocked.Increment(ref _reads) == 1) { Started.TrySetResult(); await Release.Task; }
            return Item;
        }
        public Task SetPinnedAsync(string id, bool pinned) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }

    private static void CheckRetrievalVisuals()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
                System.Windows.Threading.Dispatcher.CurrentDispatcher));
            RopeWindow? window = null;
            try
            {
                var history = new RetrievalHistory();
                window = new RopeWindow(history, settings: new AppSettings { Animate = false });
                Pump(window.RefreshAsync());
                var oldSearch = window.SearchNowAsync("oldneedle"); Pump(history.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)));
                Pump(window.SearchNowAsync("absent-query"));
                Check(window.VisibleNoteCount == 0, "new search completes while obsolete payload read is pending");
                history.Release.TrySetResult(); Pump(oldSearch);
                Check(window.VisibleNoteCount == 0, "late canceled search cannot replace current results");
                Pump(window.SearchNowAsync("oldneedle"));
                Check(window.VisibleNoteCount == 1, "full-text match appears in rope even when absent from summary");
                var reads = history.Reads; Pump(window.SearchNowAsync("oldneedle"));
                Check(history.Reads == reads, "unchanged positive search reuses bounded metadata result without reading full payload again");
                Pump(window.SearchNowAsync("work")); reads = history.Reads; Pump(window.RefreshAsync());
                Check(history.Reads == reads && window.VisibleNoteCount == 0, "unchanged negative search also avoids repeated decrypt/scan");
                history.Item = history.Item with { Labels = ["work"] }; Pump(window.RefreshAsync());
                Check(window.VisibleNoteCount == 1, "changed label invalidates result even when body fingerprint stays unchanged");
                history.Item = history.Item with { Labels = ["personal"] }; Pump(window.RefreshAsync());
                Check(window.VisibleNoteCount == 0, "removed label cannot leave stale positive search match");
                window.Close();

                window = new RopeWindow(history, preview: true, settings: new AppSettings { Animate = false })
                    { Width = 1200, Height = 720, Left = -20000, Top = -20000, ShowActivated = false, Topmost = false };
                window.Show(); window.RenderDemo(1000); window.UpdateLayout();
                window.SelectNote(0); var first = window.SelectedNoteId;
                window.SelectNote(999); window.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(window.SelectedNoteId != first && window.SelectedNoteId is not null && window.ScrollOffset > 300000,
                    $"keyboard selection reaches last note through virtualized history (first={first}, selected={window.SelectedNoteId}, offset={window.ScrollOffset}, visible={window.VisibleNoteCount})");
                Check(window.RenderedNoteCount < 10, "keyboard navigation does not materialize all note controls");
                Pump(window.SearchNowAsync("Idea 900")); window.UpdateLayout();
                Check(window.VisibleNoteCount == 1 && window.SelectedNoteId is null, "filtering removes obsolete selected identity");
                window.SelectNote(0);
                Check(window.SelectedNoteId is not null, "single filtered result can be selected by keyboard");
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var restored = 0; window.Selected += _ => { ++restored; return Task.CompletedTask; };
                var source = System.Windows.PresentationSource.FromVisual(window);
                var toolbar = (Button)window.FindName("AllButton");
                var toolbarEnter = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source!, 0, System.Windows.Input.Key.Enter)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                toolbar.RaiseEvent(toolbarEnter);
                Check(!toolbarEnter.Handled && restored == 0, "Enter on toolbar preserves its native button action");
                var note = Descendants<Button>(window).First(button => System.Windows.Automation.AutomationProperties.GetName(button).StartsWith("Restore "));
                var noteEnter = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source!, 0, System.Windows.Input.Key.Enter)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                note.RaiseEvent(noteEnter);
                Check(noteEnter.Handled && restored == 1, "Enter on selected note requests exactly one restore");
                var date = (Button)window.FindName("DateFilterButton"); date.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check((string)date.Content == "Last 24 hours", "date filter provides a rolling last-day view");
                var status = (TextBlock)window.FindName("Status");
                Check(System.Windows.Automation.AutomationProperties.GetLiveSetting(status) == System.Windows.Automation.AutomationLiveSetting.Polite,
                    "result and selection status has a polite screen-reader live region");
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Retrieval visual checks failed", failure);
    }
}
