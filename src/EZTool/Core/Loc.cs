using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Markup;

namespace EZTool.Core;

/// <summary>
/// Mehrsprachigkeit. Texte liegen als JSON unter Localization/&lt;Sprache&gt;/*.json (eingebettete Ressourcen).
/// Fehlt ein Text in einer Sprache, wird Englisch verwendet, danach der Schlüssel selbst.
/// Neue Sprache hinzufügen: Ordner mit den übersetzten JSON-Dateien anlegen und in <see cref="Languages"/> eintragen.
/// </summary>
public static class Loc
{
    public sealed record LanguageInfo(string Code, string NativeName, string EnglishName);

    public static IReadOnlyList<LanguageInfo> Languages { get; } =
    [
        new("de", "Deutsch", "German"),
        new("en", "English", "English"),
        new("es", "Español", "Spanish"),
        new("fr", "Français", "French"),
        new("it", "Italiano", "Italian"),
        new("pt", "Português", "Portuguese"),
        new("nl", "Nederlands", "Dutch"),
        new("pl", "Polski", "Polish"),
        new("cs", "Čeština", "Czech"),
        new("sv", "Svenska", "Swedish"),
        new("tr", "Türkçe", "Turkish"),
        new("ru", "Русский", "Russian"),
        new("uk", "Українська", "Ukrainian"),
        new("ja", "日本語", "Japanese"),
        new("ko", "한국어", "Korean"),
        new("zh-Hans", "简体中文", "Chinese (Simplified)"),
        new("zh-Hant", "繁體中文", "Chinese (Traditional)"),
        new("hi", "हिन्दी", "Hindi"),
        new("id", "Bahasa Indonesia", "Indonesian"),
        new("vi", "Tiếng Việt", "Vietnamese"),
    ];

    // Windows-Sprache und -Region, bevor EZTool die Kultur des Programms umstellt
    private static readonly CultureInfo OsUiCulture = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo OsCulture = CultureInfo.CurrentCulture;

    /// <summary>Aktive Sprache (z. B. "de", "zh-Hans").</summary>
    public static string Code { get; private set; } = "en";

    /// <summary>Kultur für Zahlen- und Datumsformate.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    private static Dictionary<string, string> _strings = new();
    private static Dictionary<string, string> _fallback = new();

    /// <summary>Sprache laden. "auto" = Sprache des Betriebssystems.</summary>
    public static void Init(string? setting)
    {
        Code = string.IsNullOrEmpty(setting) || setting == "auto" || Languages.All(l => l.Code != setting) ? Detect() : setting;
        _fallback = Load("en");
        _strings = Code == "en" ? _fallback : Load(Code);

        Culture = CultureFor(Code);
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentCulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;

        // WPF-Bindungen (StringFormat) formatieren sonst immer englisch
        System.Windows.FrameworkElement.LanguageProperty.OverrideMetadata(typeof(System.Windows.FrameworkElement),
            new System.Windows.FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Culture.IetfLanguageTag)));
    }

    /// <summary>Ermittelt die passende Sprache aus der Windows-Anzeigesprache.</summary>
    public static string Detect()
    {
        var ui = OsUiCulture;
        string name = ui.Name;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) || name.EndsWith("TW", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith("HK", StringComparison.OrdinalIgnoreCase) || name.EndsWith("MO", StringComparison.OrdinalIgnoreCase)
                ? "zh-Hant" : "zh-Hans";
        var two = ui.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == two) ? two : "en";
    }

    public static LanguageInfo DetectedLanguage => Languages.First(l => l.Code == Detect());

    private static CultureInfo CultureFor(string code)
    {
        try
        {
            // Für Datums-/Zahlenformat die Windows-Region bevorzugen, wenn sie zur Sprache passt (z. B. de-AT)
            var current = OsCulture;
            if (current.Name.StartsWith(code.Split('-')[0], StringComparison.OrdinalIgnoreCase) && !code.StartsWith("zh")) return current;
            return code switch
            {
                "zh-Hans" => CultureInfo.GetCultureInfo("zh-CN"),
                "zh-Hant" => CultureInfo.GetCultureInfo("zh-TW"),
                "pt" => CultureInfo.GetCultureInfo("pt-BR"),
                "en" => CultureInfo.GetCultureInfo("en-US"),
                _ => CultureInfo.GetCultureInfo(code),
            };
        }
        catch { return CultureInfo.InvariantCulture; }
    }

    private static readonly JsonSerializerOptions Json = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static Dictionary<string, string> Load(string code)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            var n = name.Replace('\\', '/');
            if (!n.StartsWith($"lang/{code}/", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var s = asm.GetManifestResourceStream(name)!;
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(s, Json);
                if (dict != null)
                    foreach (var (k, v) in dict) result[k] = v;
            }
            catch { /* defekte Datei überspringen */ }
        }
        return result;
    }

    /// <summary>Text zu einem Schlüssel.</summary>
    public static string T(string key) =>
        _strings.TryGetValue(key, out var s) ? s : _fallback.TryGetValue(key, out var f) ? f : key;

    /// <summary>Text mit Platzhaltern {0}, {1} … (formatiert mit der aktiven Kultur).</summary>
    public static string T(string key, params object?[] args)
    {
        try { return string.Format(Culture, T(key), args); }
        catch (FormatException) { return T(key); }
    }
}

/// <summary>XAML: Text="{l:T Dashboard.Title}"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
