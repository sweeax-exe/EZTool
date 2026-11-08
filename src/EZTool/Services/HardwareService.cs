using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using LibreHardwareMonitor.Hardware;

namespace EZTool.Services;

public sealed record SensorReading(string Id, string Name, SensorType Type, float? Value, float? Min, float? Max);

public sealed record HardwareNode(string Id, string Name, HardwareType Type, IReadOnlyList<SensorReading> Sensors);

public sealed record DriveSnapshot(string Letter, string Label, string Format, long Total, long Free)
{
    public long Used => Total - Free;
    public double UsedPercent => Total > 0 ? Used * 100.0 / Total : 0;
}

public sealed class HardwareSnapshot
{
    public string CpuName { get; init; } = "CPU";
    public double CpuLoad { get; init; }
    public double? CpuTemp { get; init; }
    public double? CpuClock { get; init; }
    public double? CpuPower { get; init; }
    public double[] ThreadLoads { get; init; } = [];

    public string? GpuName { get; init; }
    public double? GpuLoad { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuHotSpot { get; init; }
    public double? GpuMemUsed { get; init; }   // MB
    public double? GpuMemTotal { get; init; }  // MB
    public double? GpuClock { get; init; }
    public double? GpuFan { get; init; }
    public double? GpuPower { get; init; }

    public ulong RamTotal { get; init; }
    public ulong RamUsed { get; init; }
    public double RamLoad { get; init; }

    public double NetDown { get; init; }
    public double NetUp { get; init; }

    public IReadOnlyList<DriveSnapshot> Drives { get; init; } = [];
    public IReadOnlyList<HardwareNode> Nodes { get; init; } = [];

    public double[] CpuLoadHistory { get; init; } = [];
    public double[] CpuTempHistory { get; init; } = [];
    public double[] GpuLoadHistory { get; init; } = [];
    public double[] GpuTempHistory { get; init; } = [];
    public double[] RamHistory { get; init; } = [];
    public double[] NetDownHistory { get; init; } = [];
    public double[] NetUpHistory { get; init; } = [];

    public TimeSpan Uptime { get; init; }
}

/// <summary>
/// Liest einmal pro Sekunde alle Sensoren (über LibreHardwareMonitor) im Hintergrund
/// und verteilt einen Snapshot an die Oberfläche.
/// </summary>
public sealed class HardwareService : IDisposable
{
    public static HardwareService Instance { get; } = new();

    public event Action<HardwareSnapshot>? Updated;
    public HardwareSnapshot? Latest { get; private set; }

    /// <summary>True, wenn der PawnIO-Treiber fehlt (ohne ihn keine CPU-Temperaturen).</summary>
    public bool LowLevelDriverMissing { get; private set; }
    public string? InitError { get; private set; }

    private const int HistoryLength = 60;
    private readonly History _cpuLoad = new(HistoryLength), _cpuTemp = new(HistoryLength), _gpuLoad = new(HistoryLength),
        _gpuTemp = new(HistoryLength), _ram = new(HistoryLength), _netDown = new(HistoryLength), _netUp = new(HistoryLength);

    private Computer? _computer;
    private CancellationTokenSource? _cts;
    private long _lastRx, _lastTx;
    private DateTime _lastNet;
    private readonly CpuTimes _cpuFallback = new();

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var thread = new Thread(() => Loop(token)) { IsBackground = true, Name = "EZTool.Hardware", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    private volatile bool _reopen;

    /// <summary>Sensoren neu einlesen (z. B. nachdem der PawnIO-Treiber installiert wurde).</summary>
    public void Reinitialize() => _reopen = true;

    private void OpenComputer()
    {
        try { _computer?.Close(); } catch { /* egal */ }
        LowLevelDriverMissing = !PawnIoService.IsInstalled;
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = true,
                IsNetworkEnabled = false,
                IsBatteryEnabled = true,
                IsPsuEnabled = true,
            };
            _computer.Open();
            InitError = null;
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            _computer = null;
        }
    }

    private void Loop(CancellationToken token)
    {
        OpenComputer();

        while (!token.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            if (_reopen)
            {
                _reopen = false;
                OpenComputer();
            }
            try
            {
                var snap = Collect();
                Latest = snap;
                var app = Application.Current;
                app?.Dispatcher.BeginInvoke(() => Publish(snap));
            }
            catch
            {
                // Ein fehlerhafter Sensor darf das Monitoring nie stoppen.
            }

            var interval = Math.Clamp(SettingsService.Current.UpdateIntervalMs, 250, 5000);
            var wait = interval - (int)(DateTime.UtcNow - started).TotalMilliseconds;
            if (wait > 0) token.WaitHandle.WaitOne(wait);
        }
    }

