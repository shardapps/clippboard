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
