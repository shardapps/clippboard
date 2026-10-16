using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Clippboard.Core;

namespace Clippboard.App.Services;

internal sealed record ResourceSample(long PrivateBytes, long WorkingSet, int Handles);
internal static class SupportDiagnostics
{
    internal static ResourceSample? Sample(Process? process)
    {
        if (process is null) return null;
        try { process.Refresh(); return new(process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }
    internal static string Collect(AppSettings settings, Process? overlay, bool captureSuspended, bool transferActive)
    {
        using var listener = Process.GetCurrentProcess();
        return Format(settings, Sample(listener), Sample(overlay), Platform.PackageIdentity.IsPackaged, captureSuspended, transferActive);
    }
    internal static string Format(AppSettings settings, ResourceSample? listener, ResourceSample? overlay, bool packaged, bool captureSuspended, bool transferActive)
    {
        var report = new StringBuilder(Feedback.SupportInfo());
        report.AppendLine().AppendLine("Resource snapshot (not a leak or latency benchmark)");
        Add("Listener", listener); Add("Overlay / prepared runtime", overlay);
        if (listener is not null && overlay is not null)
            report.AppendLine("Combined private memory: " + MiB(listener.PrivateBytes + overlay.PrivateBytes));
        report.AppendLine("Working sets can contain shared pages; do not add them to estimate RAM.");
        report.AppendLine($"Store package identity: {packaged}");
        report.AppendLine($"Capture paused or suspended: {settings.IsCapturePaused || captureSuspended}; transfer active: {transferActive}");
        report.AppendLine($"Rendering: {(settings.LowMemoryRendering ? "software requested" : "Windows default")}; fast opening: {settings.FastOpen}; animation: {settings.Animate}");
        report.AppendLine($"Limits: {settings.MaxEntries} notes, {settings.StorageMiB} MiB disk, {settings.RetentionDays} days, {settings.PreviewCacheMiB} MiB configured preview ceiling");
        report.AppendLine("No history was read. No note contents, titles, labels, paths, excluded-app names, process IDs, passwords or account identifiers included. Nothing is uploaded.");
        return report.ToString();
        void Add(string name, ResourceSample? sample) => report.AppendLine(sample is null ? name + ": unavailable"
            : $"{name}: private {MiB(sample.PrivateBytes)}, working set {MiB(sample.WorkingSet)}, handles {sample.Handles}");
    }
    private static string MiB(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
}
