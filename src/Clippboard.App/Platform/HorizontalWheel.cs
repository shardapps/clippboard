using System;
using System.Runtime.InteropServices;

namespace Clippboard.App.Platform;

internal static class HorizontalWheel
{
    internal const int Message = 0x020E;
    internal static int Delta(nint parameter) => unchecked((short)((long)parameter >> 16));
    internal static int ScreenX(nint parameter) => unchecked((short)(long)parameter);
    internal static int ScreenY(nint parameter) => unchecked((short)((long)parameter >> 16));
    internal static bool ControlDown(nint parameter) => ((long)parameter & 0x0008) != 0;

    // Preserve fractional wheel ticks: precision touchpads must not wait for 120.
    internal static double Distance(int delta, uint characters, double viewport)
        => delta / 120.0 * (characters == uint.MaxValue ? viewport : Math.Min(characters, 100) * 24.0);

    internal static uint ScrollCharacters() => SystemParametersInfo(0x006C, 0, out var value, 0) ? value : 3;
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
}
