using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EZTool.Controls;

public sealed record ChartSeries(string Label, Color Color, IReadOnlyList<double?> Values, string Unit);

/// <summary>Zeitdiagramm mit mehreren Linien, Achsenbeschriftung und Fadenkreuz beim Überfahren.</summary>
public sealed class TimeChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(TimeChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TimesProperty = DependencyProperty.Register(
        nameof(Times), typeof(IReadOnlyList<DateTime>), typeof(TimeChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(TimeChart),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartSeries>? Series { get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public IReadOnlyList<DateTime>? Times { get => (IReadOnlyList<DateTime>?)GetValue(TimesProperty); set => SetValue(TimesProperty, value); }

    /// <summary>0 = automatisch skalieren.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    private static readonly Typeface Face = new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush Axis = Freeze(new SolidColorBrush(Color.FromArgb(150, 140, 157, 186)));
    private static readonly Pen Grid = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), 1) { DashStyle = new DashStyle([3, 4], 0) });
    private const double Left = 40, Bottom = 24, Top = 8, Right = 8;
    private double? _hoverX;

    public TimeChart()
    {
        MouseMove += (_, e) => { _hoverX = e.GetPosition(this).X; InvalidateVisual(); };
        MouseLeave += (_, _) => { _hoverX = null; InvalidateVisual(); };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var series = Series;
        var times = Times;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (series == null || times == null || times.Count < 2 || w < 80 || h < 60)
        {
            var empty = new FormattedText("Noch keine Daten – der Verlauf füllt sich alle 5 Sekunden.", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 13, Axis, dpi);
            dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
            return;
        }

        double max = Maximum;
        if (max <= 0)
        {
            max = 10;
            foreach (var s in series) foreach (var v in s.Values) if (v is { } x && x > max) max = x;
            max = Math.Ceiling(max * 1.1 / 10) * 10;
        }

        double plotW = w - Left - Right, plotH = h - Top - Bottom;
        var t0 = times[0];
        double span = Math.Max(1, (times[^1] - t0).TotalSeconds);
        double X(int i) => Left + (times[i] - t0).TotalSeconds / span * plotW;
        double Y(double v) => Top + plotH * (1 - Math.Clamp(v / max, 0, 1));

        // Raster + Y-Achse
        for (int i = 0; i <= 4; i++)
        {
            double v = max * i / 4, y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(Grid, new Point(Left, y), new Point(w - Right, y));
            var ft = new FormattedText(v.ToString("0"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, Axis, dpi);
            dc.DrawText(ft, new Point(Left - ft.Width - 8, y - ft.Height / 2));
        }

        // X-Achse (5 Zeitmarken)
        string fmt = span > 6 * 3600 ? "HH:mm" : span > 600 ? "HH:mm" : "HH:mm:ss";
        for (int i = 0; i <= 4; i++)
        {
            var t = t0.AddSeconds(span * i / 4);
            var ft = new FormattedText(t.ToString(fmt), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, Axis, dpi);
            double x = Left + plotW * i / 4 - ft.Width / 2;
            dc.DrawText(ft, new Point(Math.Clamp(x, Left - 10, w - ft.Width), h - Bottom + 6));
        }

        // Linien (Lücken bei fehlenden Werten)
        foreach (var s in series)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool open = false;
                for (int i = 0; i < s.Values.Count && i < times.Count; i++)
                {
                    if (s.Values[i] is not { } v) { open = false; continue; }
                    var p = new Point(X(i), Y(v));
                    if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                    else ctx.LineTo(p, true, true);
                }
            }
            geo.Freeze();
            var c = s.Color;
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B)), 6) { LineJoin = PenLineJoin.Round }, geo);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(c), 1.8) { LineJoin = PenLineJoin.Round }, geo);
        }

        // Fadenkreuz
        if (_hoverX is { } hx && hx >= Left && hx <= w - Right)
        {
            int idx = 0;
            double best = double.MaxValue;
            for (int i = 0; i < times.Count; i++)
            {
                double d = Math.Abs(X(i) - hx);
                if (d < best) { best = d; idx = i; }
            }
            double x = X(idx);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1), new Point(x, Top), new Point(x, Top + plotH));

            var lines = new List<(string, Brush)> { (times[idx].ToString("dd.MM. HH:mm:ss"), Brushes.White) };
            foreach (var s in series)
            {
                if (idx >= s.Values.Count || s.Values[idx] is not { } v) continue;
                dc.DrawEllipse(new SolidColorBrush(s.Color), new Pen(Brushes.White, 1.5), new Point(x, Y(v)), 4, 4);
                lines.Add(($"{s.Label}: {v:0.#} {s.Unit}", new SolidColorBrush(s.Color)));
            }

            var texts = lines.Select(l => new FormattedText(l.Item1, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12, l.Item2, dpi)).ToList();
            double bw = texts.Max(t => t.Width) + 20, bh = texts.Sum(t => t.Height + 2) + 14;
            double bx = x + 12 + bw > w ? x - bw - 12 : x + 12, by = Top + 6;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 13, 20, 36)), new Pen(new SolidColorBrush(Color.FromArgb(80, 0, 229, 255)), 1),
                new Rect(bx, by, bw, bh), 8, 8);
            double ty = by + 7;
            foreach (var t in texts)
            {
                dc.DrawText(t, new Point(bx + 10, ty));
                ty += t.Height + 2;
            }
        }
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
    private static Pen FreezePen(Pen p) { p.Freeze(); return p; }
}
