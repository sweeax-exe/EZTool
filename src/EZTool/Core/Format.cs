using System.Globalization;
using System.Windows.Data;

namespace EZTool.Core;

public static class Format
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes) => Bytes((double)bytes);

    public static string Bytes(double bytes)
    {
        if (bytes < 0) bytes = 0;
        int u = 0;
        while (bytes >= 1024 && u < Units.Length - 1)
        {
            bytes /= 1024;
            u++;
        }
        string fmt = u == 0 ? "0" : bytes >= 100 ? "0" : bytes >= 10 ? "0.0" : "0.00";
        return bytes.ToString(fmt, De) + " " + Units[u];
    }

    public static string Speed(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string Number(double value, int decimals = 0) => value.ToString("N" + decimals, De);

    public static string Duration(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} T {t.Hours} Std";
        if (t.TotalHours >= 1) return $"{t.Hours} Std {t.Minutes} Min";
        return $"{t.Minutes} Min";
    }

    public static string Age(DateTime? date)
    {
        if (date is null || date == DateTime.MinValue) return "—";
        var days = (DateTime.Now - date.Value).TotalDays;
        if (days < 1) return "heute";
        if (days < 2) return "gestern";
        if (days < 60) return $"vor {(int)days} Tagen";
        if (days < 730) return $"vor {(int)(days / 30)} Monaten";
        return $"vor {(int)(days / 365)} Jahren";
    }
}

public sealed class BytesConverter : IValueConverter
{
    /// <summary>ConverterParameter="dash" zeigt "—" statt "0 B" (z. B. bei unbekannter Größe).</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        long z when z == 0 && parameter as string == "dash" => "—",
        long l => Format.Bytes(l),
        double d => Format.Bytes(d),
        int i => Format.Bytes(i),
        ulong u => Format.Bytes((double)u),
        _ => "—"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool x => x,
            null => false,
            string s => !string.IsNullOrEmpty(s),
            int i => i != 0,
            _ => true
        };
        if (Invert) b = !b;
        return b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True, wenn der Wert (als Text) dem ConverterParameter entspricht – für Segment-Schalter.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class AgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Format.Age(value as DateTime?);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Wandelt einen Text in einen deterministischen Farbverlauf um (für Platzhalter-Icons).</summary>
public sealed class NameToBrushConverter : IValueConverter
{
    private static readonly (string, string)[] Pairs =
    [
        ("#00E5FF", "#8B5CF6"), ("#8B5CF6", "#FF3D9A"), ("#2CF5A0", "#00E5FF"),
        ("#FFB547", "#FF3D9A"), ("#00E5FF", "#2CF5A0"), ("#FF3D9A", "#FFB547")
    ];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        int h = 0;
        foreach (var c in s) h = (h * 31 + c) & 0x7FFFFFFF;
        var (a, b) = Pairs[h % Pairs.Length];
        var brush = new System.Windows.Media.LinearGradientBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(a),
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(b), 45);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InitialConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = (value as string ?? "").Trim();
        return s.Length > 0 ? char.ToUpperInvariant(s[0]).ToString() : "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
