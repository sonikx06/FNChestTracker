using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ChestTracker.App.Controls;

/// <summary>Attached behavior: briefly scales an element up when the bound value changes.</summary>
public static class Pulse
{
    public static readonly DependencyProperty OnChangeProperty = DependencyProperty.RegisterAttached(
        "OnChange", typeof(object), typeof(Pulse), new PropertyMetadata(null, OnChanged));

    public static object? GetOnChange(DependencyObject d) => d.GetValue(OnChangeProperty);

    public static void SetOnChange(DependencyObject d, object? value) => d.SetValue(OnChangeProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || e.OldValue is null || Equals(e.OldValue, e.NewValue))
        {
            return;
        }

        if (element.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(1, 1);
            element.RenderTransform = scale;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        var animation = new DoubleAnimation(1.18, 1, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
