using System;
using System.Windows.Media;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    private double _availableHeightPixels;
    private double AvailableHeight => _availableHeightPixels > 0 ? Math.Min(720, _availableHeightPixels / VisualTreeHelper.GetDpi(this).DpiScaleY) : ActualHeight;
    private void FitSurface()
    {
        if (_availableHeightPixels <= 0 || Retired || _closing) return;
        // One bounded surface fits both the rope and its panels. Repeated native
        // resizes can retain extra compositor buffers, so opening panels does not
        // resize the window. Outside clicks dismiss through deactivation.
        Height = AvailableHeight;
    }
    internal void DiagnosticCompactSurface()
    {
        if (!_preview) throw new InvalidOperationException("Synthetic surface configuration requires preview mode.");
        _availableHeightPixels = ActualHeight * VisualTreeHelper.GetDpi(this).DpiScaleY;
        FitSurface();
    }
}
