using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace EZTool.Controls;

/// <summary>Messkanal: Name, großer Wert, Segmentanzeige und zwei Kennzahlen. ArcBrush ist die Kanalfarbe.</summary>
public class GaugeCard : Control
{

    public static readonly DependencyProperty TitleProperty = Reg<string>(nameof(Title), "");
    public static readonly DependencyProperty IconProperty = Reg<string>(nameof(Icon), "");
    public static readonly DependencyProperty ValueProperty = Reg<double>(nameof(Value), 0d);
    public static readonly DependencyProperty ValueTextProperty = Reg<string>(nameof(ValueText), "—");
    public static readonly DependencyProperty UnitProperty = Reg<string>(nameof(Unit), "%");
    public static readonly DependencyProperty SubTextProperty = Reg<string>(nameof(SubText), "");
    public static readonly DependencyProperty CaptionProperty = Reg<string>(nameof(Caption), "");
    public static readonly DependencyProperty ArcBrushProperty = Reg<Brush>(nameof(ArcBrush), Brushes.Gold);
    public static readonly DependencyProperty GlowColorProperty = Reg<Color>(nameof(GlowColor), Color.FromRgb(0xFF, 0xC2, 0x0E));
    public static readonly DependencyProperty SubBrushProperty = Reg<Brush?>(nameof(SubBrush), null);
    public static readonly DependencyProperty Stat1LabelProperty = Reg<string>(nameof(Stat1Label), "");
    public static readonly DependencyProperty Stat1ValueProperty = Reg<string>(nameof(Stat1Value), "");
    public static readonly DependencyProperty Stat2LabelProperty = Reg<string>(nameof(Stat2Label), "");
    public static readonly DependencyProperty Stat2ValueProperty = Reg<string>(nameof(Stat2Value), "");

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string SubText { get => (string)GetValue(SubTextProperty); set => SetValue(SubTextProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public Brush ArcBrush { get => (Brush)GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }
    public Color GlowColor { get => (Color)GetValue(GlowColorProperty); set => SetValue(GlowColorProperty, value); }
    public Brush? SubBrush { get => (Brush?)GetValue(SubBrushProperty); set => SetValue(SubBrushProperty, value); }
    public string Stat1Label { get => (string)GetValue(Stat1LabelProperty); set => SetValue(Stat1LabelProperty, value); }
    public string Stat1Value { get => (string)GetValue(Stat1ValueProperty); set => SetValue(Stat1ValueProperty, value); }
    public string Stat2Label { get => (string)GetValue(Stat2LabelProperty); set => SetValue(Stat2LabelProperty, value); }
    public string Stat2Value { get => (string)GetValue(Stat2ValueProperty); set => SetValue(Stat2ValueProperty, value); }

    private static DependencyProperty Reg<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(GaugeCard), new PropertyMetadata(def));
}

/// <summary>Temperatur → Farbe (kühl = blau, ok = grün, warm = amber, heiß = rot).</summary>
public sealed class TempToBrushConverter : IValueConverter
{
    private static readonly Brush Cool = Freeze(new SolidColorBrush(Color.FromRgb(0x4D, 0xA8, 0xE8)));
    private static readonly Brush Ok = Freeze(new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x6A)));
    private static readonly Brush Warm = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x1C)));
    private static readonly Brush Hot = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0x44, 0x3A)));
    private static readonly Brush None = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0x75, 0x7C)));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double t || t <= 0) return None; // kein Messwert
        return t < 50 ? Cool : t < 70 ? Ok : t < 85 ? Warm : Hot;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>Prozentwert → Pixelhöhe (Parameter = maximale Höhe).</summary>
public sealed class PercentToLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double max = double.TryParse(parameter?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 100;
        double v = value is double d ? d : 0;
        return Math.Max(2, Math.Clamp(v, 0, 100) / 100 * max);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

