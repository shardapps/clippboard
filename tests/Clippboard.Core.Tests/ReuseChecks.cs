using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using Clippboard.Core;
using Clippboard.App.Platform;
using Clippboard.App.Services;
using Clippboard.App.Views;

internal static partial class Program
{
    private static async Task CheckTextReuseAsync(string root)
    {
        const string original = " B\r\nA\r\nA\r\nاردو ";
        Check(TextReuse.Transform(original, TextTransform.Original) == original, "original transformation preserves exact line endings");
        Check(TextReuse.Transform("  hello  ", TextTransform.TrimEdges) == "hello", "trim preview removes only outer whitespace");
        Check(TextReuse.Transform("Hello اردو", TextTransform.Uppercase) == "HELLO اردو"
            && TextReuse.Transform("HELLO", TextTransform.Lowercase) == "hello", "case transforms support Unicode without mutating the source");
        Check(TextReuse.Transform("a\t \r\nb", TextTransform.CollapseWhitespace) == "a b", "whitespace transformation has a predictable preview");
        Check(TextReuse.Transform("B\r\nA\rA", TextTransform.SortLines) == "A\nA\nB", "line sorting handles mixed newline styles");
        Check(TextReuse.Transform("B\nA\nB\na", TextTransform.RemoveDuplicateLines) == "B\nA\na", "unique lines preserve first occurrence, order and case");
        var rejected = false;
        try { TextReuse.Transform(new string('x', TextReuse.MaxTextLength + 1), TextTransform.TrimEdges); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "transformation rejects oversized input before work");
        Check(TextReuse.Labels("work, Work, short").SequenceEqual(["work", "short"]), "labels trim and deduplicate with stable order");
        var directory = Path.Combine(root, "snippets"); var limits = new HistoryLimits(3, 100000, 30, 20000);
        var history = new HistoryStore(directory, new WindowsProtector(), limits); await history.InitializeAsync();
        var captured = new Clip { Kind = ClipKind.Text, Text = original }; await history.AddAsync(captured);
        Check(await history.SaveSnippetAsync(null, original, "Reply", ["work"]), "text copy can become an encrypted snippet");
        var snippet = (await history.ListAsync()).Single(x => x.IsSnippet);
        Check(snippet.Pinned && snippet.Title == "Reply" && snippet.Labels!.SequenceEqual(["work"])
            && (await history.GetAsync(captured.Id))!.Text == original, "saving snippet preserves the captured source and stars the new entry");
        Check(await history.SaveSnippetAsync(null, original, "Other reply", ["personal"]), "same-body snippets can have separate titles and identities");
        Check(await history.SaveSnippetAsync(snippet.Id, "Updated reply", "Work reply", ["work", "short"])
            && (await history.ListAsync()).Count == 3 && await history.GetAsync(captured.Id) is not null,
            "editing at entry capacity replaces exactly one snippet without evicting original");
        var edited = (await history.GetAsync(snippet.Id))!;
        Check(edited.CopiedAt == snippet.CopiedAt && edited.Text == "Updated reply", "editing a snippet does not reset its retention clock");
        IHistoryAccess access = history;
        Check((await access.SearchBatchAsync("short", [snippet.Id])).Count == 1
            && (await access.SearchBatchAsync("Work reply", [snippet.Id])).Count == 1, "search includes snippet titles and labels");
        rejected = false;
        try { await history.SaveSnippetAsync(captured.Id, "overwrite source", "", []); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && (await history.GetAsync(captured.Id))!.Text == original, "snippet editing cannot overwrite an ordinary captured note");
        rejected = false;
        try { await history.SaveSnippetAsync(snippet.Id, new string('x', 30000), "Oversized", []); } catch (ArgumentException) { rejected = true; }
        Check(!rejected && (await history.GetAsync(snippet.Id))!.Text == "Updated reply", "entry-byte rejection preserves existing snippet content");
        await history.DeleteAsync(snippet.Id); rejected = false;
        try { await history.SaveSnippetAsync(snippet.Id, "revived", "", []); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && await history.GetAsync(snippet.Id) is null, "deleted snippet cannot be resurrected by stale editor save");
        var old = new Clip { Kind = ClipKind.Text, Text = "old snippet", IsSnippet = true, Pinned = true, CopiedAt = DateTimeOffset.UtcNow.AddDays(-10) };
        await history.AddAsync(old); await history.SaveSnippetAsync(old.Id, "edited old snippet", "Old", []);
        await history.UpdateLimitsAsync(limits with { RetentionDays = 7 });
        Check(await history.GetAsync(old.Id) is null, "edited starred snippets obey reduced retention");

        var largeText = new string('語', TextReuse.MaxTextLength);
        using var frame = new MemoryStream(); await OverlayTransport.WriteAsync(frame, new OverlayRequest("save-snippet", Text: largeText), OverlayTransport.RequestLimit, CancellationToken.None);
        frame.Position = 0;
        Check((await OverlayTransport.ReadAsync<OverlayRequest>(frame, OverlayTransport.RequestLimit, CancellationToken.None)).Text == largeText,
            "maximum Unicode text edit crosses bounded request framing without truncation");
        CheckTextEditorVisuals();
    }
    private static void CheckTextEditorVisuals()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Window? window = null;
            try
            {
                const string original = "  B\nA\nA  ";
                var view = new TextEditorView(original, "", [], false, false);
                window = new Window { Content = view, Width = 800, Height = 620, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
                window.Show(); window.UpdateLayout();
                Pump(view.TransformAsync(TextTransform.TrimEdges)); Check(view.PreviewText == "B\nA\nA", "editor displays transformation before copying");
                Pump(view.TransformAsync(TextTransform.Original)); Check(view.PreviewText == original, "editor restores exact original on request");
                var sort = view.TransformAsync(TextTransform.SortLines); var restore = view.TransformAsync(TextTransform.Original);
                Pump(Task.WhenAll(sort, restore)); Check(view.PreviewText == original, "obsolete transform cannot overwrite latest preview choice");
                var fields = Descendants<TextBox>(view).ToArray();
                var editor = fields.Single(box => AutomationProperties.GetName(box).StartsWith("Editable text"));
                editor.Text = new string('x', 400000);
                var pending = view.TransformAsync(TextTransform.Uppercase); editor.Text = "user typed this"; Pump(pending);
                Check(view.PreviewText == "user typed this", "typing during background transformation preserves user's new edit");
                fields.Single(box => AutomationProperties.GetName(box) == "Snippet title").Text = "Reply";
                fields.Single(box => AutomationProperties.GetName(box).StartsWith("Comma-separated")).Text = "work, short";
                var saved = false; view.SaveRequested += (text, title, labels, newCopy) =>
                { saved = text == "user typed this" && title == "Reply" && labels.SequenceEqual(["work", "short"]) && newCopy; return Task.FromResult<string?>(null); };
                Descendants<Button>(view).Single(button => button.Content as string == "Save as snippet").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(saved, "editor submits only explicit save with reviewed text and labels");
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Text editor checks failed", failure);
    }
}
