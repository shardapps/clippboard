using System;
using System.Windows.Media;

namespace Clippboard.App.Views;

public partial class RopeWindow
{
    private double _availableHeightPixels;
    private double _surfaceHeight;
    private double AvailableHeight => _availableHeightPixels > 0 ? Math.Min(_surfaceHeight, _availableHeightPixels / VisualTreeHelper.GetDpi(this).DpiScaleY) : ActualHeight;
    private void PrepareSurface(double availablePixels)
    {
        _availableHeightPixels = availablePixels;
        // Measure typography, toolbar wrapping and board geometry once before
        // creating the native surface. Its size follows content and the monitor.
        Panel.Measure(new System.Windows.Size(Width, double.PositiveInfinity));
        _surfaceHeight = Math.Max(Panel.DesiredSize.Height + SidebarHost.Margin.Top + SidebarHost.Margin.Bottom,
            DetailHost.Height + DetailHost.Margin.Top + DetailHost.Margin.Bottom);
        Height = AvailableHeight;
    }
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
        PrepareSurface((ActualHeight > 0 ? ActualHeight : Height) * VisualTreeHelper.GetDpi(this).DpiScaleY);
    }
}
