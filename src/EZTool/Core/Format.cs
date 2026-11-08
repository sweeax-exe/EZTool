using System.Globalization;
using System.Windows.Data;

namespace EZTool.Core;

public static class Format
{
    private static CultureInfo De => Loc.Culture;
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
        if (t.TotalDays >= 1) return Loc.T("Format.DurationDays", (int)t.TotalDays, t.Hours);
        if (t.TotalHours >= 1) return Loc.T("Format.DurationHours", t.Hours, t.Minutes);
        return Loc.T("Format.DurationMinutes", t.Minutes);
    }

    public static string Age(DateTime? date)
    {
        if (date is null || date == DateTime.MinValue) return "—";
        var days = (DateTime.Now - date.Value).TotalDays;
        if (days < 1) return Loc.T("Format.Today");
        if (days < 2) return Loc.T("Format.Yesterday");
        if (days < 60) return Loc.T("Format.DaysAgo", (int)days);
        if (days < 730) return Loc.T("Format.MonthsAgo", (int)(days / 30));
        return Loc.T("Format.YearsAgo", (int)(days / 365));
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

/// <summary>true, wenn alle Werte gleich sind (z. B. gewählter Eintrag in einer Liste von RadioButtons).</summary>
public sealed class EqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length > 1 && values.All(v => Equals(v?.ToString(), values[0]?.ToString()));

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
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

/// <summary>Wandelt einen Text in eine deterministische Flächenfarbe um (für Platzhalter-Icons).</summary>
public sealed class NameToBrushConverter : IValueConverter
{
    // Gedeckte Flächenfarben – das Kürzel steht hell darauf
    private static readonly string[] Fills = ["#5A4A1A", "#1F4A66", "#2F5A33", "#6A3F18", "#5E2D47", "#1E5550", "#433D6B", "#57503D"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        int h = 0;
        foreach (var c in s) h = (h * 31 + c) & 0x7FFFFFFF;
        var brush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Fills[h % Fills.Length]));
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