    /// <summary>Jeder Empfänger einzeln – ein fehlerhaftes Modul blockiert so nie die anderen.</summary>
    private void Publish(HardwareSnapshot snap)
    {
        if (Updated == null) return;
        foreach (var handler in Updated.GetInvocationList().Cast<Action<HardwareSnapshot>>())
        {
            try { handler(snap); }
            catch (Exception ex) { Debug.WriteLine($"[EZTool] Hardware-Empfänger fehlgeschlagen: {ex}"); }
        }
    }

    private HardwareSnapshot Collect()
    {
        var nodes = new List<HardwareNode>();
        IHardware? cpu = null, gpu = null;

        if (_computer != null)
        {
            foreach (var hw in _computer.Hardware)
            {
                try { hw.Update(); } catch { /* ignorieren */ }
                foreach (var sub in hw.SubHardware)
                {
                    try { sub.Update(); } catch { /* ignorieren */ }
                }

                nodes.Add(ToNode(hw));
                foreach (var sub in hw.SubHardware) nodes.Add(ToNode(sub, hw.Name));

                if (hw.HardwareType == HardwareType.Cpu && cpu == null) cpu = hw;
                if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel && GpuScore(hw) > GpuScore(gpu)) gpu = hw;
            }
        }

        // ── CPU ──
        double cpuLoad = Find(cpu, SensorType.Load, "CPU Total") ?? _cpuFallback.Sample();
        double? cpuTemp = Pos(FindFirst(cpu, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)", "Core Average", "CPU Cores")
                              ?? MaxOf(cpu, SensorType.Temperature));
        double? cpuClock = Pos(Avg(cpu, SensorType.Clock, s => s.Name.Contains("Core #", StringComparison.OrdinalIgnoreCase)));
        double? cpuPower = Pos(FindFirst(cpu, SensorType.Power, "CPU Package", "Package"));
        var threadLoads = cpu?.Sensors
            .Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue)
            .OrderBy(s => s.Index)
            .Select(s => (double)s.Value!.Value)
            .ToArray() ?? [];

        // ── GPU ──
        double? gpuLoad = FindFirst(gpu, SensorType.Load, "GPU Core", "D3D 3D") ?? MaxOf(gpu, SensorType.Load);
        double? gpuTemp = Pos(FindFirst(gpu, SensorType.Temperature, "GPU Core", "GPU Temperature") ?? MaxOf(gpu, SensorType.Temperature));
        double? gpuHot = Pos(FindFirst(gpu, SensorType.Temperature, "GPU Hot Spot"));
        double? memUsed = FindFirst(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        double? memTotal = FindFirst(gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
        double? gpuClock = Pos(FindFirst(gpu, SensorType.Clock, "GPU Core"));
        double? gpuFan = MaxOf(gpu, SensorType.Fan);
        double? gpuPower = Pos(FindFirst(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Core") ?? MaxOf(gpu, SensorType.Power));

        // ── RAM ──
        var (total, avail, load) = Core.Native.GetMemory();

        // ── Netzwerk ──
        var (down, up) = NetworkSpeed();

        // ── Laufwerke ──
        var drives = new List<DriveSnapshot>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                drives.Add(new DriveSnapshot(d.Name.TrimEnd('\\'), string.IsNullOrWhiteSpace(d.VolumeLabel) ? Core.Loc.T("Hardware.LocalDisk") : d.VolumeLabel,
                    d.DriveFormat, d.TotalSize, d.TotalFreeSpace));
            }
            catch { /* Laufwerk nicht bereit */ }
        }

        _cpuLoad.Push(cpuLoad);
        _cpuTemp.Push(cpuTemp ?? 0);
        _gpuLoad.Push(gpuLoad ?? 0);
        _gpuTemp.Push(gpuTemp ?? 0);
        _ram.Push(load);
        _netDown.Push(down);
        _netUp.Push(up);

        return new HardwareSnapshot
        {
            CpuName = Clean(cpu?.Name) ?? SystemInfo.Current?.CpuName ?? "CPU",
            CpuLoad = cpuLoad,
            CpuTemp = cpuTemp,
            CpuClock = cpuClock,
            CpuPower = cpuPower,
            ThreadLoads = threadLoads,
            GpuName = Clean(gpu?.Name) ?? SystemInfo.Current?.GpuName,
            GpuLoad = gpuLoad,
            GpuTemp = gpuTemp,
            GpuHotSpot = gpuHot,
            GpuMemUsed = memUsed,
            GpuMemTotal = memTotal,
            GpuClock = gpuClock,
            GpuFan = gpuFan,
            GpuPower = gpuPower,
            RamTotal = total,
            RamUsed = total - avail,
            RamLoad = load,
            NetDown = down,
            NetUp = up,
            Drives = drives,
            Nodes = nodes,
            CpuLoadHistory = _cpuLoad.ToArray(),
            CpuTempHistory = _cpuTemp.ToArray(),
            GpuLoadHistory = _gpuLoad.ToArray(),
            GpuTempHistory = _gpuTemp.ToArray(),
            RamHistory = _ram.ToArray(),
            NetDownHistory = _netDown.ToArray(),
            NetUpHistory = _netUp.ToArray(),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }

    /// <summary>Sensoren ohne Treiberzugriff liefern oft 0 – das als "nicht verfügbar" behandeln.</summary>
    private static double? Pos(double? v) => v is > 0.01 && !double.IsNaN(v.Value) ? v : null;

    /// <summary>Bevorzugt die dedizierte Grafikkarte (NVIDIA, dann viel VRAM) vor der iGPU.</summary>
    private static double GpuScore(IHardware? hw)
    {
        if (hw == null) return -1;
        double vram = FindFirst(hw, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total") ?? 0;
        double bonus = hw.HardwareType switch { HardwareType.GpuNvidia => 1_000_000, HardwareType.GpuAmd => 1000, _ => 0 };
        return bonus + vram;
    }

    private static string? Clean(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name.Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ").Trim();

    private static HardwareNode ToNode(IHardware hw, string? parent = null) =>
        new(hw.Identifier.ToString(), parent == null ? hw.Name : $"{parent} · {hw.Name}", hw.HardwareType,
            hw.Sensors.Select(s => new SensorReading(s.Identifier.ToString(), s.Name, s.SensorType, s.Value, s.Min, s.Max)).ToList());

    private static double? Find(IHardware? hw, SensorType type, string name)
    {
        if (hw == null) return null;
        var s = hw.Sensors.FirstOrDefault(x => x.SensorType == type && x.Value.HasValue && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return s?.Value;
    }

    private static double? FindFirst(IHardware? hw, SensorType type, params string[] names)
    {
        foreach (var n in names)
        {
            var v = Find(hw, type, n);
            if (v.HasValue && !double.IsNaN(v.Value)) return v;
        }
        return null;
    }

    private static double? MaxOf(IHardware? hw, SensorType type)
    {
        var vals = hw?.Sensors.Where(s => s.SensorType == type && s.Value.HasValue && !float.IsNaN(s.Value.Value)).Select(s => (double)s.Value!.Value).ToList();
        return vals is { Count: > 0 } ? vals.Max() : null;
    }

    private static double? Avg(IHardware? hw, SensorType type, Func<ISensor, bool> filter)
    {
        var vals = hw?.Sensors.Where(s => s.SensorType == type && s.Value.HasValue && filter(s)).Select(s => (double)s.Value!.Value).ToList();
        return vals is { Count: > 0 } ? vals.Average() : null;
    }

    private (double Down, double Up) NetworkSpeed()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var st = ni.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
        }
        catch { return (0, 0); }

        var now = DateTime.UtcNow;
        double secs = (now - _lastNet).TotalSeconds;
        double down = 0, up = 0;
        if (_lastNet != default && secs > 0)
        {
            down = Math.Max(0, (rx - _lastRx) / secs);
            up = Math.Max(0, (tx - _lastTx) / secs);
        }
        _lastRx = rx;
        _lastTx = tx;
        _lastNet = now;
        return (down, up);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _computer?.Close(); } catch { /* egal beim Beenden */ }
    }

    private sealed class History(int capacity)
    {
        private readonly Queue<double> _q = new();

        public void Push(double v)
        {
            _q.Enqueue(double.IsNaN(v) ? 0 : v);
            while (_q.Count > capacity) _q.Dequeue();
        }

        public double[] ToArray() => _q.ToArray();
    }

    /// <summary>CPU-Auslastung über GetSystemTimes – Fallback, falls LibreHardwareMonitor nicht startet.</summary>
    private sealed class CpuTimes
    {
        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        private long _idle, _kernel, _user;

        public double Sample()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
            long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
            _idle = idle; _kernel = kernel; _user = user;
            long total = dk + du;
            return total <= 0 ? 0 : Math.Clamp((total - di) * 100.0 / total, 0, 100);
        }
    }
}
