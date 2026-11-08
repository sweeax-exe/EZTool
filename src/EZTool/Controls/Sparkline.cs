using System.Windows;
using System.Windows.Media;

namespace EZTool.Controls;

/// <summary>Weicher Verlaufsgraph (Catmull-Rom) mit Flächenfüllung und Rasterlinien.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0xC2, 0x0E), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    /// <summary>0 = automatisch skalieren.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public Color Stroke { get => (Color)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        if (ShowGrid)
        {
            // Raster wie auf einem Oszilloskop: 10 × 4 Teilungen
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)), 1);
            grid.Freeze();
            for (int i = 0; i <= 4; i++)
            {
                double y = Math.Min(h - 0.5, Math.Round(h * i / 4) + 0.5);
                dc.DrawLine(grid, new Point(0, y), new Point(w, y));
            }
            for (int i = 0; i <= 10; i++)
            {
                double x = Math.Min(w - 0.5, Math.Round(w * i / 10) + 0.5);
                dc.DrawLine(grid, new Point(x, 0), new Point(x, h));
            }
        }

        var vals = Values;
        if (vals is null || vals.Count < 2) return;

        double max = Maximum;
        if (max <= 0)
        {
            max = 1;
            foreach (var v in vals) max = Math.Max(max, v);
            max *= 1.15;
        }

        int cap = Math.Max(Capacity, vals.Count);
        double step = w / (cap - 1);
        double x0 = w - step * (vals.Count - 1);
        const double pad = 3;

        var pts = new Point[vals.Count];
        for (int i = 0; i < vals.Count; i++)
        {
            double f = Math.Clamp(vals[i] / max, 0, 1);
            pts[i] = new Point(x0 + i * step, pad + (h - 2 * pad) * (1 - f));
        }

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            l.BeginFigure(pts[0], false, false);
            a.BeginFigure(new Point(pts[0].X, h), true, true);
            a.LineTo(pts[0], false, false);
            for (int i = 0; i < pts.Length - 1; i++)
            {
                var p0 = pts[Math.Max(i - 1, 0)];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = pts[Math.Min(i + 2, pts.Length - 1)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                c1.Y = Math.Clamp(c1.Y, pad, h - pad);
                c2.Y = Math.Clamp(c2.Y, pad, h - pad);
                l.BezierTo(c1, c2, p2, true, true);
                a.BezierTo(c1, c2, p2, false, false);
            }
            a.LineTo(new Point(pts[^1].X, h), false, false);
        }
        line.Freeze();
        area.Freeze();

        var c = Stroke;
        var fill = new SolidColorBrush(Color.FromArgb(16, c.R, c.G, c.B));
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);

        var pen = new Pen(new SolidColorBrush(c), 1.6) { LineJoin = PenLineJoin.Round };
        dc.DrawGeometry(null, pen, line);

        var last = pts[^1];
        // aktueller Messpunkt als kleines Quadrat
        dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(last.X - 3, last.Y - 3, 6, 6));
    }
}
