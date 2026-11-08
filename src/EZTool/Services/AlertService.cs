namespace EZTool.Services;

/// <summary>
/// Überwacht Temperaturen und freien Speicher und meldet sich über eine Windows-Benachrichtigung.
/// Ein Alarm wird erst nach mehreren Messungen über dem Grenzwert ausgelöst (keine Fehlalarme durch kurze Spitzen)
/// und wiederholt sich frühestens nach einer Pause.
/// </summary>
public sealed class AlertService
{
    private const int SamplesNeeded = 5;
    private static readonly TimeSpan TempCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DriveCooldown = TimeSpan.FromHours(6);

    private int _cpuHot, _gpuHot, _tick;
    private DateTime _lastCpu, _lastGpu, _lastDrive;

    public AlertService() => HardwareService.Instance.Updated += Check;

    private void Check(HardwareSnapshot s)
    {
        var cfg = SettingsService.Current;
        var tray = TrayService.Instance;
        if (!cfg.AlertsEnabled || tray == null) return;
        var now = DateTime.Now;

        _cpuHot = s.CpuTemp >= cfg.CpuTempLimit ? _cpuHot + 1 : 0;
        if (_cpuHot >= SamplesNeeded && now - _lastCpu > TempCooldown)
        {
            _lastCpu = now;
            tray.Notify(Core.Loc.T("Alert.CpuHotTitle"), Core.Loc.T("Alert.CpuHotText", s.CpuName, s.CpuTemp, cfg.CpuTempLimit), warning: true, openModule: "dashboard");
        }

        _gpuHot = s.GpuTemp >= cfg.GpuTempLimit ? _gpuHot + 1 : 0;
        if (_gpuHot >= SamplesNeeded && now - _lastGpu > TempCooldown)
        {
            _lastGpu = now;
            tray.Notify(Core.Loc.T("Alert.GpuHotTitle"), Core.Loc.T("Alert.GpuHotText", s.GpuName, s.GpuTemp, cfg.GpuTempLimit), warning: true, openModule: "dashboard");
        }

        // Speicherplatz nur gelegentlich prüfen
        if (_tick++ % 60 != 0 || now - _lastDrive < DriveCooldown) return;
        foreach (var d in s.Drives)
        {
            if (d.Total <= 0) continue;
            var freePct = d.Free * 100.0 / d.Total;
            if (freePct >= cfg.DriveFreeLimitPercent) continue;
            _lastDrive = now;
            tray.Notify(Core.Loc.T("Alert.DriveFullTitle", d.Letter), Core.Loc.T("Alert.DriveFullText", Core.Format.Bytes(d.Free), freePct), warning: true, openModule: "cleaner");
            break;
        }
    }

    public static void Test() =>
        TrayService.Instance?.Notify(Core.Loc.T("Alert.TestTitle"), Core.Loc.T("Alert.TestText"));
}
