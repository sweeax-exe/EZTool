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
        new FrameworkPropertyMetadata(Color.FromRgb(0, 229, 255), FrameworkPropertyMetadataOptions.AffectsRender));

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

        // Tick-Ring
        var tickOn = new Pen(new SolidColorBrush(Color.FromArgb(200, GlowColor.R, GlowColor.G, GlowColor.B)), 1.6);
        var tickOff = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1.2);
        tickOn.Freeze();
        tickOff.Freeze();
        for (int i = 0; i <= TickCount; i++)
        {
            double t = (double)i / TickCount;
            double a = StartAngle + Sweep * t;
            bool major = i % 5 == 0;
            double r1 = outer - 2;
            double r2 = outer - 2 - (major ? tickLen : tickLen * 0.55);
            dc.DrawLine(t <= frac ? tickOn : tickOff, Polar(center, r1, a), Polar(center, r2, a));
        }

        // Track
        var track = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        track.Freeze();
        dc.DrawGeometry(null, track, Arc(center, radius, StartAngle, Sweep));

        // Innerer dünner Ring
        var inner = new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1);
        inner.Freeze();
        dc.DrawEllipse(null, inner, center, radius - Thickness - 6, radius - Thickness - 6);

        if (frac <= 0.002) return;
        double sweep = Sweep * frac;
        var arc = Arc(center, radius, StartAngle, sweep);

        // Glow (mehrere breite, transparente Schichten)
        for (int i = 3; i >= 1; i--)
        {
            var glow = new Pen(new SolidColorBrush(Color.FromArgb((byte)(14 * (4 - i)), GlowColor.R, GlowColor.G, GlowColor.B)),
                Thickness + i * 7)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawGeometry(null, glow, arc);
        }

        var pen = new Pen(ArcBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, arc);

        // Leuchtender Endpunkt
        var tip = Polar(center, radius, StartAngle + sweep);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(60, GlowColor.R, GlowColor.G, GlowColor.B)), null, tip, Thickness * 0.95, Thickness * 0.95);
        dc.DrawEllipse(Brushes.White, null, tip, Thickness * 0.28, Thickness * 0.28);
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
