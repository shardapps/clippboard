using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clippboard.Core;

namespace Clippboard.App.Services;

internal static class OverlayDiagnostics
{
    internal static async Task RunAsync(string output, bool software)
    {
        using var current = Process.GetCurrentProcess();
        object Sample() { current.Refresh(); return new { WorkingSetBytes = current.WorkingSet64, PrivateBytes = current.PrivateMemorySize64, ManagedBytes = GC.GetTotalMemory(false), Handles = current.HandleCount }; }
        var before = Sample(); var cycles = new List<object>();
        for (var cycle = 0; cycle < 8; cycle++)
        {
            string? renderer = null; double? firstFrameMs = null;
            var standby = cycle % 2 == 1; var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = Stopwatch.StartNew();
            var preferencePath = software && cycle % 2 == 1;
            OverlayProcess? child = null;
            using var overlay = child = new OverlayProcess(async request =>
            {
                if (request.Command == "diagnostic-renderer") renderer = request.Id;
                if (request.Command == "diagnostic-first-frame") firstFrameMs ??= watch.Elapsed.TotalMilliseconds;
                if (request.Command == "standby-ready") ready.TrySetResult(request.Flag);
                if (request.Command == "wait-open") await child!.WaitForOpenAsync();
                return new OverlayReply(Settings: new AppSettings { LowMemoryRendering = preferencePath }, StartupAvailable: false, Demo: true, Standby: standby && child?.OpenRequested != true);
            }, software && !preferencePath, standby);
            double? prepareMs = null; long? preparedPrivate = null; long? preparedWorkingSet = null;
            if (standby)
            {
                if (!await ready.Task.WaitAsync(TimeSpan.FromSeconds(15))) throw new IOException("Standby overlay created a native surface.");
                prepareMs = watch.Elapsed.TotalMilliseconds;
                overlay.Process.Refresh(); preparedPrivate = overlay.Process.PrivateMemorySize64; preparedWorkingSet = overlay.Process.WorkingSet64;
                watch.Restart(); overlay.RequestOpen();
            }
            long peak = 0; long combined = 0; long privatePeak = 0; long combinedPrivate = 0;
            while (!overlay.Process.HasExited)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new IOException("Overlay diagnostic exceeded its deadline.");
                try { overlay.Process.Refresh(); peak = Math.Max(peak, overlay.Process.WorkingSet64); privatePeak = Math.Max(privatePeak, overlay.Process.PrivateMemorySize64); current.Refresh(); combined = Math.Max(combined, overlay.Process.WorkingSet64 + current.WorkingSet64); combinedPrivate = Math.Max(combinedPrivate, overlay.Process.PrivateMemorySize64 + current.PrivateMemorySize64); }
                catch (InvalidOperationException) when (overlay.Process.HasExited) { }
                await Task.Delay(100);
            }
            if (overlay.Process.ExitCode != 0) throw new IOException("Overlay diagnostic failed.");
            if (firstFrameMs is null) throw new IOException("Overlay did not report its first rendered content.");
            if (renderer != (software ? "SoftwareOnly" : "Default")) throw new IOException("Overlay rendering preference did not apply.");
            await Task.Delay(350);
            cycles.Add(new { Cycle = cycle + 1, RenderingMode = renderer, PreferencePath = preferencePath, Prepared = standby, PreparationMs = prepareMs, PreparedPrivateBytes = preparedPrivate, PreparedWorkingSetBytes = preparedWorkingSet, FirstContentRenderedMs = firstFrameMs, ChildPeakWorkingSetBytes = peak, CombinedPeakWorkingSetBytes = combined, ChildPeakPrivateBytes = privatePeak, CombinedPeakPrivateBytes = combinedPrivate, Seconds = watch.Elapsed.TotalSeconds, Closed = Sample() });
        }
        // Pressure release must cancel a waiting server, not leave an idle
        // long-poll worker behind. This scenario never creates a window.
        var idleReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        OverlayProcess? pending = null;
        using (var standby = pending = new OverlayProcess(async request =>
        {
            if (request.Command == "standby-ready") idleReady.TrySetResult(request.Flag);
            if (request.Command == "wait-open") await pending!.WaitForOpenAsync();
            return new OverlayReply(Standby: true);
        }, standby: true))
        {
            if (!await idleReady.Task.WaitAsync(TimeSpan.FromSeconds(15))) throw new IOException("Idle worker created a window.");
            var server = standby.ServerTask;
            standby.Dispose();
            await server.WaitAsync(TimeSpan.FromSeconds(3));
        }
        var cpu = current.TotalProcessorTime; var idle = Stopwatch.StartNew(); await Task.Delay(10000); current.Refresh();
        var report = new { Scenario = "Eight disposable overlay processes, alternating cold/prepared launch; synthetic 1000 notes, scroll and settings; no clipboard, forced GC or working-set trim. Prepared startup has no native surface. First-content event is not physical presentation latency.", SoftwareRendering = software,
            Runtime = Environment.Version.ToString(), Before = before, Cycles = cycles, StandbyReleasePassed = true, Idle = Sample(), IdleCpuPercentOneCore = Math.Round((current.TotalProcessorTime - cpu).TotalSeconds / idle.Elapsed.TotalSeconds * 100, 3) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
