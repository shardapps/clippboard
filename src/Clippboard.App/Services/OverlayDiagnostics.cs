using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
            using var overlay = new OverlayProcess(_ => Task.FromResult(new OverlayReply(Settings: new AppSettings(), StartupAvailable: false, Demo: true)), software);
            var watch = Stopwatch.StartNew(); long peak = 0; long combined = 0;
            while (!overlay.Process.HasExited)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new IOException("Overlay diagnostic exceeded its deadline.");
                try { overlay.Process.Refresh(); peak = Math.Max(peak, overlay.Process.WorkingSet64); current.Refresh(); combined = Math.Max(combined, overlay.Process.WorkingSet64 + current.WorkingSet64); }
                catch (InvalidOperationException) when (overlay.Process.HasExited) { }
                await Task.Delay(100);
            }
            if (overlay.Process.ExitCode != 0) throw new IOException("Overlay diagnostic failed.");
            await Task.Delay(350);
            cycles.Add(new { Cycle = cycle + 1, ChildPeakWorkingSetBytes = peak, CombinedPeakWorkingSetBytes = combined, Seconds = watch.Elapsed.TotalSeconds, Closed = Sample() });
        }
        var cpu = current.TotalProcessorTime; var idle = Stopwatch.StartNew(); await Task.Delay(10000); current.Refresh();
        var report = new { Scenario = "Eight disposable overlay processes; synthetic 1000 notes, scroll and settings; no clipboard, forced GC or working-set trim", SoftwareRendering = software,
            Runtime = Environment.Version.ToString(), Before = before, Cycles = cycles, Idle = Sample(), IdleCpuPercentOneCore = Math.Round((current.TotalProcessorTime - cpu).TotalSeconds / idle.Elapsed.TotalSeconds * 100, 3) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
