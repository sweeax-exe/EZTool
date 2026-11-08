using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Controls;
using EZTool.Core;
using EZTool.Services;
using Microsoft.Win32;

namespace EZTool.Modules.History;

public sealed class HistoryModule : IModule
{
    public string Id => "history";
    public string Title => Loc.T("History.Title");
    public string Subtitle => Loc.T("History.Subtitle");
    public string Icon => "";
    public int Order => 40;

    public FrameworkElement CreateView() => new HistoryView { DataContext = new HistoryViewModel() };
}

public sealed partial class HistoryViewModel : ObservableObject
{
    private static readonly Color Cyan = Color.FromRgb(0x00, 0xE5, 0xFF), Magenta = Color.FromRgb(0xFF, 0x3D, 0x9A),
        Green = Color.FromRgb(0x2C, 0xF5, 0xA0), Amber = Color.FromRgb(0xFF, 0xB5, 0x47);
    private const int MaxPoints = 480;

    [ObservableProperty] private int _rangeMinutes = 60;
    [ObservableProperty] private IReadOnlyList<DateTime>? _times;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _tempSeries;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _loadSeries;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _fpsSeries;
    [ObservableProperty] private bool _hasFps;

    [ObservableProperty] private string _cpuTempMax = "—";
    [ObservableProperty] private string _cpuTempMaxAt = "";
    [ObservableProperty] private string _gpuTempMax = "—";
    [ObservableProperty] private string _gpuTempMaxAt = "";
    [ObservableProperty] private string _cpuLoadAvg = "—";
    [ObservableProperty] private string _gpuLoadAvg = "—";
    [ObservableProperty] private int _sampleCount;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public HistoryViewModel() => _timer.Tick += (_, _) => Refresh();

    public void SetActive(bool active)
    {
        if (active) { Refresh(); _timer.Start(); }
        else _timer.Stop();
    }

    partial void OnRangeMinutesChanged(int value) => Refresh();

    [RelayCommand]
    private void SetRange(string minutes) => RangeMinutes = int.Parse(minutes);

    private IReadOnlyList<SensorSample> Current() => SensorLogService.Instance.Since(DateTime.Now.AddMinutes(-RangeMinutes));

    private void Refresh()
    {
        var all = Current();
        SampleCount = all.Count;

        // Auf max. MaxPoints verdichten: Temperaturen = Maximum (Spitzen sichtbar), Last = Durchschnitt
        int bucket = Math.Max(1, (int)Math.Ceiling(all.Count / (double)MaxPoints));
        var times = new List<DateTime>();
        var cpuT = new List<double?>(); var gpuT = new List<double?>();
        var cpuL = new List<double?>(); var gpuL = new List<double?>(); var ram = new List<double?>(); var fps = new List<double?>();
        for (int i = 0; i < all.Count; i += bucket)
        {
            var b = all.Skip(i).Take(bucket).ToList();
            times.Add(b[^1].Time);
            cpuT.Add(b.Max(x => x.CpuTemp));
            gpuT.Add(b.Max(x => x.GpuTemp));
            cpuL.Add(b.Average(x => x.CpuLoad));
            gpuL.Add(b.Average(x => x.GpuLoad));
            ram.Add(b.Average(x => x.RamLoad));
            fps.Add(b.Average(x => x.Fps));
        }

        Times = times;
        TempSeries = [new("CPU", Cyan, cpuT, "°C"), new("GPU", Magenta, gpuT, "°C")];
        LoadSeries = [new("CPU", Cyan, cpuL, "%"), new("GPU", Magenta, gpuL, "%"), new("RAM", Green, ram, "%")];
        HasFps = fps.Any(f => f is > 0);
        FpsSeries = [new("FPS", Amber, fps, "")];

        var maxCpu = all.Where(s => s.CpuTemp != null).MaxBy(s => s.CpuTemp);
        var maxGpu = all.Where(s => s.GpuTemp != null).MaxBy(s => s.GpuTemp);
        CpuTempMax = maxCpu != null ? $"{maxCpu.CpuTemp:0} °C" : "—";
        CpuTempMaxAt = maxCpu != null ? Loc.T("History.MaxAt", maxCpu.Time) : Loc.T("History.NoData");
        GpuTempMax = maxGpu != null ? $"{maxGpu.GpuTemp:0} °C" : "—";
        GpuTempMaxAt = maxGpu != null ? Loc.T("History.MaxAt", maxGpu.Time) : Loc.T("History.NoData");
        CpuLoadAvg = all.Count > 0 ? $"{all.Average(s => s.CpuLoad):0} %" : "—";
        GpuLoadAvg = all.Any(s => s.GpuLoad != null) ? $"{all.Where(s => s.GpuLoad != null).Average(s => s.GpuLoad):0} %" : "—";
    }

    [RelayCommand]
    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Title = Loc.T("History.ExportTitle"),
            Filter = Loc.T("History.CsvFilter"),
            FileName = $"EZTool-Verlauf-{DateTime.Now:yyyy-MM-dd-HHmm}.csv",
        };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("Zeit;CPU-Last %;CPU-Temp °C;GPU-Last %;GPU-Temp °C;RAM %;FPS" + Environment.NewLine);
        foreach (var s in Current()) sb.AppendLine(SensorLogService.ToCsv(s));
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    [RelayCommand]
    private static void OpenFolder()
    {
        SensorLogService.Instance.Flush();
        Directory.CreateDirectory(SensorLogService.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SensorLogService.Folder}\"") { UseShellExecute = true });
    }
}
