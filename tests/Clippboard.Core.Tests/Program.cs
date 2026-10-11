using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clippboard.App.Services;
using Clippboard.App.Platform;
using Clippboard.App.Views;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Clippboard.Core;
using Clippboard.App.Design;

// A small executable test suite; assertions exercise storage invariants and real Windows DPAPI.
internal static class Program
{
    private static int _assertions;
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    private static async Task CheckBoundedReaderAsync()
    {
        var apartment = ApartmentState.Unknown;
        using var quick = new BoundedClipboardReader(_ => { apartment = Thread.CurrentThread.GetApartmentState(); return new Clip { Kind = ClipKind.Text, Text = "STA fixture" }; });
        Check((await quick.ReadAsync(new()))?.Text == "STA fixture" && apartment == ApartmentState.STA, "clipboard read runs on separate STA");
        using var release = new ManualResetEventSlim(false); var calls = 0; var bytes = new byte[]{1,2,3};
        using var stalled = new BoundedClipboardReader(_ => { Interlocked.Increment(ref calls); release.Wait(); return new Clip { Kind = ClipKind.Image, ImagePng = bytes }; }, TimeSpan.FromMilliseconds(60));
        var timedOut = false;
        try { await stalled.ReadAsync(new()); } catch (IOException) { timedOut = true; }
        Check(timedOut && stalled.HasOutstandingRead, "stalled clipboard owner returns deadline failure without blocking caller");
        for (var i = 0; i < 10; i++) { try { await stalled.ReadAsync(new()); } catch (IOException) { } }
        Check(calls == 1, "retries cannot spawn additional blocked clipboard workers");
        release.Set();
        for (var i = 0; i < 100 && stalled.HasOutstandingRead; i++) await Task.Delay(10);
        Check(!stalled.HasOutstandingRead && bytes.All(x => x == 0), "late image result discarded and worker released");
        Check(await stalled.ReadAsync(new()) is not null && calls == 2, "reads resume after owner recovers");
        stalled.Dispose(); var disposed = false;
        try { await stalled.ReadAsync(new()); } catch (ObjectDisposedException) { disposed = true; }
        Check(disposed, "disposed clipboard reader rejects new work");
    }
    private static async Task CheckOverlayTransportAsync()
    {
        using var stream = new MemoryStream();
        await OverlayTransport.WriteAsync(stream, new OverlayRequest("pin", new string('a',32), true), OverlayTransport.RequestLimit, CancellationToken.None);
        stream.Position = 0;
        Check((await OverlayTransport.ReadAsync<OverlayRequest>(stream, OverlayTransport.RequestLimit, CancellationToken.None)).Flag, "overlay framed request roundtrip");
        using var oversized = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
        var rejected = false;
        try { await OverlayTransport.ReadAsync<OverlayRequest>(oversized, OverlayTransport.RequestLimit, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "overlay rejects forged frame length before allocation");
        using var truncated = new MemoryStream([8,0,0,0,1]); rejected = false;
        try { await OverlayTransport.ReadAsync<OverlayRequest>(truncated, OverlayTransport.RequestLimit, CancellationToken.None); } catch (EndOfStreamException) { rejected = true; }
        Check(rejected, "overlay rejects truncated message");
        using var server = new OverlayServer(request => Task.FromResult(request.Command == "state" ? new OverlayReply(Version: 42) : throw new InvalidOperationException()));
        var running = server.RunAsync();
        using var client = new OverlayClient(server.Name);
        Check((await client.CallAsync(new("state"))).Version == 42, "private current-user named pipe connects");
        rejected = false;
        try { await client.CallAsync(new("bad")); } catch (IOException) { rejected = true; }
        Check(rejected && (await client.CallAsync(new("state"))).Version == 42, "handler failure is recoverable and pipe remains usable");
        var concurrent = await Task.WhenAll(Enumerable.Range(0,10).Select(_ => client.CallAsync(new("state"))));
        Check(concurrent.All(x => x.Version == 42), "concurrent overlay calls remain serialized and complete");
        using (var malicious = new System.IO.Pipes.NamedPipeClientStream(".",server.Name,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly))
        {
            await malicious.ConnectAsync(3000); await malicious.WriteAsync(BitConverter.GetBytes(int.MaxValue)); await malicious.FlushAsync();
        }
        Check((await client.CallAsync(new("state"))).Version == 42, "malformed pipe client cannot stop subsequent requests");
        server.Dispose(); await running.WaitAsync(TimeSpan.FromSeconds(3));
        Check(running.IsCompletedSuccessfully, "pipe listener shuts down without orphaned workers");
    }
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--session") return await AppSessionChecks.RunAsync(args[1], args[2]);
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(), "Clippboard-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckFormats();
            await CheckBoundedReaderAsync();
            await CheckOverlayTransportAsync();
            await CheckFileProbeAsync(root);
            Check(MotionMath.Settle(0, true) == 0 && MotionMath.Settle(1, true) == 1, "spring rests at exact endpoints");
            Check(MotionMath.Sway(0) == 0 && MotionMath.Sway(1) == 1, "continuous sway rests at exact endpoints");
            Check(Enumerable.Range(0, 1001).Select(i => MotionMath.Sway(i / 1000.0)).Max() is > 1.2 and < 1.4, "natural note sway overshoot remains bounded");
            Check(Enumerable.Range(0, 101).Select(i => MotionMath.Settle(i / 100.0, true)).Max() is > 1 and < 1.2, "spring bounce bounded");
            Check(MotionMath.RopeHeight(0, 1600, 24) == 20 && MotionMath.RopeHeight(800, 1600, 24) == 44, "rope attachment curve");
            var range = MotionMath.VisibleRange(1000, 100000, 1600, 340);
            Check(range.Last - range.First < 9 && range.First > 200, "large history virtual range bounded");
            Check(MotionMath.VisibleRange(0, 0, 1600, 340).Last == -1, "empty virtualization range");
            Check(HorizontalWheel.Delta((nint)(-120 << 16)) == -120 && HorizontalWheel.Delta((nint)(5 << 16)) == 5, "horizontal input preserves signed and sub-tick deltas");
            Check(HorizontalWheel.ScreenX((nint)((-240 & 0xffff) | (-120 & 0xffff) << 16)) == -240 && HorizontalWheel.ScreenY((nint)((-240 & 0xffff) | (-120 & 0xffff) << 16)) == -120, "wheel coordinates support monitors left and above primary");
            Check(HorizontalWheel.Distance(5, 3, 1600) == 3 && HorizontalWheel.Distance(-120, 3, 1600) == -72, "small trackpad deltas scroll proportionally in both directions");
            Check(HorizontalWheel.Distance(120, uint.MaxValue, 1600) == 1600 && HorizontalWheel.Distance(120, 0, 1600) == 0, "horizontal input respects Windows page and disabled settings");
            var springStep = MotionMath.Spring(-100, 0, 0, .15, 20);
            var springRetarget = MotionMath.Spring(springStep.Position, springStep.Velocity, -200, 0, 20);
            Check(springRetarget == springStep, "retarget keeps spring position and velocity continuous");
            var springSmallSteps = (-100.0, 0.0);
            for (var step = 0; step < 120; step++) springSmallSteps = MotionMath.Spring(springSmallSteps.Item1, springSmallSteps.Item2, 0, 1.0 / 120, 20, .72);
            var springSingleStep = MotionMath.Spring(-100, 0, 0, 1, 20, .72);
            Check(Math.Abs(springSmallSteps.Item1 - springSingleStep.Position) < .00001 && Math.Abs(springSmallSteps.Item2 - springSingleStep.Velocity) < .00001, "spring solution independent of frame rate");
            var springStall = MotionMath.Spring(-100, 600, 0, 10, 20);
            Check(Math.Abs(springStall.Position) < .001 && Math.Abs(springStall.Velocity) < .001, "delayed spring frames remain finite and settle");
            Check(ClipboardService.ShortcutModifiers(ShortcutChoice.AltV) == 0x4001 && ClipboardService.ShortcutModifiers(ShortcutChoice.CtrlShiftV) == 0x4006, "shortcut modifiers include no-repeat");
            Console.WriteLine("Checking visual lifetime..."); CheckVisuals(root); Console.WriteLine("Checking storage...");
            var settingsFile = Path.Combine(root, "settings.json");
            var settingsStore = new SettingsStore(settingsFile);
            Check((await settingsStore.LoadAsync()).Theme == AppTheme.Cloud, "white theme default");
            var normalized = (new AppSettings { RetentionDays = 90, PreviewCacheMiB = 1000, NoteSpacing = -10, Theme = (AppTheme)999 }).Normalize();
            Check(normalized.RetentionDays == 30 && normalized.PreviewCacheMiB == 64 && normalized.NoteSpacing == 24 && normalized.Theme == AppTheme.Cloud, "settings bounds");
            await settingsStore.SaveAsync(new AppSettings { Theme = AppTheme.Midnight, RetentionDays = 7, AutoPaste = false });
            Check((await settingsStore.LoadAsync()).RetentionDays == 7 && !(await settingsStore.LoadAsync()).AutoPaste, "settings persistence");
            Check(!(await settingsStore.LoadAsync()).LowMemoryRendering, "existing settings keep smooth hardware rendering preference");
            await settingsStore.SaveAsync((await settingsStore.LoadAsync()) with { LowMemoryRendering = true });
            Check((await settingsStore.LoadAsync()).LowMemoryRendering, "low-RAM rendering preference persists for next overlay session");
            await File.WriteAllTextAsync(settingsFile, "broken json");
            Check((await settingsStore.LoadAsync()).RetentionDays == 30, "settings corruption fallback");
            Check(!new AppSettings { CaptureImages = false }.Captures(ClipKind.Image), "format opt out");
            var protector = new TestWindowsProtector();
            var store = new HistoryStore(root, protector, new HistoryLimits(3, 100000, 30, 20000));
            await store.InitializeAsync();
            var a = new Clip { Kind = ClipKind.Text, Text = "private-test-🧵-unicode" };
            Check(await store.AddAsync(a), "text accepted");
            Check((await store.GetAsync(a.Id))?.Text == a.Text, "Unicode roundtrip");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, a.Id + ".clip"))).Contains(a.Text), "no plaintext on disk");
            await store.SetPinnedAsync(a.Id, true);
            Check(await store.AddAsync(a with { Id = Guid.NewGuid().ToString("N") }), "duplicate accepted");
            Check((await store.ListAsync()).Count == 1 && (await store.ListAsync())[0].Pinned, "dedup preserves pin");
            var files = new Clip { Kind = ClipKind.Files, FilePaths = [@"C:\sample.txt", @"C:\example"] };
            await store.AddAsync(files);
            Check((await store.GetAsync(files.Id))!.FilePaths!.SequenceEqual(files.FilePaths!), "file refs roundtrip");
            var image = new Clip { Kind = ClipKind.Image, ImagePng = [1, 2, 3, 4, 5] };
            await store.AddAsync(image);
            Check((await store.GetAsync(image.Id))!.ImagePng!.SequenceEqual(image.ImagePng!), "binary roundtrip");
            await store.AddAsync(new Clip { Kind = ClipKind.Text, Text = "newest" });
            Check((await store.ListAsync()).Count == 3, "count cap");
            Check(await store.GetAsync(a.Id) is not null, "pin survives eviction");
            await store.AddAsync(new Clip { Kind = ClipKind.Text, Text = "expired", CopiedAt = DateTimeOffset.UtcNow.AddDays(-31) });
            await store.PruneAsync();
            Check((await store.ListAsync()).All(x => x.Preview != "expired"), "retention expiry");
            foreach (var entry in await store.ListAsync()) await store.SetPinnedAsync(entry.Id, true);
            while ((await store.ListAsync()).Count < 3)
            {
                var pin = new Clip { Kind = ClipKind.Text, Text = Guid.NewGuid().ToString(), Pinned = true };
                await store.AddAsync(pin);
            }
            Check(!await store.AddAsync(new Clip { Kind = ClipKind.Text, Text = "full" }), "reject when all slots pinned");
            Check((await store.ListAsync()).Count == 3, "rejection preserves history");
            Check(!await store.AddAsync(new Clip { Kind = ClipKind.Text, Text = new string('a', 22000) }), "oversize rejection");
            var reopened = new HistoryStore(root, protector, new HistoryLimits(3, 100000, 30, 20000));
            await reopened.InitializeAsync();
            Check((await reopened.ListAsync()).Count == 3, "restart persistence");
            await File.WriteAllBytesAsync(Path.Combine(root, Guid.NewGuid().ToString("N") + ".clip"), [0, 1, 2]);
            await reopened.InitializeAsync();
            Check(reopened.UnreadableEntries == 1, "corruption recovery");
            await reopened.ClearAsync();
            Check((await reopened.ListAsync()).Count == 0 && Directory.GetFiles(root, "*.clip").Length == 0, "clear includes pins");
            Check(Directory.GetFiles(root, "*.meta").Length == 0, "clear removes metadata pointers");
            var oldPin = new Clip { Kind = ClipKind.Text, Text = "old starred note", Pinned = true, CopiedAt = DateTimeOffset.UtcNow.AddDays(-10) };
            await reopened.AddAsync(oldPin);
            Check(await reopened.UpdateLimitsAsync(new HistoryLimits(3, 100000, 7, 20000)), "change retention");
            Check(await reopened.GetAsync(oldPin.Id) is null, "starred notes respect retention maximum");
            var largeClip = new Clip { Kind = ClipKind.Text, Text = new string('p', 15000) };
            await reopened.AddAsync(largeClip);
            protector.LargestUnprotected = 0;
            var pointers = new HistoryStore(root, protector, new HistoryLimits(3, 100000, 7, 20000));
            await pointers.InitializeAsync();
            Check(protector.LargestUnprotected < 4096, "restart loads small encrypted pointers, not payloads");
            Check((await pointers.GetAsync(largeClip.Id))?.Text?.Length == 15000, "payload loaded on demand");
            await pointers.SetPinnedAsync(largeClip.Id, true);
            Check(!await pointers.UpdateLimitsAsync(new HistoryLimits(3, 100, 7, 20000)), "too-small pinned budget rejected");
            Check(await pointers.GetAsync(largeClip.Id) is not null, "rejected settings preserve history");
            await pointers.ClearAsync();
            var budget = new HistoryStore(root, protector, new HistoryLimits(50, 3000, 30, 20000));
            await budget.InitializeAsync();
            for (var i = 0; i < 10; i++) await budget.AddAsync(new Clip { Kind = ClipKind.Text, Text = i + new string('x', 500) });
            Check((await budget.ListAsync()).Sum(x => x.StoredBytes) <= 3000, "disk budget cap");
            await budget.ClearAsync();
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => budget.AddAsync(new Clip { Kind = ClipKind.Text, Text = "concurrent-" + i })));
            Check((await budget.ListAsync()).Sum(x => x.StoredBytes) <= 3000, "concurrent writes remain bounded");
            try { await budget.GetAsync("../bad"); Check(true, "unknown IDs never used as paths"); } catch (InvalidDataException) { Check(true, "reject unsafe ID"); }
            Console.WriteLine($"PASS: {_assertions} storage, format, cache and WPF assertions (including Windows DPAPI)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL: " + message); _assertions++; }

    private static void CheckFormats()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var text = new DataObject(); text.SetText("sample Unicode 🧵", TextDataFormat.UnicodeText);
                Check(ClipboardService.ReadData(text, () => null)?.Text == "sample Unicode 🧵", "text format conversion");
                var excluded = new DataObject(); excluded.SetText("private"); excluded.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([1]));
                Check(ClipboardService.ReadData(excluded, () => null) is null, "exclusion marker");
                var noHistory = new DataObject(); noHistory.SetText("private"); noHistory.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
                Check(ClipboardService.ReadData(noHistory, () => null) is null, "zero history marker");
                var blank = new DataObject(); blank.SetText(" \n\t");
                Check(ClipboardService.ReadData(blank, () => null) is null, "blank ignored");
                var large = new DataObject(); large.SetText(new string('x', 524289));
                Check(ClipboardService.ReadData(large, () => null) is null, "text capture limit");
                var files = new DataObject(); files.SetData(DataFormats.FileDrop, new[] { @"C:\sample.txt" }); files.SetText("secondary format");
                Check(ClipboardService.ReadData(files, () => null)?.Kind == ClipKind.Files, "files before text");
                var many = new DataObject(); many.SetData(DataFormats.FileDrop, Enumerable.Range(0, 101).Select(i => @"C:\sample-" + i).ToArray());
                Check(ClipboardService.ReadData(many, () => null) is null, "file count limit");
                var invalidFiles = new DataObject(); invalidFiles.SetData(DataFormats.FileDrop, new string[] { null! });
                Check(ClipboardService.ReadData(invalidFiles, () => null) is null, "malformed null file location cannot crash capture");
                var blankFiles = new DataObject(); blankFiles.SetData(DataFormats.FileDrop, new[] { " " });
                Check(ClipboardService.ReadData(blankFiles, () => null) is null, "blank file location rejected");
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
                var image = new DataObject(); image.SetImage(bitmap);
                var converted = ClipboardService.ReadData(image, () => bitmap);
                Check(converted?.Kind == ClipKind.Image && converted.ImagePng![0] == 137 && converted.ImagePng[1] == 80, "bitmap to PNG conversion");
                Check(ClipboardService.ReadData(image, () => throw new InvalidOperationException("Disabled image must not be read"), new AppSettings { CaptureImages = false }) is null, "disabled images never decode");
                Check(ClipboardService.ReadData(new DataObject(), () => null) is null, "unsupported format ignored");
                var tall = BitmapSource.Create(10, 2000, 96, 96, PixelFormats.Bgra32, null, new byte[80000], 40);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(tall));
                using var stream = new MemoryStream(); encoder.Save(stream);
                var thumbnail = PreviewCache.Decode(stream.ToArray(), 360, 230);
                Check(thumbnail.PixelHeight <= 230 && thumbnail.PixelWidth <= 360 && thumbnail.IsFrozen, "tall image thumbnail stays bounded");
                var detached = DecodeForLifetimeCheck();
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Check(!detached.Encoded.TryGetTarget(out _), "decoded image does not retain its encoded source buffer");
                GC.KeepAlive(detached.Image);
                var rgba = new byte[] { 10, 30, 90, 255, 200, 130, 40, 255 };
                var colorSource = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, rgba, 8);
                var colorEncoder = new PngBitmapEncoder(); colorEncoder.Frames.Add(BitmapFrame.Create(colorSource));
                using var colors = new MemoryStream(); colorEncoder.Save(colors);
                var colorImage = PreviewCache.Decode(colors.ToArray(), int.MaxValue, int.MaxValue);
                var restoredPixels = new byte[8]; colorImage.CopyPixels(restoredPixels, 8, 0);
                Check(colorImage.PixelWidth == 2 && colorImage.PixelHeight == 1 && restoredPixels.SequenceEqual(rgba), "detached decode preserves original dimensions and opaque pixel colors");
                Check(Marshal.SizeOf<NativeMethods.Input>() == (Environment.Is64BitProcess ? 40 : 28), "native input ABI size");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Format checks failed", failure);
    }
    private static async Task CheckFileProbeAsync(string root)
    {
        var existing = Path.Combine(root, "probe-fixture.txt"); await File.WriteAllTextAsync(existing, "synthetic");
        var local = new FileAvailabilityProbe();
        Check(await local.CheckAsync([existing]), "file probe finds existing original");
        Check(!await local.CheckAsync([Path.Combine(root, "missing.txt")]), "file probe rejects missing original");
        Check(!await local.CheckAsync([]), "empty file reference rejected");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = 0;
        var slow = new FileAvailabilityProbe(_ => { Interlocked.Increment(ref workers); entered.TrySetResult(); release.Wait(); finished.TrySetResult(); return true; }, TimeSpan.FromMilliseconds(100));
        try
        {
            var first = slow.CheckAsync([existing]); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var timedOut = false; try { await first; } catch (FileAvailabilityException) { timedOut = true; }
            Check(timedOut, "offline file wait returns a recoverable timeout");
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var busy = false; try { await slow.CheckAsync([existing]); } catch (FileAvailabilityException) { busy = true; }
                Check(busy, "retries cannot spawn more blocked workers " + attempt);
            }
            Check(workers == 1, "outstanding file workers bounded to one");
        }
        finally { release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
    private static void CheckVisuals(string root)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            RopeWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Clippboard;component/Design/Controls.xaml", UriKind.Relative) });
                window = new RopeWindow(new HistoryStore(Path.Combine(root, "visuals"), new TestWindowsProtector()), preview: true, settings: new AppSettings { Animate = false })
                { Width = 1600, Height = 800, Left = -20000, Top = -20000, ShowActivated = false, Topmost = false };
                window.Show(); window.UpdateLayout(); window.RenderDemo(1000); window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.RenderedNoteCount is > 0 and < 10, "1000 clips render only viewport notes");
                var initialCards = window.CreatedNoteCount;
                ((System.Windows.Controls.Button)window.FindName("AllButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Check(window.CreatedNoteCount == initialCards, "unchanged summaries retain note visuals on list refresh");
                Check(window.PreviewBudgetBytes < window.SavedSettings.PreviewCacheMiB * 1024L * 1024, "preview retention follows the viewport within the user's ceiling");
                Check(MemoryAvailability.PreviewBudget(16000000, 6, 300000, 1000000000) == 3600000
                    && MemoryAvailability.PreviewBudget(16000000, 6, 300000, 10000000) == 100000
                    && MemoryAvailability.PreviewBudget(50000, 6, 300000, 10000000) == 50000,
                    "adaptive cache respects demand, live physical memory pressure and user limit");
                Check(MemoryAvailability.DecodeConcurrency(10000000, 16, 24000000) == 1
                    && MemoryAvailability.DecodeConcurrency(16000000000, 16, 24000000) == 2
                    && MemoryAvailability.DecodeConcurrency(16000000000, 1, 24000000) == 1,
                    "image worker concurrency respects memory headroom and processor availability");
                var messageCount = 0;
                using (var messages = new MessageWindow((message, _, _) => { if (message != 0x8005) return false; ++messageCount; return true; }))
                { SendMessage(messages.Handle, 0x8005, 0, 0); Check(messageCount == 1, "non-rendering message window dispatches owned native messages"); }
                var navigation = (System.Windows.Controls.Primitives.ScrollBar)window.FindName("HistoryScroll");
                navigation.ApplyTemplate();
                var navigationTrack = (System.Windows.Controls.Primitives.Track)navigation.Template.FindName("PART_Track", navigation);
                Check(navigationTrack.Thumb.ActualWidth >= 32, "1000-note navigation retains a usable thumb width");
                var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("NoteScroll");
                scroll.ScrollToRightEnd(); window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.RenderedNoteCount is > 0 and < 10 && scroll.HorizontalOffset > 300000, $"all history reachable with bounded visuals (offset {scroll.HorizontalOffset}, width {scroll.ScrollableWidth}, cards {window.RenderedNoteCount})");
                Check(window.CreatedNoteCount <= initialCards + 2, "jumping through 1000 notes reuses viewport controls");
                ClipSummary? restored = null; window.Selected += entry => { restored = entry; return Task.CompletedTask; };
                var notes = (System.Windows.Controls.Canvas)window.FindName("Notes");
                var recycledSelect = (System.Windows.Controls.Button)((System.Windows.Controls.Grid)notes.Children[0]).Tag;
                recycledSelect.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Check(restored is not null && recycledSelect.Content is System.Windows.Controls.TextBlock recycledText && restored.Preview == recycledText.Text,
                    "recycled note restores its current content rather than its previous binding");
                Check(Math.Abs(navigation.Value - navigation.Maximum) < 1 && navigationTrack.Thumb.ActualWidth >= 32, "minimum thumb preserves navigation to the final note");
                scroll.ScrollToLeftEnd(); window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var board = (System.Windows.Controls.Grid)window.FindName("Board");
                var point = board.PointToScreen(new Point(200, 100));
                var position = (nint)(((int)point.X & 0xffff) | (((int)point.Y & 0xffff) << 16));
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var characters = HorizontalWheel.ScrollCharacters();
                if (characters != 0)
                {
                    SendMessage(handle, HorizontalWheel.Message, (nint)(5 << 16), position);
                    Check(Math.Abs(window.ScrollOffset - HorizontalWheel.Distance(5, characters, scroll.ViewportWidth)) < .001, "native WM_MOUSEHWHEEL reaches offscreen owned window without losing fractional tick");
                    SendMessage(handle, HorizontalWheel.Message, (nint)(-5 << 16), position);
                    Check(window.ScrollOffset == 0, "native horizontal direction reverses to exact start");
                    SendMessage(handle, HorizontalWheel.Message, (nint)((120 << 16) | 8), position);
                    Check(window.ScrollOffset == 0, "Ctrl gesture does not become history scrolling");
                }
                window.ApplyAppearance(window.SavedSettings with { Animate = true });
                window.DiagnosticScroll(600); Pump(Task.Delay(90));
                Check(SystemParameters.ClientAreaAnimation ? window.ScrollOffset > 0 && scroll.HorizontalOffset == 0 : window.ScrollOffset == 600 && scroll.HorizontalOffset == 600, "scrolling uses render motion or immediate reduced-motion commit");
                var midScroll = window.ScrollOffset;
                window.DiagnosticScroll(200);
                Check(SystemParameters.ClientAreaAnimation ? Math.Abs(window.ScrollOffset - midScroll) < .001 : window.ScrollOffset == 200, "rapid scroll reversal preserves animated position or applies reduced motion immediately");
                Pump(Task.Delay(650)); window.UpdateLayout();
                Check(Math.Abs(window.ScrollOffset - 200) < .001 && Math.Abs(scroll.HorizontalOffset - 200) < .001, "rendered and accessible scroll positions commit together at rest");
                Check(!window.ScrollRunning, "scroll frame subscription stops at rest");
                for (var tick = 0; tick < 60; tick++) { window.DiagnosticGesture(tick < 30 ? .75 : -.75); Pump(Task.Delay(4)); }
                Pump(Task.Delay(650)); window.UpdateLayout();
                Check(Math.Abs(window.ScrollOffset - 200) < .001 && !window.ScrollRunning, "fractional sustained gestures and reversal settle without cumulative drift");
                var beforePan = window.CreatedNoteCount;
                for (var tick = 0; tick < 40; tick++) { window.DiagnosticGesture(tick < 20 ? 100 : -100); Pump(Task.Delay(8)); }
                Pump(Task.Delay(650)); window.UpdateLayout();
                Check(Math.Abs(window.ScrollOffset - 200) < .001 && window.CreatedNoteCount - beforePan <= 2,
                    "sustained panning crosses viewport boundaries with bounded control allocations");
                window.ApplyAppearance(window.SavedSettings with { Animate = false });
                window.DiagnosticScroll(0); Pump(Task.Delay(30)); window.UpdateLayout();
                foreach (var theme in Enum.GetValues<AppTheme>())
                {
                    window.ApplyAppearance(new AppSettings { Theme = theme, Animate = false });
                    var owner = Descendants<System.Windows.Controls.Button>(window).First(x => x.ToolTip is not null);
                    Interaction.PrepareToolTip(owner);
                    var tip = (System.Windows.Controls.ToolTip)owner.ToolTip;
                    tip.PlacementTarget = owner; tip.IsOpen = true; tip.ApplyTemplate();
                    Check(Contrast(((SolidColorBrush)tip.Foreground).Color, ((SolidColorBrush)tip.Background).Color) >= 4.5, "tooltip readable in " + theme);
                    Check(Descendants<System.Windows.Controls.TextBlock>(tip).Any(x => !string.IsNullOrWhiteSpace(x.Text) && Equals(x.Foreground, tip.Foreground)), "tooltip template uses its explicit ink in " + theme);
                    tip.IsOpen = false;
                    Check(Contrast(((SolidColorBrush)window.Resources["Muted"]).Color, ((SolidColorBrush)window.Resources["NoteSurface"]).Color) >= 4.5, "date and metadata contrast in " + theme);
                }
                window.ApplyAppearance(new AppSettings { Animate = false });
                var contrastResources = new ResourceDictionary();
                ThemeManager.Apply(AppTheme.Rose, contrastResources, highContrast: true);
                Check(Equals(contrastResources["Foreground"], SystemColors.WindowTextBrush) && Equals(contrastResources["Chrome"], SystemColors.WindowBrush), "high contrast uses Windows ink and surfaces");
                window.Width = 640; window.UpdateLayout();
                var toolbar = (System.Windows.Controls.Border)window.FindName("Toolbar");
                Check(toolbar.Child is System.Windows.Controls.WrapPanel && toolbar.ActualWidth <= window.ActualWidth, "narrow viewport wraps toolbar without clipping controls");
                window.OpenSettings(); window.UpdateLayout();
                var compactSidebar = (System.Windows.Controls.Border)window.FindName("SidebarHost");
                Check(compactSidebar.ActualWidth < window.ActualWidth, "settings stays within narrow screen");
                window.Width = 1600; window.UpdateLayout();
                var search = (System.Windows.Controls.TextBox)window.FindName("SearchBox");
                search.Text = "no-such-synthetic-preview"; Pump(Task.Delay(200)); window.UpdateLayout();
                Check(window.VisibleNoteCount == 0 && window.RenderedNoteCount == 0, "search has a safe empty state");
                search.Text = "a LITTLE space"; Pump(Task.Delay(200)); window.UpdateLayout();
                Check(window.VisibleNoteCount == 1, "search matches previews without case sensitivity");
                search.Text = ""; Pump(Task.Delay(200)); window.UpdateLayout();
                var typeFilter = (System.Windows.Controls.Button)window.FindName("TypeFilterButton");
                typeFilter.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Check(window.VisibleNoteCount == 998, "text filter applies to metadata");
                typeFilter.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Check(window.VisibleNoteCount == 1, "image filter");
                typeFilter.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Check(window.VisibleNoteCount == 1, "file filter");
                typeFilter.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Check(window.VisibleNoteCount == 1000 && window.RenderedNoteCount < 10, "clear filters returns bounded full history");
                Check(Feedback.Link("bug").Host == "github.com" && Feedback.Link("bug").Query.Contains("template=bug_report.yml"), "feedback opens correct public form");
                var support = Feedback.SupportInfo();
                Check(!support.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase) && !support.Contains(root, StringComparison.OrdinalIgnoreCase), "support information excludes account and paths");
                window.OpenSettings(); window.UpdateLayout();
                var host = (System.Windows.Controls.Border)window.FindName("SidebarHost");
                Check(host.Child is SettingsView && host.Visibility == Visibility.Visible, "custom settings sidebar opens");
                var settings = (SettingsView)host.Child;
                var widthSlider = Descendants<System.Windows.Controls.Slider>(settings).First(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Note width");
                widthSlider.Value = 380; window.UpdateLayout();
                Check(window.Appearance.NoteWidth == 380 && window.SavedSettings.NoteWidth == 300 && window.RenderedNoteCount < 10, "size preview is live, bounded and uncommitted");
                Descendants<System.Windows.Controls.Button>(settings).First(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Close settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Check(window.Appearance.NoteWidth == 300 && window.SavedSettings.NoteWidth == 300, "cancel restores saved size");
                window.OpenSettings(); window.UpdateLayout(); settings = (SettingsView)host.Child;
                widthSlider = Descendants<System.Windows.Controls.Slider>(settings).First(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Note width");
                widthSlider.Value = 340;
                Descendants<System.Windows.Controls.Button>(settings).First(x => Equals(x.Content, "Save changes")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Check(window.SavedSettings.NoteWidth == 340, "save commits preview size");
                window.ApplyAppearance(window.SavedSettings with { Animate = true });
                window.OpenSettings(); window.UpdateLayout();
                var priorView = (SettingsView)host.Child;
                Descendants<System.Windows.Controls.Button>(priorView).First(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Close settings").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                window.OpenSettings(); window.UpdateLayout(); var latestView = host.Child;
                Pump(Task.Delay(600));
                Check(ReferenceEquals(host.Child, latestView) && host.Visibility == Visibility.Visible, "old dismiss animation cannot remove reopened settings");
                window.ApplyAppearance(window.SavedSettings with { Animate = false });
                settings = (SettingsView)host.Child;
                var transform = new TranslateTransform(); Interaction.Move(transform, TranslateTransform.YProperty, -5, false);
                Check(transform.Y == -5 && !transform.HasAnimatedProperties, "reduced motion applies exact state without clocks");
                Interaction.Move(transform, TranslateTransform.YProperty, 0, true, 80);
                Pump(WaitForRest(transform));
                Check(Math.Abs(transform.Y) < .001, "hover settles at exact rest without drift");
                Interaction.Move(transform, TranslateTransform.YProperty, 100, true, 250); Pump(Task.Delay(90));
                var priorPosition = transform.Y; var priorVelocity = SmoothMotion.Velocity(transform, TranslateTransform.YProperty);
                var transientDestination = false;
                var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TranslateTransform.YProperty, typeof(TranslateTransform));
                EventHandler observe = (_, _) => { if (transform.Y == -20) transientDestination = true; };
                descriptor.AddValueChanged(transform, observe);
                Interaction.Move(transform, TranslateTransform.YProperty, -20, true, 250);
                descriptor.RemoveValueChanged(transform, observe);
                Check(!transientDestination, "retargeting does not publish a temporary destination to dependency-property callbacks");
                Check(Math.Abs(transform.Y - priorPosition) < .001 && Math.Abs(SmoothMotion.Velocity(transform, TranslateTransform.YProperty) - priorVelocity) < .001, "actual WPF motion preserves position and velocity when interrupted");
                Interaction.Move(transform, TranslateTransform.YProperty, 0, false);
                Check(transform.Y == 0 && !transform.HasAnimatedProperties && SmoothMotion.Velocity(transform, TranslateTransform.YProperty) == 0, "reduced motion cancels an active spring immediately");
                for (var page = 0; page < 5; page++) { settings.ShowPage(page); window.UpdateLayout(); Check(settings.ActualHeight > 0, "settings page lays out " + page); }
                settings.ShowPage(2);
                var retention = Descendants<System.Windows.Controls.Slider>(settings).First(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Keep history");
                retention.Value = 7;
                Check(settings.Draft.RetentionDays == 7 && retention.Maximum == 30, "retention control edits bounded draft");
                settings.ShowPage(1);
                window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var autoPaste = Descendants<System.Windows.Controls.CheckBox>(settings).First(x => Equals(x.Content, "Paste into the previous app"));
                autoPaste.ApplyTemplate();
                var knob = (TranslateTransform)autoPaste.Template.FindName("KnobMotion", autoPaste);
                Check(knob.X == 14, "checked switch starts at correct position without entrance drift");
                autoPaste.IsChecked = false;
                Check(!settings.Draft.AutoPaste, "behaviour toggle edits draft");
                Check(knob.X == 0 && !knob.HasAnimatedProperties, "reduced-motion switch updates without animation");
                var actionButton = Descendants<System.Windows.Controls.Button>(settings).First(x => Equals(x.Content, "Save changes"));
                actionButton.ApplyTemplate(); var hoverSurface = (System.Windows.Controls.Border)actionButton.Template.FindName("HoverSurface", actionButton);
                var mouse = System.Windows.Input.Mouse.PrimaryDevice;
                actionButton.RaiseEvent(new System.Windows.Input.MouseEventArgs(mouse, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
                Check(hoverSurface.Opacity == 1, "button hover surface receives themed feedback");
                actionButton.RaiseEvent(new System.Windows.Input.MouseEventArgs(mouse, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
                Check(hoverSurface.Opacity == 0 && !hoverSurface.HasAnimatedProperties, "button hover clears without sticky surface in reduced motion");
                var longText = new string('x', 4000) + "END-OF-NOTE";
                Pump(window.PreviewDetailAsync(new Clip { Kind = ClipKind.Text, Text = longText }));
                Check(window.DetailText?.Text == longText, "expanded note reads full text beyond preview");
                var detail = (System.Windows.Controls.Border)window.FindName("DetailHost");
                var oldWidth = detail.Width;
                Descendants<System.Windows.Controls.Button>(detail).First(x => Equals(x.Content, "Larger")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Check(detail.Width > oldWidth && detail.Width < window.ActualWidth, "expanded note enlarges within screen");
                Pump(window.OpenDetailAsync(new Clip { Kind = ClipKind.Text }.Summary(0)));
                // Preview mode uses synthetic fallback for a missing payload; normal mode reports expiry.
                Check(window.DetailText is not null, "synthetic detail fallback stays isolated");
                var cacheStore = new HistoryStore(Path.Combine(root, "cache-images"), new TestWindowsProtector());
                Pump(Task.Run(() => cacheStore.InitializeAsync()));
                var cache = new PreviewCache(cacheStore) { BudgetBytes = 200000 };
                for (var i = 0; i < 8; i++)
                {
                    var pixels = new byte[40000]; for (var p = 0; p < pixels.Length; p += 4) { pixels[p] = (byte)(i * 20); pixels[p + 3] = 255; }
                    var bitmap = BitmapSource.Create(100, 100, 96, 96, PixelFormats.Bgra32, null, pixels, 400);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var buffer = new MemoryStream(); encoder.Save(buffer);
                    var clip = new Clip { Kind = ClipKind.Image, ImagePng = buffer.ToArray() };
                    Pump(Task.Run(() => cacheStore.AddAsync(clip))); Pump(cache.GetAsync(clip.Id, CancellationToken.None));
                }
                Check(cache.Count == 5 && cache.RetainedBytes == 200000, "LRU evicts images at exact byte budget");
                cache.BudgetBytes = 80000; Check(cache.Count == 2 && cache.RetainedBytes == 80000, "memory pressure immediately releases excess decoded previews");
                cache.Clear(); Check(cache.Count == 0 && cache.RetainedBytes == 0, "preview cache clears all references");
                var deferred = new DeferredHistory();
                var deferredCache = new PreviewCache(deferred);
                var pending = deferredCache.GetAsync("fixture", CancellationToken.None);
                Pump(deferred.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)));
                deferredCache.Clear();
                var tiny = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
                var tinyEncoder = new PngBitmapEncoder(); tinyEncoder.Frames.Add(BitmapFrame.Create(tiny));
                using var tinyBuffer = new MemoryStream(); tinyEncoder.Save(tinyBuffer);
                var encoded = tinyBuffer.ToArray();
                deferred.Result.SetResult(new Clip { Kind = ClipKind.Image, ImagePng = encoded }); Pump(pending);
                Check(pending.Result is not null && deferredCache.Count == 0 && encoded.All(x => x == 0), "late preview after clear cannot repopulate cache and releases encoded payload");
                var cancelledHistory = new DeferredHistory(); var cancelledCache = new PreviewCache(cancelledHistory);
                using var cancelledLoads = new CancellationTokenSource();
                var queued = Enumerable.Range(0, 20).Select(i => cancelledCache.GetAsync(i.ToString(), cancelledLoads.Token)).ToArray();
                Pump(cancelledHistory.Started.Task.WaitAsync(TimeSpan.FromSeconds(3))); cancelledLoads.Cancel();
                var abandoned = new byte[] { 1, 2, 3 };
                cancelledHistory.Result.SetResult(new Clip { Kind = ClipKind.Image, ImagePng = abandoned });
                try { Pump(Task.WhenAll(queued)); } catch (OperationCanceledException) { }
                Check(queued.All(x => x.IsCanceled) && cancelledHistory.Calls <= 2 && abandoned.All(x => x == 0), "cancelled thumbnail queue bounds workers and wipes late payloads before decode");
                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") CheckWindowsClipboard(root);
                var close = window.CloseRopeAsync();
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(async () => { try { await close; } catch (Exception ex) { failure = ex; } finally { frame.Continue = false; } });
                Dispatcher.PushFrame(frame);
                Check(window.Retired && !window.IsVisible && window.RenderedNoteCount == 0 && window.CachedImageBytes == 0 && window.DetailText is null, "closing releases native surface, cards, detail and thumbnail cache");
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    window = new RopeWindow(cacheStore, preview: true, settings: new AppSettings { Animate = false }) { Width = 1200, Height = 1000, Left = -20000, Top = -20000, ShowActivated = false, Topmost = false };
                    window.DiagnosticCompactSurface();
                    Check(window.Height < 1000, "surface can be measured before a native window exists");
                    window.Show(); window.UpdateLayout(); window.RenderDemo(); window.UpdateLayout();
                    window.DiagnosticCompactSurface(); window.UpdateLayout();
                    var compactHeight = window.ActualHeight;
                    Check(compactHeight < 1000 && compactHeight >= ((System.Windows.Controls.Grid)window.FindName("Panel")).ActualHeight, "transparent surface follows measured content rather than a hardcoded desktop size");
                    window.OpenSettings(); window.UpdateLayout();
                    Check(window.ActualHeight == compactHeight, "settings reuses surface without retaining resize buffers");
                    Check(window.RenderedNoteCount > 0 && !window.Retired, "fresh overlay opens after retirement " + cycle);
                    Pump(window.CloseRopeAsync()); Check(window.Retired && window.RenderedNoteCount == 0, "repeat close releases overlay " + cycle);
                    Pump(window.RefreshAsync()); Check(window.RenderedNoteCount == 0, "late refresh cannot revive retired overlay " + cycle);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Visual checks failed", failure);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (BitmapSource Image, WeakReference<byte[]> Encoded) DecodeForLifetimeCheck()
    {
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream(); encoder.Save(stream);
        var bytes = stream.ToArray();
        return (PreviewCache.Decode(bytes, 360, 230), new WeakReference<byte[]>(bytes));
    }
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await task; } catch { /* Rethrown below. */ } finally { frame.Continue = false; } });
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    private static void CheckWindowsClipboard(string root)
    {
        // An isolated hosted runner only: never replace a developer's personal clipboard.
        Console.WriteLine("Checking real Windows clipboard notifications on isolated CI...");
        var captured = new System.Collections.Generic.List<Clip>();
        using (var service = new ClipboardService(clip => { captured.Add(clip); return Task.CompletedTask; }))
        {
            try
            {
                Clipboard.SetText("Clippboard CI Unicode 🧵"); Pump(WaitForCapture(() => captured.Count >= 1));
                Check(captured.LastOrDefault()?.Text == "Clippboard CI Unicode 🧵", "Windows text notification and capture");
                service.Paused = true; Clipboard.SetText("paused fixture"); Pump(Task.Delay(350));
                Check(captured.Count == 1, "Windows capture pause"); service.Paused = false;
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8); bitmap.Freeze();
                Clipboard.SetImage(bitmap); Pump(WaitForCapture(() => captured.Count >= 2));
                Check(captured.Last().Kind == ClipKind.Image && captured.Last().ImagePng is not null, "Windows image notification and PNG capture");
                var file = Path.Combine(root, "clipboard-fixture.txt"); File.WriteAllText(file, "synthetic file");
                var files = new System.Collections.Specialized.StringCollection { file };
                Clipboard.SetFileDropList(files); Pump(WaitForCapture(() => captured.Count >= 3));
                Check(captured.Last().Kind == ClipKind.Files && captured.Last().FilePaths!.SequenceEqual([file]), "Windows file-drop capture");
                Pump(service.RestoreAsync(captured.Last()));
                Check(Clipboard.GetFileDropList().Contains(file), "Windows file reference restore");
                Pump(service.RestoreAsync(new Clip { Kind = ClipKind.Text, Text = "restored fixture" })); Pump(Task.Delay(350));
                Check(Clipboard.GetText() == "restored fixture" && captured.Count == 3, "own restore does not duplicate history");
                Pump(service.RestoreAsync(captured[1]));
                Check(Clipboard.GetImage()?.PixelWidth == 2, "Windows image restore");
                var privateData = new DataObject(); privateData.SetText("excluded fixture"); privateData.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([1]));
                Clipboard.SetDataObject(privateData, true); Pump(Task.Delay(350));
                Check(captured.Count == 3, "Windows exclusion marker suppresses capture");
                var missingRejected = false;
                try { Pump(service.RestoreAsync(new Clip { Kind = ClipKind.Files, FilePaths = [Path.Combine(root, "does-not-exist.txt")] })); }
                catch (FileNotFoundException) { missingRejected = true; }
                Check(missingRejected, "missing original does not replace clipboard");
            }
            finally { service.Paused = true; Clipboard.Clear(); }
            service.Dispose(); service.Dispose(); Check(true, "clipboard listener disposal is idempotent");
            var disposedRejected = false;
            try { Pump(service.RestoreAsync(new Clip { Kind = ClipKind.Text, Text = "must not write" })); }
            catch (ObjectDisposedException) { disposedRejected = true; }
            Check(disposedRejected, "disposed listener cannot change clipboard");
        }
    }
    private static async Task WaitForCapture(Func<bool> ready)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && deadline.ElapsedMilliseconds < 3000) await Task.Delay(20);
    }
    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Linear(byte value) { var x = value / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        return (Math.Max(L(a), L(b)) + .05) / (Math.Min(L(a), L(b)) + .05);
    }
    private static async Task WaitForRest(TranslateTransform transform)
    {
        // Animation clocks start on a rendering tick, not at the call's wall time.
        // Hosted runners can delay that first tick; observe state with a bounded watchdog.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (Math.Abs(transform.Y) > .001 && deadline.ElapsedMilliseconds < 3000) await Task.Delay(20);
    }
    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var item2 in Descendants<T>(child)) yield return item2;
        }
    }
    private sealed class DeferredHistory : IHistoryAccess
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<Clip?> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public Task<System.Collections.Generic.IReadOnlyList<ClipSummary>> ListAsync() => Task.FromResult<System.Collections.Generic.IReadOnlyList<ClipSummary>>([]);
        public Task<Clip?> GetAsync(string id) { Interlocked.Increment(ref Calls); Started.TrySetResult(); return Result.Task; }
        public Task SetPinnedAsync(string id, bool pinned) => Task.CompletedTask;
        public Task DeleteAsync(string id) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
    private sealed class TestWindowsProtector : IContentProtector
    {
        public int LargestUnprotected { get; set; }
        public byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] data) { LargestUnprotected = Math.Max(LargestUnprotected, data.Length); return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser); }
    }
}
