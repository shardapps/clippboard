using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Clippboard.Core;

namespace Clippboard.App.Design;

public sealed class RopeVisual : FrameworkElement
{
    public static readonly DependencyProperty SagProperty = DependencyProperty.Register(nameof(Sag), typeof(double), typeof(RopeVisual),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsRender, (o, _) => ((RopeVisual)o).ShapeChanged?.Invoke()));
    public double Sag { get => (double)GetValue(SagProperty); set => SetValue(SagProperty, value); }
    public event Action? ShapeChanged;
    public double HeightAt(double x) => MotionMath.RopeHeight(x, ActualWidth, Sag);
    protected override void OnRender(DrawingContext dc)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(-6, 20), false, false);
            path.QuadraticBezierTo(new Point(ActualWidth / 2, 20 + 2 * Sag), new Point(ActualWidth + 6, 20), true, false);
        }
        geometry.Freeze();
        dc.PushTransform(new TranslateTransform(0, 2)); dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(35, 0, 0, 0)), 10), geometry); dc.Pop();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(145, 120, 86)), 9), geometry);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(206, 183, 146)), 7), geometry);
        var braid = new Pen(new SolidColorBrush(Color.FromRgb(235, 219, 188)), 2) { DashStyle = new DashStyle([1, 2.5], 0) };
        dc.DrawGeometry(null, braid, geometry);
    }
}

internal sealed class SpringEase : EasingFunctionBase
{
    public SpringEase() => EasingMode = EasingMode.EaseIn;
    public bool Bounce { get; set; }
    protected override double EaseInCore(double normalizedTime) => MotionMath.Settle(normalizedTime, Bounce);
    protected override Freezable CreateInstanceCore() => new SpringEase { Bounce = Bounce };
}
