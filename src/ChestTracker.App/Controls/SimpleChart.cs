using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ChestTracker.App.Controls;

public sealed record ChartPoint(string Label, double Value, string? Tooltip = null);

public enum ChartKind
{
    Bar,
    Line,
}

/// <summary>
/// Lightweight bar/line chart drawn directly in OnRender (no chart library, no per-point visuals).
/// Hovering shows the value of the nearest point.
/// </summary>
public sealed class SimpleChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IList), typeof(SimpleChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryPointsProperty = DependencyProperty.Register(
        nameof(SecondaryPoints), typeof(IList), typeof(SimpleChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(ChartKind), typeof(SimpleChart),
        new FrameworkPropertyMetadata(ChartKind.Bar, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(SimpleChart),
        new FrameworkPropertyMetadata(Color.FromRgb(0xA8, 0x55, 0xF7), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(SimpleChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat), typeof(string), typeof(SimpleChart),
        new FrameworkPropertyMetadata("0.##", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FixedMaximumProperty = DependencyProperty.Register(
        nameof(FixedMaximum), typeof(double), typeof(SimpleChart),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface TipFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8C, 0x82, 0xA8)));
    private static readonly Pen GridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))), 1));
    private static readonly Brush TipBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x1B, 0x37)));
    private static readonly Pen TipBorder = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x25, 0x49))), 1));

    private int _hover = -1;

    public SimpleChart()
    {
        MinHeight = 120;
        ClipToBounds = true;
    }

    public IList? Points
    {
        get => (IList?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>Optional second line series (drawn dashed, same scale).</summary>
    public IList? SecondaryPoints
    {
        get => (IList?)GetValue(SecondaryPointsProperty);
        set => SetValue(SecondaryPointsProperty, value);
    }

    public ChartKind Kind
    {
        get => (ChartKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public string ValueFormat
    {
        get => (string)GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    public double FixedMaximum
    {
        get => (double)GetValue(FixedMaximumProperty);
        set => SetValue(FixedMaximumProperty, value);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var points = Materialize(Points);
        if (points.Count == 0)
        {
            return;
        }

        var plot = PlotArea();
        var x = e.GetPosition(this).X;
        var index = (int)Math.Floor((x - plot.Left) / (plot.Width / points.Count));
        index = Math.Clamp(index, 0, points.Count - 1);
        if (index != _hover)
        {
            _hover = index;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var points = Materialize(Points);
        var secondary = Materialize(SecondaryPoints);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (points.Count == 0)
        {
            var empty = Text(EmptyText, 12.5, LabelBrush, LabelFace, dpi);
            dc.DrawText(empty, new Point((ActualWidth - empty.Width) / 2, (ActualHeight - empty.Height) / 2));
            return;
        }

        var plot = PlotArea();
        if (plot.Width <= 4 || plot.Height <= 4)
        {
            return;
        }

        var dataMax = Math.Max(points.Max(p => p.Value), secondary.Count > 0 ? secondary.Max(p => p.Value) : 0);
        var max = double.IsNaN(FixedMaximum) ? NiceMax(dataMax) : FixedMaximum;

        // Grid and Y labels.
        const int lines = 4;
        for (var i = 0; i <= lines; i++)
        {
            var value = max * i / lines;
            var y = plot.Bottom - (plot.Height * i / lines);
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(value.ToString("0.#", CultureInfo.CurrentCulture), 10.5, LabelBrush, LabelFace, dpi);
            dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - (label.Height / 2)));
        }

        var accent = new SolidColorBrush(Accent);
        accent.Freeze();
        var slot = plot.Width / points.Count;

        if (Kind == ChartKind.Bar)
        {
            var gradient = new LinearGradientBrush(Lighten(Accent, 0.25), Color.FromArgb(0xB0, Accent.R, Accent.G, Accent.B), 90);
            gradient.Freeze();
            var barWidth = Math.Max(2, Math.Min(36, slot * 0.62));
            for (var i = 0; i < points.Count; i++)
            {
                var h = max <= 0 ? 0 : plot.Height * (points[i].Value / max);
                var x = plot.Left + (slot * i) + ((slot - barWidth) / 2);
                var rect = new Rect(x, plot.Bottom - h, barWidth, Math.Max(0, h));
                var radius = Math.Min(5, barWidth / 2);
                dc.DrawRoundedRectangle(i == _hover ? accent : gradient, null, rect, radius, radius);
            }
        }
        else
        {
            DrawLine(dc, points, plot, max, slot, accent, dashed: false, fill: true);
        }

        if (secondary.Count == points.Count && secondary.Count > 0)
        {
            var gold = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            gold.Freeze();
            DrawLine(dc, secondary, plot, max, slot, gold, dashed: true, fill: false);
        }

        // X labels, thinned to avoid overlap.
        var step = Math.Max(1, (int)Math.Ceiling(points.Count / Math.Max(1, plot.Width / 56)));
        for (var i = 0; i < points.Count; i += step)
        {
            var label = Text(points[i].Label, 10.5, LabelBrush, LabelFace, dpi);
            var cx = plot.Left + (slot * i) + (slot / 2);
            dc.DrawText(label, new Point(cx - (label.Width / 2), plot.Bottom + 6));
        }

        if (_hover >= 0 && _hover < points.Count)
        {
            var p = points[_hover];
            var text = p.Tooltip ?? $"{p.Label}: {p.Value.ToString(ValueFormat, CultureInfo.CurrentCulture)}";
            if (secondary.Count == points.Count)
            {
                text += $"  ·  {secondary[_hover].Value.ToString(ValueFormat, CultureInfo.CurrentCulture)}";
            }

            var tip = Text(text, 12, Brushes.White, TipFace, dpi);
            var cx = plot.Left + (slot * _hover) + (slot / 2);
            var w = tip.Width + 16;
            var tx = Math.Clamp(cx - (w / 2), 0, Math.Max(0, ActualWidth - w));
            var box = new Rect(tx, 2, w, tip.Height + 8);
            dc.DrawLine(new Pen(accent, 1) { DashStyle = DashStyles.Dot }, new Point(cx, box.Bottom), new Point(cx, plot.Bottom));
            dc.DrawRoundedRectangle(TipBackground, TipBorder, box, 6, 6);
            dc.DrawText(tip, new Point(tx + 8, 6));
        }
    }

    private static void DrawLine(DrawingContext dc, IReadOnlyList<ChartPoint> points, Rect plot, double max, double slot,
        Brush brush, bool dashed, bool fill)
    {
        if (points.Count == 0)
        {
            return;
        }

        Point At(int i) => new(plot.Left + (slot * i) + (slot / 2),
            plot.Bottom - (max <= 0 ? 0 : plot.Height * (points[i].Value / max)));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(0), false, false);
            for (var i = 1; i < points.Count; i++)
            {
                ctx.LineTo(At(i), true, true);
            }
        }

        geometry.Freeze();

        if (fill && points.Count > 1 && brush is SolidColorBrush solid)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(At(0).X, plot.Bottom), true, true);
                for (var i = 0; i < points.Count; i++)
                {
                    ctx.LineTo(At(i), true, true);
                }

                ctx.LineTo(new Point(At(points.Count - 1).X, plot.Bottom), true, true);
            }

            area.Freeze();
            var c = solid.Color;
            var areaBrush = new LinearGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Color.FromArgb(0x00, c.R, c.G, c.B), 90);
            areaBrush.Freeze();
            dc.DrawGeometry(areaBrush, null, area);
        }

        var pen = new Pen(brush, 2.2) { LineJoin = PenLineJoin.Round };
        if (dashed)
        {
            pen.DashStyle = new DashStyle([3, 2], 0);
        }

        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
        if (points.Count <= 40 && !dashed)
        {
            for (var i = 0; i < points.Count; i++)
            {
                dc.DrawEllipse(brush, null, At(i), 2.6, 2.6);
            }
        }
    }

    private Rect PlotArea()
    {
        const double left = 34, top = 28, right = 8, bottom = 24;
        return new Rect(left, top, Math.Max(0, ActualWidth - left - right), Math.Max(0, ActualHeight - top - bottom));
    }

    private static IReadOnlyList<ChartPoint> Materialize(IList? list) =>
        list is null ? [] : list.OfType<ChartPoint>().ToList();

    private static double NiceMax(double value)
    {
        if (value <= 0)
        {
            return 4;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var f in new[] { 1, 2, 2.5, 4, 5, 10 })
        {
            if (f * magnitude >= value)
            {
                return Math.Max(4, f * magnitude);
            }
        }

        return Math.Max(4, 10 * magnitude);
    }

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + ((255 - c.R) * amount)), (byte)(c.G + ((255 - c.G) * amount)), (byte)(c.B + ((255 - c.B) * amount)));

    private static FormattedText Text(string s, double size, Brush brush, Typeface face, double dpi) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, dpi);

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
