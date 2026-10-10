using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Clippboard.Core;

namespace Clippboard.App.Design;

public sealed class RopeVisual : FrameworkElement
{
    private static readonly Pen ShadowPen = MakePen(Color.FromArgb(35, 0, 0, 0), 10);
    private static readonly Pen EdgePen = MakePen(Color.FromRgb(145, 120, 86), 9);
    private static readonly Pen CordPen = MakePen(Color.FromRgb(206, 183, 146), 7);
    private static readonly Pen BraidPen = MakePen(Color.FromRgb(235, 219, 188), 2, new DashStyle([1, 2.5], 0));
    private static Pen MakePen(Color color, double width, DashStyle? dash = null)
    {
        var pen = new Pen(new SolidColorBrush(color), width) { DashStyle = dash ?? DashStyles.Solid }; pen.Freeze(); return pen;
    }
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
        dc.PushTransform(new TranslateTransform(0, 2)); dc.DrawGeometry(null, ShadowPen, geometry); dc.Pop();
        dc.DrawGeometry(null, EdgePen, geometry); dc.DrawGeometry(null, CordPen, geometry); dc.DrawGeometry(null, BraidPen, geometry);
    }
}

internal sealed class SpringEase : EasingFunctionBase
{
    public SpringEase() => EasingMode = EasingMode.EaseIn;
    public bool Bounce { get; set; }
    protected override double EaseInCore(double normalizedTime) => MotionMath.Settle(normalizedTime, Bounce);
    protected override Freezable CreateInstanceCore() => new SpringEase { Bounce = Bounce };
}
internal sealed class SwayEase : EasingFunctionBase
{
    public SwayEase() => EasingMode = EasingMode.EaseIn;
    protected override double EaseInCore(double normalizedTime) => MotionMath.Sway(normalizedTime);
    protected override Freezable CreateInstanceCore() => new SwayEase();
}
