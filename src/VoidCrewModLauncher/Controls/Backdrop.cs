using System.Windows;
using System.Windows.Media;

namespace VoidCrewModLauncher.Controls;

/// <summary>
/// The background of the game's menus: near black, a soft blue glow off-centre, a dim teal light in the top-left corner
/// (the main menu's corridor), and the faint circle-and-star sigil the settings menu shows behind its lists.
/// </summary>
public sealed class Backdrop : FrameworkElement
{
    private static readonly Brush Base = Frozen(new SolidColorBrush(Color.FromRgb(0x06, 0x09, 0x0E)));
    private static readonly Pen Sigil = FrozenPen(Color.FromArgb(0x16, 0x9F, 0xC4, 0xE8), 1.3);
    private static readonly Pen SigilFaint = FrozenPen(Color.FromArgb(0x0C, 0x9F, 0xC4, 0xE8), 1);

    protected override void OnRender(DrawingContext drawing)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var bounds = new Rect(0, 0, width, height);
        drawing.DrawRectangle(Base, null, bounds);
        drawing.DrawRectangle(Glow(Color.FromArgb(0xA0, 0x1A, 0x39, 0x5E), 0.6, 0.48, 0.62), null, bounds);
        drawing.DrawRectangle(Glow(Color.FromArgb(0x70, 0x0F, 0x33, 0x36), 0.08, 0.0, 0.45), null, bounds);

        // The sigil: three rings, an eight-pointed star and its spokes.
        var centre = new Point(width * 0.6, height * 0.5);
        double radius = Math.Min(width, height) * 0.42;
        drawing.DrawEllipse(null, Sigil, centre, radius, radius);
        drawing.DrawEllipse(null, SigilFaint, centre, radius * 0.93, radius * 0.93);
        drawing.DrawEllipse(null, Sigil, centre, radius * 0.55, radius * 0.55);
        var star = new StreamGeometry();
        using (StreamGeometryContext context = star.Open())
        {
            for (int i = 0; i < 8; i++)
            {
                Point from = OnCircle(centre, radius * 0.93, i * 45);
                Point to = OnCircle(centre, radius * 0.93, i * 45 + 135);
                context.BeginFigure(from, false, false);
                context.LineTo(to, true, false);
                context.BeginFigure(OnCircle(centre, radius * 0.55, i * 45 + 22.5), false, false);
                context.LineTo(OnCircle(centre, radius, i * 45 + 22.5), true, false);
            }
        }
        star.Freeze();
        drawing.DrawGeometry(null, SigilFaint, star);
    }

    private static Point OnCircle(Point centre, double radius, double degrees)
    {
        double angle = degrees * Math.PI / 180;
        return new Point(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));
    }

    private static Brush Glow(Color color, double x, double y, double size)
    {
        var brush = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B))
        {
            Center = new Point(x, y),
            GradientOrigin = new Point(x, y),
            RadiusX = size,
            RadiusY = size * 1.4,
        };
        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
