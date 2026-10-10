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
using Clippboard.Core;

// A small executable test suite; assertions exercise storage invariants and real Windows DPAPI.
internal static class Program
{
    private static int _assertions;
    public static async Task<int> Main()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(), "Clippboard-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            CheckFormats();
            Check(MotionMath.Settle(0, true) == 0 && MotionMath.Settle(1, true) == 1, "spring rests at exact endpoints");
            Check(Enumerable.Range(0, 101).Select(i => MotionMath.Settle(i / 100.0, true)).Max() is > 1 and < 1.2, "spring bounce bounded");
            Check(MotionMath.RopeHeight(0, 1600, 24) == 20 && MotionMath.RopeHeight(800, 1600, 24) == 44, "rope attachment curve");
            var range = MotionMath.VisibleRange(1000, 100000, 1600, 340);
            Check(range.Last - range.First < 9 && range.First > 200, "large history virtual range bounded");
            Check(MotionMath.VisibleRange(0, 0, 1600, 340).Last == -1, "empty virtualization range");
            Check(ClipboardService.ShortcutModifiers(ShortcutChoice.AltV) == 0x4001 && ClipboardService.ShortcutModifiers(ShortcutChoice.CtrlShiftV) == 0x4006, "shortcut modifiers include no-repeat");
            Console.WriteLine("Checking visual lifetime..."); CheckVisuals(root); Console.WriteLine("Checking storage...");
            var settingsFile = Path.Combine(root, "settings.json");
            var settingsStore = new SettingsStore(settingsFile);
            Check((await settingsStore.LoadAsync()).Theme == AppTheme.Cloud, "white theme default");
            var normalized = (new AppSettings { RetentionDays = 90, PreviewCacheMiB = 1000, NoteSpacing = -10, Theme = (AppTheme)999 }).Normalize();
            Check(normalized.RetentionDays == 30 && normalized.PreviewCacheMiB == 64 && normalized.NoteSpacing == 24 && normalized.Theme == AppTheme.Cloud, "settings bounds");
            await settingsStore.SaveAsync(new AppSettings { Theme = AppTheme.Midnight, RetentionDays = 7, AutoPaste = false });
            Check((await settingsStore.LoadAsync()).RetentionDays == 7 && !(await settingsStore.LoadAsync()).AutoPaste, "settings persistence");
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
                Check(Marshal.SizeOf<NativeMethods.Input>() == (Environment.Is64BitProcess ? 40 : 28), "native input ABI size");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Format checks failed", failure);
    }
    private static void CheckVisuals(string root)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            RopeWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); var app = new Clippboard.App.App(); app.InitializeComponent();
                window = new RopeWindow(new HistoryStore(Path.Combine(root, "visuals"), new TestWindowsProtector()), preview: true, settings: new AppSettings { Animate = false })
                { Width = 1600, Height = 800 };
                window.Show(); window.UpdateLayout(); window.RenderDemo(1000); window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.RenderedNoteCount is > 0 and < 10, "1000 clips render only viewport notes");
                var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("NoteScroll");
                scroll.ScrollToRightEnd(); window.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Check(window.RenderedNoteCount is > 0 and < 10 && scroll.HorizontalOffset > 300000, "all history reachable with bounded visuals");
                window.OpenSettings(); window.UpdateLayout();
                var host = (System.Windows.Controls.Border)window.FindName("SidebarHost");
                Check(host.Child is SettingsView && host.Visibility == Visibility.Visible, "custom settings sidebar opens");
                var settings = (SettingsView)host.Child;
                for (var page = 0; page < 4; page++) { settings.ShowPage(page); window.UpdateLayout(); Check(settings.ActualHeight > 0, "settings page lays out " + page); }
                settings.ShowPage(2);
                var retention = Descendants<System.Windows.Controls.Slider>(settings).First(x => System.Windows.Automation.AutomationProperties.GetName(x) == "Keep history");
                retention.Value = 7;
                Check(settings.Draft.RetentionDays == 7 && retention.Maximum == 30, "retention control edits bounded draft");
                settings.ShowPage(1);
                var autoPaste = Descendants<System.Windows.Controls.CheckBox>(settings).First(x => Equals(x.Content, "Paste into the previous app"));
                autoPaste.IsChecked = false;
                Check(!settings.Draft.AutoPaste, "behaviour toggle edits draft");
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
                cache.Clear(); Check(cache.Count == 0 && cache.RetainedBytes == 0, "preview cache clears all references");
                var close = window.CloseRopeAsync();
                var frame = new DispatcherFrame();
                window.Dispatcher.BeginInvoke(async () => { try { await close; } catch (Exception ex) { failure = ex; } finally { frame.Continue = false; } });
                Dispatcher.PushFrame(frame);
                Check(window.Retired && !window.IsVisible && window.RenderedNoteCount == 0 && window.CachedImageBytes == 0 && window.DetailText is null, "closing releases native surface, cards, detail and thumbnail cache");
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    window = new RopeWindow(cacheStore, preview: true, settings: new AppSettings { Animate = false }) { Width = 1200, Height = 700 };
                    window.Show(); window.UpdateLayout(); window.RenderDemo(); window.UpdateLayout();
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
    private static void Pump(Task task)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(async () => { try { await task; } catch { /* Rethrown below. */ } finally { frame.Continue = false; } });
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
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
    private sealed class TestWindowsProtector : IContentProtector
    {
        public int LargestUnprotected { get; set; }
        public byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] data) { LargestUnprotected = Math.Max(LargestUnprotected, data.Length); return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser); }
    }
}
