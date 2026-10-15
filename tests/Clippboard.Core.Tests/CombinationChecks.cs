using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Clippboard.Core;
using Clippboard.App.Platform;
using Clippboard.App.Views;

internal static partial class Program
{
    private sealed class CombinationHistory(params Clip[] clips) : IHistoryAccess
    {
        public Task<IReadOnlyList<ClipSummary>> ListAsync() => Task.FromResult<IReadOnlyList<ClipSummary>>(clips.Select(x => x.Summary(0)).ToArray());
        public Task<Clip?> GetAsync(string id) => Task.FromResult(clips.FirstOrDefault(x => x.Id == id));
        public Task SetPinnedAsync(string id, bool pinned) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
    private static async Task CheckCombinationAsync(string root)
    {
        var a = new Clip { Text = "A\r\nB", CopiedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var b = new Clip { Text = "اردو 🧵" };
        var store = new HistoryStore(Path.Combine(root, "combine"), new WindowsProtector()); await store.InitializeAsync(); await store.AddAsync(a); await store.AddAsync(b);
        Check(await TextCombination.BuildAsync(store, [b.Id, a.Id], " | ") == b.Text + " | " + a.Text,
            "combination preserves chosen order, separator, Unicode and original line endings");
        Check((await store.GetAsync(a.Id))!.Text == a.Text && (await store.GetAsync(b.Id))!.Text == b.Text,
            "combination preview cannot alter original clips");
        var failed = false;
        try { await TextCombination.BuildAsync(store, [a.Id, a.Id], ""); } catch (ArgumentException) { failed = true; }
        Check(failed, "duplicate combination identity rejected instead of silently duplicating text");
        await store.DeleteAsync(a.Id); failed = false;
        try { await TextCombination.BuildAsync(store, [b.Id, a.Id], ""); } catch (IOException) { failed = true; }
        Check(failed, "expired/deleted selected note does not produce a misleading partial result");
        var empty = new Clip { Text = "" };
        Check(await TextCombination.BuildAsync(new CombinationHistory(empty, b), [empty.Id, b.Id], "|") == "|" + b.Text,
            "separator semantics remain correct with an empty leading source");
        var large = new Clip { Text = new string('x', TextReuse.MaxTextLength) }; failed = false;
        try { await TextCombination.BuildAsync(new CombinationHistory(large, b), [large.Id, b.Id], ""); } catch (ArgumentException) { failed = true; }
        Check(failed, "aggregate text bound is checked before growing the result");
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); failed = false;
        try { await TextCombination.BuildAsync(store, [b.Id], "", canceled.Token); } catch (OperationCanceledException) { failed = true; }
        Check(failed, "canceled combination stops before another payload read");
        var image = new Clip { Kind = ClipKind.Image, ImagePng = [1, 2, 3] }; failed = false;
        try { await TextCombination.BuildAsync(new CombinationHistory(image), [image.Id], ""); } catch (ArgumentException) { failed = true; }
        Check(failed && image.ImagePng!.All(x => x == 0), "unexpected image source is rejected and its owned buffer cleared");
        CheckCombinationVisuals(a, b);
    }
    private static void CheckCombinationVisuals(Clip a, Clip b)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Window? host = null; RopeWindow? rope = null;
            try
            {
                var entries = new[] { a.Summary(0), b.Summary(0) };
                var text = new Dictionary<string, string> { [a.Id] = a.Text!, [b.Id] = b.Text! };
                var view = new CombinationView(entries, (ids, separator, _) => Task.FromResult(string.Join(separator, ids.Select(id => text[id]))), CancellationToken.None);
                host = new Window { Content = view, Width = 820, Height = 620, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
                host.Show(); Pump(view.RebuildAsync());
                Check(view.PreviewText == a.Text + "\n\n" + b.Text, "combination view displays selection order before copying");
                Descendants<Button>(view).Single(x => x.Content as string == "Newest first").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(view.PreviewText == b.Text + "\n\n" + a.Text, "chronological order control changes preview");
                Descendants<Button>(view).Single(x => x.Content as string == "Comma").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(view.PreviewText == b.Text + ", " + a.Text, "separator button changes reviewed result");
                var copied = ""; view.CopyRequested += value => { copied = value; return Task.CompletedTask; };
                Descendants<Button>(view).Single(x => x.Content as string == "Copy combined text").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(copied == view.PreviewText, "explicit copy receives exactly the reviewed combined text");
                host.Close(); host = null;
                var picker = new LabelPickerView(Enumerable.Range(0, 8000).Select(i => $"label-{i:D4}").ToArray(), null);
                host = new Window { Content = picker, Width = 460, Height = 500, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
                host.Show(); host.UpdateLayout();
                Check(Descendants<ListBoxItem>(picker).Count() < 50, "large label picker keeps its item controls virtualized");
                Descendants<TextBox>(picker).Single().Text = "label-7999";
                var labels = Descendants<ListBox>(picker).Single();
                Check(labels.Items.Count == 1, "label search reaches far entries without cycling thousands of buttons");
                string? chosen = null; picker.Chosen += label => chosen = label; labels.SelectedIndex = 0;
                var enter = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, System.Windows.Input.Key.Enter)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                labels.RaiseEvent(enter); Check(chosen == "label-7999" && enter.Handled, "label picker supports explicit keyboard apply");
                host.Close(); host = null;
                rope = new RopeWindow(new CombinationHistory(), preview: true, settings: new AppSettings { Animate = false })
                    { Width = 1200, Height = 720, Left = -20000, Top = -20000, ShowActivated = false, Topmost = false };
                rope.Show(); rope.RenderDemo(1000); rope.UpdateLayout();
                Descendants<Button>(rope).First(x => AutomationProperties.GetName(x) == "Select text for combination" && x.IsVisible).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(rope.CombinationCount == 1, "note footer selects its current bound text identity");
                rope.SelectNote(999); rope.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(!Descendants<Button>(rope).Any(x => x.IsVisible && x.Content as string == "✓"), "recycled note cannot inherit another note's combination state");
                rope.SelectNote(0); rope.UpdateLayout(); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(Descendants<Button>(rope).Any(x => x.IsVisible && AutomationProperties.GetName(x) == "Remove text from combination"),
                    "returning to selected note restores visible and accessible combination state");
            }
            catch (Exception ex) { failure = ex; }
            finally { host?.Close(); rope?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Combination visual checks failed", failure);
    }
}
