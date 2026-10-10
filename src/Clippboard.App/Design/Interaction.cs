using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Clippboard.App.Design;

// One motion policy for controls, panels and notes, inherited from the overlay.
public static class Interaction
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(Interaction), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty) && SystemParameters.ClientAreaAnimation;
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
    public static readonly DependencyProperty EnhanceProperty = DependencyProperty.RegisterAttached("Enhance", typeof(bool), typeof(Interaction), new PropertyMetadata(false, Changed));
    public static bool GetEnhance(DependencyObject value) => (bool)value.GetValue(EnhanceProperty);
    public static void SetEnhance(DependencyObject value, bool enabled) => value.SetValue(EnhanceProperty, enabled);
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        if (value is not FrameworkElement element) return;
        if ((bool)e.NewValue) { element.ToolTipOpening += Tip; element.MouseEnter += Enter; element.MouseLeave += Leave; }
        else { element.ToolTipOpening -= Tip; element.MouseEnter -= Enter; element.MouseLeave -= Leave; }
    }
    private static void Tip(object sender, ToolTipEventArgs e)
    {
        PrepareToolTip((FrameworkElement)sender);
    }
    internal static void PrepareToolTip(FrameworkElement owner)
    {
        if (owner.ToolTip is string text) owner.ToolTip = new ToolTip { Content = text };
        if (owner.ToolTip is not ToolTip tip) return;
        // Popups have a separate visual tree: take the actual owner's palette explicitly.
        tip.Background = (Brush)owner.FindResource("Chrome");
        tip.Foreground = (Brush)owner.FindResource("Foreground");
        tip.BorderBrush = (Brush)owner.FindResource("Outline");
    }
    private static void Enter(object sender, System.Windows.Input.MouseEventArgs e) => Scale((FrameworkElement)sender, 1.035);
    private static void Leave(object sender, System.Windows.Input.MouseEventArgs e) => Scale((FrameworkElement)sender, 1);
    private static void Scale(FrameworkElement element, double target)
    {
        if (element.RenderTransform is not ScaleTransform scale) { scale = new ScaleTransform(1, 1); element.RenderTransform = scale; element.RenderTransformOrigin = new Point(.5, .5); }
        Move(scale, ScaleTransform.ScaleXProperty, target, GetEnabled(element)); Move(scale, ScaleTransform.ScaleYProperty, target, GetEnabled(element));
    }
    internal static void Move(Animatable value, DependencyProperty property, double target, bool animate, int milliseconds = 240)
    {
        var current = (double)value.GetValue(property); value.BeginAnimation(property, null); value.SetValue(property, target);
        if (animate && Math.Abs(current - target) > .001) value.BeginAnimation(property, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.Stop, EasingFunction = new SpringEase { Bounce = false } });
    }
    internal static void Move(FrameworkElement value, DependencyProperty property, double target, bool animate, int milliseconds = 240)
    {
        var current = (double)value.GetValue(property); value.BeginAnimation(property, null); value.SetValue(property, target);
        if (animate && double.IsFinite(current) && Math.Abs(current - target) > .001) value.BeginAnimation(property, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.Stop, EasingFunction = new SpringEase { Bounce = false } });
    }
}
