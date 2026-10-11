using System;
using System.Runtime.InteropServices;

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
        return GlobalMemoryStatusEx(ref status) ? (long)Math.Min(status.AvailablePhysical, (ulong)long.MaxValue) : 0;
    }
    internal static long PreviewBudget(long userLimit, int viewportCards, long bytesPerPreview, long available)
    {
        // Keep approximately two viewports warm, within the user's ceiling and
        // one percent of currently available physical RAM. Zero means no cache.
        var demand = Math.Max(0, viewportCards) * Math.Max(0, bytesPerPreview) * 2;
        return Math.Max(0, Math.Min(Math.Min(userLimit, demand), available > 0 ? available / 100 : demand));
    }
    internal static int DecodeConcurrency(long available, int processors, long maxPayload)
        => (int)Math.Clamp(Math.Min(Math.Max(1, processors), available > 0 ? available / 100 / Math.Max(1, maxPayload) : 1), 1, 2);
}
