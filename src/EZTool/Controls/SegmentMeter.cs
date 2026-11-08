using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace EZTool.Controls;

/// <summary>
/// Segmentierte Pegelanzeige wie bei einem Mischpult: diskrete Segmente statt eines glatten Balkens.
/// Segmente oberhalb von <see cref="WarnAt"/> leuchten orange, oberhalb von <see cref="HotAt"/> rot.
/// Der Höchstwert der letzten Sekunden bleibt als einzelnes Segment stehen (Peak-Hold).
/// </summary>
public sealed class SegmentMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(SegmentMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((SegmentMeter)d).OnValue((double)e.NewValue)));
    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(SegmentMeter),
        new FrameworkPropertyMetadata(Brushes.Gold, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(nameof(Segments), typeof(int), typeof(SegmentMeter),
        new FrameworkPropertyMetadata(40, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WarnAtProperty = DependencyProperty.Register(nameof(WarnAt), typeof(double), typeof(SegmentMeter),
        new FrameworkPropertyMetadata(101d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HotAtProperty = DependencyProperty.Register(nameof(HotAt), typeof(double), typeof(SegmentMeter),
        new FrameworkPropertyMetadata(101d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public int Segments { get => (int)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double WarnAt { get => (double)GetValue(WarnAtProperty); set => SetValue(WarnAtProperty, value); }
    public double HotAt { get => (double)GetValue(HotAtProperty); set => SetValue(HotAtProperty, value); }

    private static readonly Brush Off = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x33, 0x38)));
    private static readonly Brush Warn = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x1C)));
    private static readonly Brush Hot = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0x44, 0x3A)));
    private static readonly TimeSpan PeakHold = TimeSpan.FromSeconds(3);

    private double _peak;
    private DateTime _peakAt;

    private void OnValue(double v)
    {
        var now = DateTime.UtcNow;
        if (v >= _peak || now - _peakAt > PeakHold)
        {
            _peak = v;
            _peakAt = now;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        int n = Math.Max(4, Segments);
        if (w <= 0 || h <= 0) return;

        const double gap = 3;
        double seg = (w - gap * (n - 1)) / n;
        if (seg < 1) return;

        int lit = (int)Math.Round(Math.Clamp(Value, 0, 100) / 100 * n);
        int peak = (int)Math.Round(Math.Clamp(_peak, 0, 100) / 100 * n) - 1;

        for (int i = 0; i < n; i++)
        {
            double pct = (i + 1) * 100.0 / n;
            Brush on = pct > HotAt ? Hot : pct > WarnAt ? Warn : Brush;
            bool isOn = i < lit || (i == peak && peak >= lit);
            // Segmente auf ganze Pixel legen, damit die Lücken scharf bleiben
            double x = Math.Round(i * (seg + gap));
            double x2 = Math.Round(i * (seg + gap) + seg);
            dc.DrawRectangle(isOn ? on : Off, null, new Rect(x, 0, Math.Max(1, x2 - x), h));
        }
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
