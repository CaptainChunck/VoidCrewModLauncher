using System.Windows;
using System.Windows.Media;

namespace VoidCrewModLauncher.Controls;

/// <summary>Attached properties the theme's templates read, so one template serves several button looks.</summary>
public static class Ui
{
    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(Brushes.White));

    public static Brush GetHoverBackground(DependencyObject element) => (Brush)element.GetValue(HoverBackgroundProperty);

    public static void SetHoverBackground(DependencyObject element, Brush value) => element.SetValue(HoverBackgroundProperty, value);
}
