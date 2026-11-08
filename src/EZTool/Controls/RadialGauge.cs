using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EZTool.Controls;

/// <summary>Futuristischer 270°-Ringgauge mit animiertem Wert, Glow und Tick-Ring.</summary>
public sealed class RadialGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RadialGauge), new PropertyMetadata(0d, OnValueChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        nameof(ArcBrush), typeof(Brush), typeof(RadialGauge),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowColorProperty = DependencyProperty.Register(
        nameof(GlowColor), typeof(Color), typeof(RadialGauge),
        new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0xC2, 0x0E), FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        "DisplayValue", typeof(double), typeof(RadialGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush ArcBrush { get => (Brush)GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }
    public Color GlowColor { get => (Color)GetValue(GlowColorProperty); set => SetValue(GlowColorProperty, value); }

    private const double StartAngle = 135, Sweep = 270;
    private const int TickCount = 60;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var g = (RadialGauge)d;
        var target = double.IsNaN((double)e.NewValue) ? 0 : (double)e.NewValue;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        g.BeginAnimation(DisplayValueProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var center = new Point(w / 2, h / 2);
        double outer = Math.Min(w, h) / 2;
        double tickLen = Math.Max(4, outer * 0.07);
        double radius = outer - tickLen - Thickness / 2 - 6;
        if (radius <= 4) return;

        double max = Maximum <= 0 ? 100 : Maximum;
        double frac = Math.Clamp((double)GetValue(DisplayValueProperty) / max, 0, 1);

        // Segmentring statt glühendem Bogen: einzelne radiale Segmente, beleuchtet bis zum Wert
        const int segments = 36;
        var off = new SolidColorBrush(Color.FromRgb(0x2E, 0x33, 0x38));
        off.Freeze();
        double segSweep = Sweep / segments;
        double gapDeg = Math.Min(segSweep * 0.35, 3.0);
        int lit = (int)Math.Round(frac * segments);
        var onPen = new Pen(ArcBrush, Thickness);
        var offPen = new Pen(off, Thickness);
        offPen.Freeze();
        for (int i = 0; i < segments; i++)
        {
            double a = StartAngle + i * segSweep + gapDeg / 2;
            dc.DrawGeometry(null, i < lit ? onPen : offPen, Arc(center, radius, a, segSweep - gapDeg));
        }

        // Feine Skala außen: alle 10 %
        var scale = new Pen(new SolidColorBrush(Color.FromRgb(0x6E, 0x75, 0x7C)), 1);
        scale.Freeze();
        for (int i = 0; i <= 10; i++)
        {
            double a = StartAngle + Sweep * i / 10;
            dc.DrawLine(scale, Polar(center, outer - 2, a), Polar(center, outer - 2 - tickLen, a));
        }
    }

    private static Point Polar(Point c, double r, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private static Geometry Arc(Point c, double r, double start, double sweep)
    {
        sweep = Math.Min(sweep, 359.99);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(Polar(c, r, start), false, false);
            ctx.ArcTo(Polar(c, r, start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
