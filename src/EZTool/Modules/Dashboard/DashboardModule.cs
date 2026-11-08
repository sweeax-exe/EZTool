using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using EZTool.Core;
using EZTool.Services;

namespace EZTool.Modules.Dashboard;

public sealed class DashboardModule : IModule
{
    public string Id => "dashboard";
    public string Title => "Dashboard";
    public string Subtitle => "Live-Überblick über Auslastung, Temperaturen und dein System.";
    public string Icon => "";
    public int Order => 10;

    public FrameworkElement CreateView() => new DashboardView { DataContext = new DashboardViewModel() };
}

public sealed partial class DashboardViewModel : ObservableObject
{
    // CPU
    [ObservableProperty] private string _cpuName = "Prozessor";
    [ObservableProperty] private double _cpuLoad;
    [ObservableProperty] private string _cpuLoadText = "—";
    [ObservableProperty] private string _cpuTempText = "—";
    [ObservableProperty] private double _cpuTemp;
    [ObservableProperty] private string _cpuClockText = "—";
    [ObservableProperty] private string _cpuPowerText = "—";
    [ObservableProperty] private double[] _threadLoads = [];

    // GPU
    [ObservableProperty] private string _gpuName = "Grafikkarte";
    [ObservableProperty] private double _gpuLoad;
    [ObservableProperty] private string _gpuLoadText = "—";
    [ObservableProperty] private string _gpuTempText = "—";
    [ObservableProperty] private double _gpuTemp;
    [ObservableProperty] private double _gpuHotSpot;
    [ObservableProperty] private string _gpuHotSpotText = "—";
    [ObservableProperty] private string _gpuMemText = "—";
    [ObservableProperty] private string _gpuClockText = "—";
    [ObservableProperty] private string _gpuFanText = "—";
    [ObservableProperty] private string _gpuPowerText = "—";

    // RAM
    [ObservableProperty] private double _ramLoad;
    [ObservableProperty] private string _ramLoadText = "—";
    [ObservableProperty] private string _ramUsedText = "—";
    [ObservableProperty] private string _ramFreeText = "—";
    [ObservableProperty] private string _ramTotalText = "—";

    // Systemlaufwerk
    [ObservableProperty] private double _sysDriveLoad;
    [ObservableProperty] private string _sysDriveLoadText = "—";
    [ObservableProperty] private string _sysDriveFreeText = "—";
    [ObservableProperty] private string _sysDriveTotalText = "—";
    [ObservableProperty] private string _sysDriveName = "C:";

    // Verlauf
    [ObservableProperty] private double[] _cpuHistory = [];
    [ObservableProperty] private double[] _gpuHistory = [];
    [ObservableProperty] private double[] _ramHistory = [];
    [ObservableProperty] private double[] _netDownHistory = [];
    [ObservableProperty] private double[] _netUpHistory = [];

    // Netzwerk
    [ObservableProperty] private string _netDownText = "—";
    [ObservableProperty] private string _netUpText = "—";

    // Laufwerke & System
    [ObservableProperty] private IReadOnlyList<DriveSnapshot> _drives = [];
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private SystemInfo? _system;
    [ObservableProperty] private bool _showDriverHint;
    [ObservableProperty] private string _driverHint = "";
    [ObservableProperty] private bool _canInstallDriver;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task InstallDriverAsync() => await PawnIoService.InstallWithDialogsAsync();

    private int _tick;

    private static readonly bool IsAdmin = new global::System.Security.Principal.WindowsPrincipal(global::System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(global::System.Security.Principal.WindowsBuiltInRole.Administrator);

    public DashboardViewModel()
    {
        HardwareService.Instance.Updated += Apply;
        if (HardwareService.Instance.Latest is { } s) Apply(s);
    }

    private void Apply(HardwareSnapshot s)
    {
        System ??= SystemInfo.Current;

        CpuName = s.CpuName;
        CpuLoad = s.CpuLoad;
        CpuLoadText = s.CpuLoad.ToString("0");
        CpuTemp = s.CpuTemp ?? 0;
        CpuTempText = s.CpuTemp is { } ct ? $"{ct:0} °C" : "n/a";
        CpuClockText = s.CpuClock is { } cc ? $"{cc / 1000:0.00} GHz" : "—";
        CpuPowerText = s.CpuPower is { } cp ? $"{cp:0} W" : "—";
        ThreadLoads = s.ThreadLoads;

        GpuName = s.GpuName ?? "Keine GPU erkannt";
        GpuLoad = s.GpuLoad ?? 0;
        GpuLoadText = s.GpuLoad is { } gl ? gl.ToString("0") : "—";
        GpuTemp = s.GpuTemp ?? 0;
        GpuTempText = s.GpuTemp is { } gt ? $"{gt:0} °C" : "n/a";
        GpuHotSpot = s.GpuHotSpot ?? 0;
        GpuHotSpotText = s.GpuHotSpot is { } gh ? $"{gh:0} °C" : "n/a";
        GpuMemText = s.GpuMemUsed is { } mu && s.GpuMemTotal is { } mt && mt > 0
            ? $"{mu / 1024:0.0} / {mt / 1024:0} GB"
            : "—";
        GpuClockText = s.GpuClock is { } gc ? $"{gc:0} MHz" : "—";
        GpuFanText = s.GpuFan is { } gf ? $"{gf:0} RPM" : "—";
        GpuPowerText = s.GpuPower is { } gp ? $"{gp:0} W" : "—";

        RamLoad = s.RamLoad;
        RamLoadText = s.RamLoad.ToString("0");
        RamUsedText = Format.Bytes(s.RamUsed);
        RamFreeText = Format.Bytes(s.RamTotal - s.RamUsed);
        RamTotalText = Format.Bytes(s.RamTotal);

        var sysLetter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        var sys = s.Drives.FirstOrDefault(d => d.Letter.Equals(sysLetter, StringComparison.OrdinalIgnoreCase)) ?? s.Drives.FirstOrDefault();
        if (sys != null)
        {
            SysDriveName = sys.Letter;
            SysDriveLoad = sys.UsedPercent;
            SysDriveLoadText = sys.UsedPercent.ToString("0");
            SysDriveFreeText = Format.Bytes(sys.Free);
            SysDriveTotalText = Format.Bytes(sys.Total);
        }

        CpuHistory = s.CpuLoadHistory;
        GpuHistory = s.GpuLoadHistory;
        RamHistory = s.RamHistory;
        NetDownHistory = s.NetDownHistory;
        NetUpHistory = s.NetUpHistory;
        NetDownText = Format.Speed(s.NetDown);
        NetUpText = Format.Speed(s.NetUp);

        if (_tick++ % 5 == 0 || Drives.Count != s.Drives.Count) Drives = s.Drives;
        UptimeText = Format.Duration(s.Uptime);
        ShowDriverHint = s.CpuTemp is null;
        CanInstallDriver = IsAdmin && HardwareService.Instance.LowLevelDriverMissing;
        DriverHint = !IsAdmin
            ? "CPU-Temperatur, -Takt und -Leistung sind nur mit Administrator-Rechten verfügbar. Starte EZTool als Administrator."
            : HardwareService.Instance.LowLevelDriverMissing
                ? "CPU-Temperaturen benötigen den kostenlosen PawnIO-Treiber (pawnio.eu). GPU-, RAM- und Laufwerksdaten funktionieren auch ohne."
                : "Für diese CPU wurden keine Temperatursensoren gefunden.";
    }
}
