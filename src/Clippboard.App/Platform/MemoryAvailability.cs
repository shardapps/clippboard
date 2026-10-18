using System;
using System.Runtime.InteropServices;
using Clippboard.Core;

namespace Clippboard.App.Platform;

internal static class MemoryAvailability
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Status
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref Status status);
    internal static long AvailableBytes()
    {
        var status = new Status { Length = (uint)Marshal.SizeOf<Status>() };
        return GlobalMemoryStatusEx(ref status) ? Headroom(status.AvailablePhysical, status.AvailablePageFile) : -1;
    }
    internal static long Headroom(ulong physical, ulong commit) => (long)Math.Min(Math.Min(physical, commit), (ulong)long.MaxValue);
    internal static bool ImageFits(long sourcePixels, long outputPixels, long encodedBytes, long available)
    {
        if (sourcePixels is <= 0 or > ImageLimits.MaxPixels || outputPixels is <= 0 or > ImageLimits.MaxPixels
            || encodedBytes is < 0 or > ImageLimits.MaxEncodedBytes) return false;
        // Conservatively account for source raster plus two output buffers and
        // encoded input, leaving most headroom to Windows and concurrent work.
        var estimate = sourcePixels * 4 + outputPixels * 8 + encodedBytes;
        return available < 0 || estimate <= available / 4;
    }
    internal static long PreviewBudget(long userLimit, int viewportCards, long bytesPerPreview, long available)
    {
        // Keep approximately two viewports warm, within the user's ceiling and
        // one percent of currently available physical RAM. Zero means no cache.
        var demand = Math.Max(0, viewportCards) * Math.Max(0, bytesPerPreview) * 2;
        return Math.Max(0, Math.Min(Math.Min(userLimit, demand), available >= 0 ? available / 100 : demand));
    }
    internal static int DecodeConcurrency(long available, int processors, long maxPayload)
        => (int)Math.Clamp(Math.Min(Math.Max(1, processors), available > 0 ? available / 100 / Math.Max(1, maxPayload) : 1), 1, 2);
    internal static long RetainedSurfaceBudget(long demand, long available)
        // Visible render targets are separate from the optional image cache.
        // Reserve source, snapshot and worst rotation before allocating, using
        // viewport geometry and at most four percent of current headroom.
        => Math.Max(0, Math.Min(Math.Max(0, demand), available >= 0 ? available / 25 : Math.Max(0, demand)));
    internal static bool KeepStandby(long available, long measuredPrivateBytes, bool alreadyRunning)
        // Measured cost rather than a fixed per-machine RAM allowance. Separate
        // start/stop thresholds prevent restarting around a pressure boundary.
        => available > 0 && measuredPrivateBytes > 0 && measuredPrivateBytes <= available / (alreadyRunning ? 50 : 75);
    internal static bool StartStandby(long available, long measuredRuntimeBytes, long bootstrapEstimate)
        // Probe only a runtime, never a native surface. Once its actual cost is
        // known, all later starts use the normal, tighter retention budget.
        => measuredRuntimeBytes > 0 ? KeepStandby(available, measuredRuntimeBytes, false)
            : available > 0 && bootstrapEstimate > 0 && bootstrapEstimate <= available / 8;
    internal static bool PrepareControls(long available, long measuredControlsBytes, long bootstrapEstimate)
        // Controls expand an already-running runtime; their measured total
        // cost must fit the retention budget, not another process-start budget.
        => KeepStandby(available, measuredControlsBytes > 0 ? measuredControlsBytes : bootstrapEstimate, true);
}
