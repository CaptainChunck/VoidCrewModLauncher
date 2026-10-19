using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoidCrewModLauncher.Controls;

/// <summary>The outlines Void Crew's interface is built from.</summary>
public enum BorderShape
{
    Rectangle,

    /// <summary>Pointed left and right ends, like the game's buttons (metem-outline).</summary>
    Hex,

    /// <summary>Top-right and bottom-left corners cut, like the game's pop-up windows.</summary>
    ChamferPopup,

    /// <summary>Top-left and bottom-right corners cut, like the loadout terminal's ship tiles.</summary>
    ChamferTile,

    /// <summary>Only the bottom-right corner cut.</summary>
    ChamferCorner,

    /// <summary>Both bottom corners cut, like the main menu's panel and its news cards.</summary>
    ChamferBottom,

    /// <summary>Both top corners cut and an open bottom, like the settings menu's tabs.</summary>
    ChamferTop,

    /// <summary>A square stood on its point, like the matchmaking filters' check boxes.</summary>
    Diamond,
}

/// <summary>
/// A Border that draws one of the game's shapes with a fill and an outline, sized to its content. The cut stays the
/// same size whatever the width, so wide and narrow buttons have the same pointed ends.
/// </summary>
public sealed class ShapeBorder : Decorator
{
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(BorderShape), typeof(ShapeBorder), new FrameworkPropertyMetadata(BorderShape.Hex, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(ShapeBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(ShapeBorder), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(double), typeof(ShapeBorder), new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Size of the cut corners or pointed ends; NaN picks the game's proportions (half the height for Hex).</summary>
    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(
        nameof(Cut), typeof(double), typeof(ShapeBorder), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(ShapeBorder), new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public BorderShape Shape
    {
        get => (BorderShape)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush? BorderBrush
    {
        get => (Brush?)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public double BorderThickness
    {
        get => (double)GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public double Cut
    {
        get => (double)GetValue(CutProperty);
        set => SetValue(CutProperty, value);
    }

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    private double CutFor(Size size)
    {
        if (!double.IsNaN(Cut)) return Cut;
        // The main menu's buttons point less steeply than 45 degrees.
        return Shape == BorderShape.Hex ? size.Height * 0.4 : Math.Min(14, Math.Min(size.Width, size.Height) / 3);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        Thickness padding = Padding;
        double horizontal = padding.Left + padding.Right;
        double vertical = padding.Top + padding.Bottom;
        if (Child == null) return new Size(horizontal, vertical);
        Child.Measure(new Size(Math.Max(0, constraint.Width - horizontal), Math.Max(0, constraint.Height - vertical)));
        Size desired = Child.DesiredSize;
        return new Size(desired.Width + horizontal, desired.Height + vertical);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Thickness padding = Padding;
        Child?.Arrange(new Rect(padding.Left, padding.Top,
            Math.Max(0, arrangeSize.Width - padding.Left - padding.Right),
            Math.Max(0, arrangeSize.Height - padding.Top - padding.Bottom)));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext drawing)
    {
        Size size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        Brush? stroke = BorderBrush;
        double thickness = stroke == null ? 0 : BorderThickness;
        Geometry geometry = Build(Shape, size, CutFor(size), thickness / 2);
        drawing.DrawGeometry(Background, stroke == null ? null : new Pen(stroke, thickness) { LineJoin = PenLineJoin.Miter, MiterLimit = 4 }, geometry);
    }

    /// <summary>The outline inside <paramref name="size"/>, inset by <paramref name="inset"/> so the stroke isn't clipped.</summary>
    public static Geometry Build(BorderShape shape, Size size, double cut, double inset)
    {
        double left = inset, top = inset, right = size.Width - inset, bottom = size.Height - inset;
        double middle = size.Height / 2;
        cut = Math.Max(0, Math.Min(cut, Math.Min(size.Width / 2, size.Height)));
        Point[] points = shape switch
        {
            BorderShape.Hex => new[]
            {
                new Point(left + cut, top), new Point(right - cut, top), new Point(right, middle),
                new Point(right - cut, bottom), new Point(left + cut, bottom), new Point(left, middle),
            },
            BorderShape.ChamferPopup => new[]
            {
                new Point(left, top), new Point(right - cut, top), new Point(right, top + cut),
                new Point(right, bottom), new Point(left + cut, bottom), new Point(left, bottom - cut),
            },
            BorderShape.ChamferTile => new[]
            {
                new Point(left + cut, top), new Point(right, top), new Point(right, bottom - cut),
                new Point(right - cut, bottom), new Point(left, bottom), new Point(left, top + cut),
            },
            BorderShape.ChamferCorner => new[]
            {
                new Point(left, top), new Point(right, top), new Point(right, bottom - cut),
                new Point(right - cut, bottom), new Point(left, bottom),
            },
            BorderShape.ChamferBottom => new[]
            {
                new Point(left, top), new Point(right, top), new Point(right, bottom - cut),
                new Point(right - cut, bottom), new Point(left + cut, bottom), new Point(left, bottom - cut),
            },
            BorderShape.ChamferTop => new[]
            {
                new Point(left, bottom), new Point(left, top + cut), new Point(left + cut, top),
                new Point(right - cut, top), new Point(right, top + cut), new Point(right, bottom),
            },
            BorderShape.Diamond => new[]
            {
                new Point((left + right) / 2, top), new Point(right, (top + bottom) / 2),
                new Point((left + right) / 2, bottom), new Point(left, (top + bottom) / 2),
            },
            _ => new[] { new Point(left, top), new Point(right, top), new Point(right, bottom), new Point(left, bottom) },
        };

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            context.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        geometry.Freeze();
        return geometry;
    }
}
