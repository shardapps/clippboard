using System.Runtime.InteropServices;

namespace Clippboard.App.Platform;

internal static class PackageIdentity
{
    internal static bool IsPackaged
    {
        get { uint length = 0; return GetCurrentPackageFullName(ref length, 0) != 15700; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, nint name);
}
