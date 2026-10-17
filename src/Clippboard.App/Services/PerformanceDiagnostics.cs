using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clippboard.App.Views;
using Clippboard.Core;

namespace Clippboard.App.Services;

// Explicit synthetic diagnostic. No screenshot buffers, clipboard listener or forced collections.
internal static class PerformanceDiagnostics
{
    internal static async Task RunMetadataAsync(string output)
    {
        var started = GC.GetTotalAllocatedBytes(true);
        var rows = Enumerable.Range(0, 1000).Select(index => new ClipSummary(index.ToString("x32"), ClipKind.Text,
            DateTimeOffset.UnixEpoch.AddMinutes(index), index % 10 == 0, index.ToString("D4") + new string('p', 316),
            "Text:" + index.ToString("x64"), 4096, false, new string((char)('a' + index % 26), 64),
            Enumerable.Range(0, 8).Select(label => new string((char)('a' + (index + label) % 26), 24)).ToArray())).ToArray();
        var construction = GC.GetTotalAllocatedBytes(true) - started;
        var cached = Array.AsReadOnly(rows.OrderByDescending(item => item.Pinned).ThenByDescending(item => item.CopiedAt).ToArray());
        var json = JsonSerializer.SerializeToUtf8Bytes(cached); var wireBytes = json.Length;
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(json);
        started = GC.GetTotalAllocatedBytes(true);
        for (var iteration = 0; iteration < 1000; iteration++)
            GC.KeepAlive(rows.OrderByDescending(item => item.Pinned).ThenByDescending(item => item.CopiedAt).ToArray());
        var sorting = GC.GetTotalAllocatedBytes(true) - started;
        started = GC.GetTotalAllocatedBytes(true);
        for (var iteration = 0; iteration < 1000; iteration++) GC.KeepAlive(cached);
        var reuse = GC.GetTotalAllocatedBytes(true) - started;
        var report = new { Scenario = "In-memory representation of 1000 synthetic maximum-length summaries with titles/eight labels; 1000 former sort/materialize operations versus reusing one read-only snapshot. Excludes storage/IPC/UI overhead, payloads, native memory and paging implementation. No forced GC or clipboard.",
            Runtime = Environment.Version.ToString(), Entries = rows.Length, ConstructionAllocatedBytes = construction, JsonMetadataBytes = wireBytes,
            SortMaterializeAllocatedBytes = sorting, SnapshotReuseAllocatedBytes = reuse };
        var path = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static async Task RunDecodeAsync(string output)
    {
        var report = await Task.Run(() =>
        {
            const int size = 2048;
            var pixels = new byte[size * size * 4]; new Random(724).NextBytes(pixels);
            for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
            var source = System.Windows.Media.Imaging.BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4); source.Freeze();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using var encoded = new BoundedImageBuffer(16 * 1024 * 1024); encoder.Save(encoded); var png = encoded.ToArray();
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(pixels);
            _ = PreviewCache.Decode(png, size, size);
            using var process = Process.GetCurrentProcess(); var samples = new List<object>();
            var allocated = GC.GetTotalAllocatedBytes(true); var managedBefore = GC.GetTotalMemory(false);
            for (var cycle = 0; cycle < 8; cycle++)
            {
                var watch = Stopwatch.StartNew(); var decoded = PreviewCache.Decode(png, size, size);
                watch.Stop(); process.Refresh();
                samples.Add(new { Cycle = cycle, DecodeMs = watch.Elapsed.TotalMilliseconds, PrivateBytesAfterDecode = process.PrivateMemorySize64,
                    WorkingSetAfterDecode = process.WorkingSet64, Frozen = decoded.IsFrozen, decoded.PixelWidth, decoded.PixelHeight });
                GC.KeepAlive(decoded);
            }
            var decodeAllocated = GC.GetTotalAllocatedBytes(true) - allocated; var managedAfter = GC.GetTotalMemory(false);
            var detailSamples = new List<object>();
            foreach (var target in new[] { (Name: "Former fixed detail", Width: 1600, Height: 1000),
                (Name: "1600x562 overlay at 100%", Width: 1600, Height: 562),
                (Name: "1600x562 overlay at 150%", Width: 2400, Height: 843) })
            {
                var decoded = PreviewCache.Decode(png, target.Width, target.Height);
                detailSamples.Add(new { target.Name, decoded.PixelWidth, decoded.PixelHeight, DecodedPixelBytes = (long)decoded.PixelWidth * decoded.PixelHeight * 4 });
                GC.KeepAlive(decoded);
            }
            var result = new { Scenario = "Eight full-size decodes of one synthetic 2048x2048 PNG after one warmup, plus former/display-sized detail comparison; no clipboard, window, forced GC or working-set trim. Post-decode samples are not native peak memory; decoded pixel bytes exclude source raster, WIC scratch and compositor allocations.",
                Runtime = Environment.Version.ToString(), EncodedBytes = png.Length, DecodeAllocatedBytes = decodeAllocated,
                ManagedBytesBefore = managedBefore, ManagedBytesAfter = managedAfter, Samples = samples, DetailSamples = detailSamples };
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(png); return result;
        });
        var path = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static async Task RunScrollAsync(HistoryStore store, string output, bool software = false)
    {
        if (software) RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        using var process = Process.GetCurrentProcess();
        var cycles = new List<object>();
        for (var cycle = 0; cycle < 4; cycle++)
        {
            var window = new RopeWindow(store, preview: true, settings: new AppSettings { Animate = true }) { Width = 1600, Height = 900, ShowActivated = false, Topmost = false };
            window.Show(); window.RenderDemo(1000); window.UpdateLayout(); window.DiagnosticCompactSurface();
            await Task.Delay(500);
            var samples = new List<double>(); TimeSpan? previous = null;
            EventHandler frame = (_, args) => { var time = ((RenderingEventArgs)args).RenderingTime; if (previous is TimeSpan last && time > last) samples.Add((time - last).TotalMilliseconds); previous = time; };
            var allocated = GC.GetTotalAllocatedBytes(); var cpu = process.TotalProcessorTime; var created = window.CreatedNoteCount;
            CompositionTarget.Rendering += frame;
            try
            {
                // Sustained fractional gesture, many viewport boundaries, then reversal.
                for (var tick = 0; tick < 360; tick++) { window.DiagnosticGesture(tick < 180 ? 32.5 : -32.5); await Task.Delay(8); }
                await Task.Delay(700);
            }
            finally { CompositionTarget.Rendering -= frame; }
            process.Refresh(); var sorted = samples.Order().ToArray();
            cycles.Add(new { Cycle = cycle, Frames = sorted.Length, P95FrameGapMs = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * .95)], MaxFrameGapMs = sorted.LastOrDefault(), GapsOver33Ms = sorted.Count(x => x > 33.4), CreatedCards = window.CreatedNoteCount - created, AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated, CpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds, WorkingSetBytes = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64, RetainedPreviewBytes = window.CachedImageBytes, FinalOffset = window.ScrollOffset });
            await window.CloseRopeAsync();
        }
        var path = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Scenario = "Four sustained 360-update precision gestures and reversals over 1000 synthetic notes; rendering callbacks, not display FPS; no clipboard or forced GC", SoftwareRendering = software, RendererRequested = RenderOptions.ProcessRenderMode.ToString(), Cycles = cycles }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static async Task RunAsync(HistoryStore store, string output, bool software)
    {
        if (software) RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        using var process = Process.GetCurrentProcess();
        object Memory() { process.Refresh(); return new { WorkingSet = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64, ManagedBytes = GC.GetTotalMemory(false), Handles = process.HandleCount }; }
        var baseline = Memory(); var cycles = new List<object>();
        for (var cycle = 0; cycle < 8; cycle++)
        {
            var samples = new List<double>(); var watch = Stopwatch.StartNew(); double last = -1;
            EventHandler frame = (_, _) => { var now = watch.Elapsed.TotalMilliseconds; if (last >= 0) samples.Add(now - last); last = now; };
            var window = new RopeWindow(store, preview: true, settings: new AppSettings { Animate = true }) { Width = 1600, Height = 900, ShowActivated = false, Topmost = false };
            window.Show(); window.RenderDemo(1000); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            CompositionTarget.Rendering += frame;
            object shown;
            try
            {
                window.AnimateEntrance(); await Task.Delay(1600);
                // Exercise rapid retargeting without sending input to another app.
                window.DiagnosticScroll(500); await Task.Delay(70); window.DiagnosticScroll(900); await Task.Delay(70); window.DiagnosticScroll(250);
                await Task.Delay(1000);
                if (cycle % 2 == 1) { window.OpenSettings(); await Task.Delay(700); }
                shown = Memory();
                await window.CloseRopeAsync();
            }
            finally { CompositionTarget.Rendering -= frame; if (!window.Retired) window.Close(); }
            await Task.Delay(350); var sorted = samples.Order().ToArray();
            cycles.Add(new { Cycle = cycle, Shown = shown, Closed = Memory(), Frames = sorted.Length, MedianFrameGapMs = sorted.Length == 0 ? 0 : sorted[sorted.Length / 2], P95FrameGapMs = sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * .95)], MaxFrameGapMs = sorted.LastOrDefault(), GapsOver33Ms = sorted.Count(x => x > 33.4), CardsAfterClose = window.RenderedNoteCount, CachedBytesAfterClose = window.CachedImageBytes });
        }
        var idleCpu = process.TotalProcessorTime; var idleWatch = Stopwatch.StartNew();
        await Task.Delay(10000); process.Refresh();
        var report = new { Scenario = "Eight synthetic 1000-note open/scroll/settings/close cycles; no clipboard, screenshot or forced GC", Runtime = Environment.Version.ToString(), SoftwareRendering = software, RenderingTier = RenderCapability.Tier >> 16, Baseline = baseline, Cycles = cycles, Idle = Memory(), IdleCpuPercentOneCore = (process.TotalProcessorTime - idleCpu).TotalSeconds / idleWatch.Elapsed.TotalSeconds * 100 };
        var path = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
