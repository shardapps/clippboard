using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using Clippboard.Core;
using Clippboard.App.Services;
using Clippboard.App.Views;

internal static partial class Program
{
    private sealed class FixtureOcr : IOcrBackend
    {
        internal Func<byte[], CancellationToken, Task<string>> Read = (_, _) => Task.FromResult("Synthetic text\nاردو 🧵");
        internal int LanguageReads;
        public IReadOnlyList<OcrLanguage> Languages() { LanguageReads++; return [new("en-US", "English (United States)")]; }
        public Task<string> RecognizeAsync(byte[] png, string language, CancellationToken cancellation) => Read(png, cancellation);
    }
    private static async Task CheckOcrAsync()
    {
        static byte[] Png() => [137, 80, 78, 71, 13, 10, 26, 10, 42];
        var backend = new FixtureOcr(); var unpackaged = new LocalOcr(backend, () => false); var loads = 0;
        Task<Clip?> Load() { loads++; return Task.FromResult<Clip?>(new() { Kind = ClipKind.Image, ImagePng = Png() }); }
        Check(unpackaged.Availability().Message!.Contains("Microsoft Store", StringComparison.Ordinal) && backend.LanguageReads == 0,
            "unpackaged OCR explains package requirement without calling unsupported native APIs");
        var failed = false;
        try { await unpackaged.ExtractAsync(Load, "en-US", CancellationToken.None); } catch (OcrUnavailableException) { failed = true; }
        Check(failed && loads == 0, "unavailable OCR never fetches image payload");
        var service = new LocalOcr(backend, () => true);
        failed = false;
        try { await service.ExtractAsync(Load, "missing", CancellationToken.None); } catch (OcrUnavailableException) { failed = true; }
        Check(failed && loads == 0, "unsupported language is rejected before image loading");
        var png = Png();
        Check(await service.ExtractAsync(() => Task.FromResult<Clip?>(new() { Kind = ClipKind.Image, ImagePng = png }), "en-US", CancellationToken.None) == "Synthetic text\nاردو 🧵"
            && png.All(value => value == 0), "successful local extraction preserves Unicode lines and wipes owned PNG");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Read = (_, _) => { entered.TrySetResult(); return finish.Task; };
        png = Png(); using var cancellation = new CancellationTokenSource();
        var current = service.ExtractAsync(() => Task.FromResult<Clip?>(new() { Kind = ClipKind.Image, ImagePng = png }), "en-US", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        failed = false;
        try { await service.ExtractAsync(Load, "en-US", CancellationToken.None); } catch (OcrUnavailableException) { failed = true; }
        Check(failed && loads == 0 && png[0] == 137, "canceling view retains one native slot and valid pixels until the in-flight job finishes");
        finish.TrySetResult("obsolete result"); failed = false;
        try { await current; } catch (OperationCanceledException) { failed = true; }
        Check(failed && png.All(value => value == 0), "late recognition cannot publish canceled result and releases payload");
        backend.Read = (_, _) => Task.FromResult(" "); failed = false;
        try { await service.ExtractAsync(Load, "en-US", CancellationToken.None); } catch (OcrUnavailableException ex) { failed = ex.Message.Contains("No text", StringComparison.Ordinal); }
        Check(failed && loads == 1, "empty recognition gives an actionable result and the worker slot recovers");
        backend.Read = (_, _) => Task.FromResult(new string('x', TextReuse.MaxTextLength + 1)); failed = false;
        try { await service.ExtractAsync(Load, "en-US", CancellationToken.None); } catch (OcrUnavailableException) { failed = true; }
        Check(failed, "oversized recognition is rejected before editor publication");
        backend.Read = (_, _) => throw new InvalidOperationException("synthetic internal details"); failed = false;
        try { await service.ExtractAsync(Load, "en-US", CancellationToken.None); } catch (OcrUnavailableException ex) { failed = !ex.Message.Contains("internal", StringComparison.Ordinal); }
        Check(failed, "native OCR failure becomes a content-free user notice");
        CheckOcrVisuals();
    }
    private static void CheckOcrVisuals()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); Window? window = null;
            try
            {
                var calls = 0; var result = "";
                var view = new OcrView(new([new("en-US", "English")]), (_, _) => { calls++; return Task.FromResult("Reviewed text"); });
                view.Recognized += text => { result = text; return Task.CompletedTask; };
                window = new Window { Content = view, Width = 750, Height = 500, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
                window.Show(); window.UpdateLayout();
                Check(calls == 0 && Descendants<ListBox>(view).Single().Items.Count == 1, "OCR panel lists local language choices without automatically extracting");
                Pump(view.ExtractAsync()); Check(calls == 1 && result == "Reviewed text", "explicit extraction opens reviewable text without automatic save or paste");
                var finish = new TaskCompletionSource<string>();
                var cancelView = new OcrView(new([new("en-US", "English")]), (_, _) => finish.Task);
                var published = false; cancelView.Recognized += _ => { published = true; return Task.CompletedTask; };
                window.Content = cancelView; window.UpdateLayout(); var pending = cancelView.ExtractAsync();
                window.Content = null; finish.SetResult("late result"); Pump(pending);
                Check(!published, "unloading OCR panel prevents stale results from replacing another view");
                var unavailable = new OcrView(new([], "Missing language"), (_, _) => throw new InvalidOperationException()); window.Content = unavailable; window.UpdateLayout();
                Check(!Descendants<Button>(unavailable).Single(button => button.Content as string == "Extract text").IsEnabled,
                    "missing OCR components leave extraction disabled with a visible explanation");
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); if (failure is not null) throw failure;
    }
}
