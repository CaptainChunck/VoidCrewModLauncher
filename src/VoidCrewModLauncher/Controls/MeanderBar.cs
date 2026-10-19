using System.Windows;
using System.Windows.Media;

namespace VoidCrewModLauncher.Controls;

/// <summary>
/// The Greek key band Void Crew runs along the top and bottom of its terminals (metem_deco_meander_1), drawn as lines
/// so it stays sharp at any size: a top and bottom rule with a hooked key every few units between them.
/// </summary>
public sealed class MeanderBar : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(MeanderBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(MeanderBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Takes no width of its own: it fills whatever its parent gives it (so it never widens a pop-up).</summary>
    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsNaN(Height) ? 12 : Height);

    protected override void OnRender(DrawingContext drawing)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 2) return;
        if (Background != null) drawing.DrawRectangle(Background, null, new Rect(0, 0, width, height));

        double unit = height / 6.0;
        double thickness = Math.Max(1, unit * 0.9);
        var pen = new Pen(Stroke, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Miter };
        pen.Freeze();
        double top = thickness / 2;
        double bottom = height - thickness / 2;
        double cell = unit * 7;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(0, top), false, false);
            context.LineTo(new Point(width, top), true, false);
            context.BeginFigure(new Point(0, bottom), false, false);
            context.LineTo(new Point(width, bottom), true, false);
            for (double x = unit; x + cell - unit * 2 <= width; x += cell)
            {
                // A square spiral hanging from the top rule, opening towards the next key.
                context.BeginFigure(new Point(x, top), false, false);
                context.PolyLineTo(new[]
                {
                    new Point(x, bottom - unit * 1.2),
                    new Point(x + unit * 4, bottom - unit * 1.2),
                    new Point(x + unit * 4, top + unit * 1.6),
                    new Point(x + unit * 2, top + unit * 1.6),
                    new Point(x + unit * 2, top + unit * 3),
                }, true, false);
                context.BeginFigure(new Point(x + unit * 5.5, bottom), false, false);
                context.LineTo(new Point(x + unit * 5.5, top + unit * 1.6), true, false);
            }
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, pen, geometry);
    }
}
