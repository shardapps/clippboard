using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;
using Clippboard.Core;

namespace Clippboard.App.Design;

internal sealed class SpringAnimation : DoubleAnimationBase
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(nameof(From), typeof(double), typeof(SpringAnimation));
    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(nameof(To), typeof(double), typeof(SpringAnimation));
    public static readonly DependencyProperty VelocityProperty = DependencyProperty.Register(nameof(Velocity), typeof(double), typeof(SpringAnimation));
    public static readonly DependencyProperty FrequencyProperty = DependencyProperty.Register(nameof(Frequency), typeof(double), typeof(SpringAnimation), new PropertyMetadata(20.0));
    public static readonly DependencyProperty DampingProperty = DependencyProperty.Register(nameof(Damping), typeof(double), typeof(SpringAnimation), new PropertyMetadata(1.0));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(SpringAnimation), new PropertyMetadata(double.NegativeInfinity));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SpringAnimation), new PropertyMetadata(double.PositiveInfinity));
    public double From { get => (double)GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public double To { get => (double)GetValue(ToProperty); set => SetValue(ToProperty, value); }
    public double Velocity { get => (double)GetValue(VelocityProperty); set => SetValue(VelocityProperty, value); }
    public double Frequency { get => (double)GetValue(FrequencyProperty); set => SetValue(FrequencyProperty, value); }
    public double Damping { get => (double)GetValue(DampingProperty); set => SetValue(DampingProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    protected override Freezable CreateInstanceCore() => new SpringAnimation();
    protected override double GetCurrentValueCore(double defaultOriginValue, double defaultDestinationValue, AnimationClock clock)
        => Math.Clamp(MotionMath.Spring(From, Velocity, To, clock.CurrentTime?.TotalSeconds ?? 0, Frequency, Damping).Position, Minimum, Maximum);
}

internal static class SmoothMotion
{
    internal static event Action<DependencyObject>? Changed;
    private sealed record State(SpringAnimation Animation, AnimationClock Clock);
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, State>> States = new();
    internal static void CancelTree(DependencyObject target)
    {
        if (States.TryGetValue(target, out var properties))
        {
            foreach (var property in new List<DependencyProperty>(properties.Keys)) { var current = target.GetValue(property); target.SetValue(property, current); Apply(target, property, null); properties[property].Clock.Controller?.Remove(); }
            properties.Clear(); States.Remove(target);
        }
        if (target is UIElement element) CancelTree(element.RenderTransform);
        if (target is System.Windows.Media.TransformGroup group) foreach (var transform in group.Children) CancelTree(transform);
        if (target is System.Windows.Media.Visual)
            for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(target); index++) CancelTree(System.Windows.Media.VisualTreeHelper.GetChild(target, index));
    }
    internal static double Velocity(DependencyObject target, DependencyProperty property)
    {
        if (!States.TryGetValue(target, out var properties) || !properties.TryGetValue(property, out var state)) return 0;
        var a = state.Animation;
        return MotionMath.Spring(a.From, a.Velocity, a.To, state.Clock.CurrentTime?.TotalSeconds ?? 0, a.Frequency, a.Damping).Velocity;
    }
    internal static void Move(DependencyObject target, DependencyProperty property, double destination, bool animated, int milliseconds, double damping = 1)
    {
        var current = (double)target.GetValue(property); var velocity = Velocity(target, property);
        var properties = States.GetOrCreateValue(target); properties.Remove(property, out var previous);
        // Set the base beneath the old clock before removing it. Do not briefly
        // publish the destination: DP callbacks can otherwise build offscreen cards.
        target.SetValue(property, double.IsFinite(current) ? current : destination);
        Apply(target, property, null); previous?.Clock.Controller?.Remove();
        if (!animated || !double.IsFinite(current) || (Math.Abs(current - destination) < .001 && Math.Abs(velocity) < .01)) { target.SetValue(property, destination); Changed?.Invoke(target); return; }
        var seconds = Math.Max(.06, milliseconds / 1000.0);
        // Keep the current base value until WPF starts the new clock on its next rendering tick.
        target.SetValue(property, current);
        var animation = new SpringAnimation { From = current, To = destination, Velocity = velocity, Frequency = 6 / seconds, Damping = damping, Duration = TimeSpan.FromSeconds(seconds * 2 / damping), FillBehavior = FillBehavior.HoldEnd };
        if (property == UIElement.OpacityProperty) { animation.Minimum = 0; animation.Maximum = 1; }
        if (property == FrameworkElement.WidthProperty || property == FrameworkElement.HeightProperty || property == System.Windows.Controls.TextBlock.FontSizeProperty) animation.Minimum = .01;
        animation.Freeze(); var clock = (AnimationClock)animation.CreateClock(true); var state = new State(animation, clock); properties[property] = state;
        clock.Completed += (_, _) => { if (properties.TryGetValue(property, out var active) && ReferenceEquals(active, state)) { properties.Remove(property); target.SetValue(property, destination); Apply(target, property, null); clock.Controller?.Remove(); } };
        Apply(target, property, clock);
        Changed?.Invoke(target);
    }
    private static void Apply(DependencyObject target, DependencyProperty property, AnimationClock? clock)
    {
        if (target is Animatable animatable) animatable.ApplyAnimationClock(property, clock);
        else if (target is UIElement element) element.ApplyAnimationClock(property, clock);
        else throw new ArgumentException("Target cannot be animated.");
    }
}
