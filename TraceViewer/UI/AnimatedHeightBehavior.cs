using System.Windows;
using System.Windows.Media.Animation;

namespace TraceViewer.UI;

public static class AnimatedHeightBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(AnimatedHeightBehavior),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.RegisterAttached(
            "Value",
            typeof(double),
            typeof(AnimatedHeightBehavior),
            new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty DurationMillisecondsProperty =
        DependencyProperty.RegisterAttached(
            "DurationMilliseconds",
            typeof(double),
            typeof(AnimatedHeightBehavior),
            new PropertyMetadata(120.0));

    public static bool GetIsEnabled(DependencyObject dependencyObject)
    {
        return (bool)dependencyObject.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject dependencyObject, bool value)
    {
        dependencyObject.SetValue(IsEnabledProperty, value);
    }

    public static double GetValue(DependencyObject dependencyObject)
    {
        return (double)dependencyObject.GetValue(ValueProperty);
    }

    public static void SetValue(DependencyObject dependencyObject, double value)
    {
        dependencyObject.SetValue(ValueProperty, value);
    }

    public static double GetDurationMilliseconds(DependencyObject dependencyObject)
    {
        return (double)dependencyObject.GetValue(DurationMillisecondsProperty);
    }

    public static void SetDurationMilliseconds(DependencyObject dependencyObject, double value)
    {
        dependencyObject.SetValue(DurationMillisecondsProperty, value);
    }

    private static void OnValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not FrameworkElement element ||
            eventArgs.NewValue is not double targetHeight ||
            !double.IsFinite(targetHeight))
        {
            return;
        }

        if (!GetIsEnabled(element) || !element.IsLoaded)
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.Height = targetHeight;
            return;
        }

        AnimateHeight(element, targetHeight);
    }

    private static void AnimateHeight(FrameworkElement element, double targetHeight)
    {
        var currentHeight = element.ActualHeight;
        if (currentHeight <= 0.0 || !double.IsFinite(currentHeight))
        {
            currentHeight = double.IsNaN(element.Height) ? targetHeight : element.Height;
        }

        if (!double.IsFinite(currentHeight) || Math.Abs(currentHeight - targetHeight) < 0.1)
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.Height = targetHeight;
            return;
        }

        var animation = new DoubleAnimation
        {
            From = currentHeight,
            To = targetHeight,
            Duration = TimeSpan.FromMilliseconds(GetDurationMilliseconds(element)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };

        animation.Completed += (_, _) =>
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.Height = targetHeight;
        };

        element.BeginAnimation(FrameworkElement.HeightProperty, animation);
    }
}
