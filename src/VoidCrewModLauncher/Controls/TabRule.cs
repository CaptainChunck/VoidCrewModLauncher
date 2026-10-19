using System.Windows;
using System.Windows.Media;

namespace VoidCrewModLauncher.Controls;

/// <summary>
/// The line the settings menu's tabs stand on: straight across, bending down at both ends.
/// </summary>
public sealed class TabRule : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(TabRule), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        double drop = Math.Min(height, 22);
        double y = 0.6;
        var pen = new Pen(Stroke, 1.2);
        pen.Freeze();
        var line = new StreamGeometry();
        using (StreamGeometryContext context = line.Open())
        {
            context.BeginFigure(new Point(0, y + drop), false, false);
            context.PolyLineTo(new[] { new Point(drop, y), new Point(width - drop, y), new Point(width, y + drop) }, true, false);
        }
        line.Freeze();
        drawing.DrawGeometry(null, pen, line);
    }
}
