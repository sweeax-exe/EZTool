using System.Globalization;
using System.Text;

namespace EZTool.Services;

public sealed record SensorSample(DateTime Time, double CpuLoad, double? CpuTemp, double? GpuLoad, double? GpuTemp, double RamLoad, double? Fps);

/// <summary>
/// Zeichnet alle 5 Sekunden die wichtigsten Werte auf – im Speicher (24 Stunden) und tageweise als CSV
/// unter %LOCALAPPDATA%\EZTool\Logs (Excel-freundlich: Semikolon, deutsches Zahlenformat).
/// </summary>
public sealed class SensorLogService
{
    public static SensorLogService Instance { get; } = new();
    public static string Folder => Path.Combine(SettingsService.Folder, "Logs");

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private const string Header = "Zeit;CPU-Last %;CPU-Temp °C;GPU-Last %;GPU-Temp °C;RAM %;FPS";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Keep = TimeSpan.FromHours(24);

    private readonly List<SensorSample> _samples = new();
    private readonly StringBuilder _pending = new();
    private DateTime _lastSample, _lastFlush = DateTime.Now;

    /// <summary>Aktuelle FPS des Vordergrund-Spiels (vom Gaming-Modus gesetzt).</summary>
    public Func<double?>? FpsSource { get; set; }

    public void Start()
    {
        Load(DateTime.Today.AddDays(-1));
        Load(DateTime.Today);
        Cleanup();
        HardwareService.Instance.Updated += OnHardware;
        System.Windows.Application.Current.Exit += (_, _) => Flush();
    }

    /// <summary>Nur vorhandene Logs laden, ohne selbst etwas aufzuzeichnen (Screenshot-Modus).</summary>
    public void LoadOnly()
    {
        Load(DateTime.Today.AddDays(-1));
        Load(DateTime.Today);
    }

    public IReadOnlyList<SensorSample> Since(DateTime from)
    {
        lock (_samples) return _samples.Where(s => s.Time >= from).ToList();
    }

    private void OnHardware(HardwareSnapshot s)
    {
        var now = DateTime.Now;
        if (now - _lastSample < Interval) return;
        _lastSample = now;

        var sample = new SensorSample(now, s.CpuLoad, s.CpuTemp, s.GpuLoad, s.GpuTemp, s.RamLoad, FpsSource?.Invoke());
        lock (_samples)
        {
            _samples.Add(sample);
            var cut = now - Keep;
            if (_samples.Count > 0 && _samples[0].Time < cut) _samples.RemoveAll(x => x.Time < cut);
        }
        _pending.AppendLine(ToCsv(sample));
        if (now - _lastFlush > TimeSpan.FromMinutes(1)) Flush();
    }

    private static string F(double? v) => v is { } x ? x.ToString("0.0", De) : "";

    public static string ToCsv(SensorSample s) =>
        $"{s.Time:yyyy-MM-dd HH:mm:ss};{F(s.CpuLoad)};{F(s.CpuTemp)};{F(s.GpuLoad)};{F(s.GpuTemp)};{F(s.RamLoad)};{F(s.Fps)}";

    private static string FileFor(DateTime day) => Path.Combine(Folder, $"sensoren-{day:yyyy-MM-dd}.csv");

    public void Flush()
    {
        if (_pending.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var file = FileFor(DateTime.Today);
            var text = (File.Exists(file) ? "" : Header + Environment.NewLine) + _pending;
            File.AppendAllText(file, text, new UTF8Encoding(true));
            _pending.Clear();
            _lastFlush = DateTime.Now;
        }
        catch { /* nächster Versuch beim nächsten Flush */ }
    }

    private void Load(DateTime day)
    {
        try
        {
            var file = FileFor(day);
            if (!File.Exists(file)) return;
            var cut = DateTime.Now - Keep;
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var p = line.Split(';');
                if (p.Length < 6 || !DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) || t < cut) continue;
                double? N(int i) => i < p.Length && double.TryParse(p[i], NumberStyles.Float, De, out var v) ? v : null;
                _samples.Add(new SensorSample(t, N(1) ?? 0, N(2), N(3), N(4), N(5) ?? 0, N(6)));
            }
        }
        catch { /* defekte Datei ignorieren */ }
    }

    private static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var f in Directory.GetFiles(Folder, "sensoren-*.csv"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30)) File.Delete(f);
        }
        catch { /* egal */ }
    }
}
