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
            var budget = new HistoryStore(root, protector, new HistoryLimits(50, 3000, 30, 20000));
            await budget.InitializeAsync();
            for (var i = 0; i < 10; i++) await budget.AddAsync(new Clip { Kind = ClipKind.Text, Text = i + new string('x', 500) });
            Check((await budget.ListAsync()).Sum(x => x.StoredBytes) <= 3000, "disk budget cap");
            await budget.ClearAsync();
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => budget.AddAsync(new Clip { Kind = ClipKind.Text, Text = "concurrent-" + i })));
            Check((await budget.ListAsync()).Sum(x => x.StoredBytes) <= 3000, "concurrent writes remain bounded");
            try { await budget.GetAsync("../bad"); Check(true, "unknown IDs never used as paths"); } catch (InvalidDataException) { Check(true, "reject unsafe ID"); }
            Console.WriteLine($"PASS: {_assertions} storage and format assertions (including Windows DPAPI)");
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
                Check(ClipboardService.ReadData(new DataObject(), () => null) is null, "unsupported format ignored");
                Check(Marshal.SizeOf<NativeMethods.Input>() == (Environment.Is64BitProcess ? 40 : 28), "native input ABI size");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw new InvalidOperationException("Format checks failed", failure);
    }
    private sealed class TestWindowsProtector : IContentProtector
    {
        public byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
    }
}
