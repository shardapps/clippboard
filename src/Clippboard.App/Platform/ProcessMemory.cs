using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clippboard.App.Platform;

internal static class ProcessMemory
{
    [StructLayout(LayoutKind.Sequential)] private struct Counters
    {
        internal uint Size, Faults;
        internal nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool;
        internal nuint PageFile, PeakPageFile, PrivateUsage, PrivateWorkingSet;
        internal ulong SharedCommit;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessMemoryInfo(nint process, ref Counters counters, uint size);
    internal static long? PrivateWorkingSet(Process process)
    {
        try
        {
            var counters = new Counters { Size = (uint)Marshal.SizeOf<Counters>() };
            // EX2 is unavailable on early Windows 11 builds. Missing telemetry
            // is unavailable, never a zero-RAM success or a compatibility error.
            return GetProcessMemoryInfo(process.Handle, ref counters, counters.Size) && counters.PrivateWorkingSet > 0
                ? checked((long)counters.PrivateWorkingSet) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or OverflowException) { return null; }
    }
}
