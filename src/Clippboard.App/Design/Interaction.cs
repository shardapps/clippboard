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
        if (element is CheckBox checkbox)
        {
            if ((bool)e.NewValue) { checkbox.Checked += SwitchChanged; checkbox.Unchecked += SwitchChanged; checkbox.Loaded += SwitchLoaded; }
            else { checkbox.Checked -= SwitchChanged; checkbox.Unchecked -= SwitchChanged; checkbox.Loaded -= SwitchLoaded; }
        }
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
    private static void Enter(object sender, System.Windows.Input.MouseEventArgs e) => Hover((FrameworkElement)sender, true);
    private static void Leave(object sender, System.Windows.Input.MouseEventArgs e) => Hover((FrameworkElement)sender, false);
    private static void Hover(FrameworkElement element, bool over)
    {
        if (element is Button or System.Windows.Controls.Primitives.Thumb) Scale(element, over ? 1.035 : 1);
        if (element is Control control && control.Template?.FindName("HoverSurface", control) is FrameworkElement surface)
            Move(surface, UIElement.OpacityProperty, over ? 1 : 0, GetEnabled(element), over ? 140 : 200);
    }
    private static void SwitchChanged(object sender, RoutedEventArgs e) => SetSwitch((CheckBox)sender, ((CheckBox)sender).IsLoaded);
    private static void SwitchLoaded(object sender, RoutedEventArgs e) => SetSwitch((CheckBox)sender, false);
    private static void SetSwitch(CheckBox checkbox, bool animate)
    {
        if (checkbox.Template?.FindName("KnobMotion", checkbox) is TranslateTransform knob)
            Move(knob, TranslateTransform.XProperty, checkbox.IsChecked == true ? 14 : 0, animate && GetEnabled(checkbox), 220);
    }
    private static void Scale(FrameworkElement element, double target)
    {
        if (element.RenderTransform is not ScaleTransform scale) { scale = new ScaleTransform(1, 1); element.RenderTransform = scale; element.RenderTransformOrigin = new Point(.5, .5); }
        Move(scale, ScaleTransform.ScaleXProperty, target, GetEnabled(element)); Move(scale, ScaleTransform.ScaleYProperty, target, GetEnabled(element));
    }
    internal static void Move(Animatable value, DependencyProperty property, double target, bool animate, int milliseconds = 240)
        => SmoothMotion.Move(value, property, target, animate, milliseconds);
    internal static void Move(FrameworkElement value, DependencyProperty property, double target, bool animate, int milliseconds = 240)
        => SmoothMotion.Move(value, property, target, animate, milliseconds);
}
