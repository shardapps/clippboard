using System.Windows;
using System.Windows.Media;

namespace Clippboard.App.Design;

// Original vector icons; no platform symbol font or external asset dependency.
public sealed class IconView : FrameworkElement
{
    public static readonly DependencyProperty InkProperty = DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(IconView), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Ink { get => (Brush)GetValue(InkProperty); set => SetValue(InkProperty, value); }
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(nameof(Symbol), typeof(string), typeof(IconView), new FrameworkPropertyMetadata("logo", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Symbol { get => (string)GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    public IconView() { Width = 22; Height = 22; IsHitTestVisible = false; SetResourceReference(InkProperty, "Foreground"); }
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        var pen = new Pen(Ink, 1.65) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var path = Symbol switch
        {
            "star" or "star-outline" => "M12,2.5 L15,8.7 L22,9.7 L17,14.5 L18.2,21.3 L12,18.1 L5.8,21.3 L7,14.5 L2,9.7 L9,8.7 Z",
            "close" => "M6,6 L18,18 M18,6 L6,18",
            "expand" => "M4,10 L4,4 L10,4 M14,4 L20,4 L20,10 M20,14 L20,20 L14,20 M10,20 L4,20 L4,14",
            "up" => "M5,15 L12,8 L19,15",
            "left" => "M15,5 L8,12 L15,19",
            "right" => "M9,5 L16,12 L9,19",
            "pause" => "M8,5 L8,19 M16,5 L16,19",
            "play" => "M7,4 L20,12 L7,20 Z",
            "settings" => "M12,3 A9,9 0 1 1 11.99,3 M12,8 A4,4 0 1 1 11.99,8 M12,0 L12,4 M12,20 L12,24 M0,12 L4,12 M20,12 L24,12",
            "search" => "M10,3 A7,7 0 1 1 9.99,3 M15,15 L21,21",
            _ => "M6,5 L18,5 Q20,5 20,7 L20,20 Q20,22 18,22 L6,22 Q4,22 4,20 L4,7 Q4,5 6,5 M9,9 L15,9 M8,14 L16,14 M8,18 L14,18 M10,1 L14,1 L14,8 L10,8 Z"
        };
        dc.DrawGeometry(Symbol == "star" ? Ink : null, pen, Geometry.Parse(path));
        dc.Pop();
    }
}
