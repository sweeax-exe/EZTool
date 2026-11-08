using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Services;
using EZTool.Services.Network;

namespace EZTool.Modules.Network;

public sealed class NetworkModule : IModule
{
    public string Id => "network";
    public string Title => Loc.T("Network.Title");
    public string Subtitle => Loc.T("Network.Subtitle");
    public string Icon => "";
    public int Order => 45;

    public FrameworkElement CreateView() => new NetworkView { DataContext = new NetworkViewModel() };
}

/// <summary>Name und Symbol eines Programms – einmal pro EXE geladen und für alle Zeilen geteilt.</summary>
public sealed partial class AppIdentity : ObservableObject
{
    public string Path { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private ImageSource? _icon;

    public AppIdentity(string path, string name)
    {
        Path = path;
        _name = name;
    }

    private static readonly ConcurrentDictionary<string, AppIdentity> ByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<int, (string Path, string Name)> Pids = new();

    public static AppIdentity ForPath(string path, string fallbackName) =>
        ByPath.GetOrAdd(path.Length > 0 ? path : "?" + fallbackName, key =>
        {
            var id = new AppIdentity(path, fallbackName);
            if (path.Length > 0) _ = id.LoadAsync();
            return id;
        });

    public static AppIdentity ForPid(int pid)
    {
        if (!Pids.TryGetValue(pid, out var info))
        {
            string name = pid switch { 0 => "System Idle", 4 => "System", _ => "" };
            string path = pid > 4 ? NetNative.ProcessPath(pid) : "";
            if (name.Length == 0)
            {
                try { name = Process.GetProcessById(pid).ProcessName; }
                catch { name = path.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(path) : $"PID {pid}"; }
            }
            info = (path, name);
            Pids[pid] = info;
        }
        return ForPath(info.Path, info.Name);
    }

    private async Task LoadAsync()
    {
        var (desc, icon) = await Task.Run(() => (ProcessService.FileInfo(Path).Description, Native.ExtractIcon(Path, 0)));
        if (desc.Length > 0) Name = desc;
        Icon = icon;
    }
}

public sealed partial class AppRowVm : ObservableObject
{
    public AppIdentity App { get; }
    [ObservableProperty] private string _rateDown = "";
    [ObservableProperty] private string _rateUp = "";
    [ObservableProperty] private string _periodDown = "";
    [ObservableProperty] private string _periodUp = "";
    [ObservableProperty] private double _share;
    [ObservableProperty] private bool _isActive;
    internal long SortKey;

    public AppRowVm(AppIdentity app) => App = app;
}

public sealed partial class ConnRowVm : ObservableObject
{
    public AppIdentity App { get; }
    public string Protocol { get; }
    public string Address { get; }
    public IPAddress Remote { get; }
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _rateDown = "";
    [ObservableProperty] private string _rateUp = "";
    [ObservableProperty] private string _total = "";
    [ObservableProperty] private bool _isActive;
    internal long SortKey;

    public ConnRowVm(AppIdentity app, IPAddress remote, int port, bool udp)
    {
        App = app;
        Remote = remote;
        Protocol = udp ? "UDP" : "TCP";
        Address = remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{remote}]:{port}" : $"{remote}:{port}";
    }
}

public sealed record PortRowVm(AppIdentity App, string Protocol, int Port, string Scope, bool Exposed);

public sealed record DeviceRowVm(string Name, string Ip, string Mac, string Vendor, string Ping, bool IsRouter, bool IsSelf);

public sealed record AdapterRowVm(AdapterInfo Info, IReadOnlyList<KeyValue> Lines)
{
    public string Title => Info.Name;
    public string Subtitle => Info.Description;
    public bool IsUp => Info.IsUp;
}

public sealed record KeyValue(string Label, string Value);

public sealed partial class NetworkViewModel : ObservableObject, IActivatable
{
    private readonly TrafficMonitor _mon = TrafficMonitor.Instance;
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(30) };
    private int _tick;
    private CancellationTokenSource? _scanCts;

    // Messfeld
    [ObservableProperty] private string _downText = "—";
    [ObservableProperty] private string _upText = "—";
    [ObservableProperty] private string _downBits = "";
    [ObservableProperty] private string _upBits = "";
    [ObservableProperty] private double _downMeter;
    [ObservableProperty] private double _upMeter;
    [ObservableProperty] private string _todayDown = "—";
    [ObservableProperty] private string _todayUp = "—";
    [ObservableProperty] private string _connectionsText = "—";
    [ObservableProperty] private string _listeningText = "";
    [ObservableProperty] private double[] _downHistory = [];
    [ObservableProperty] private double[] _upHistory = [];
    [ObservableProperty] private double _chartMax = 1;
    [ObservableProperty] private string _chartScale = "";

    // Allgemein
    [ObservableProperty] private string _tab = "overview";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyActive = true;
    [ObservableProperty] private int _usageDays = 1;
    [ObservableProperty] private string? _publicIp;
    [ObservableProperty] private bool _loadingPublicIp;

    // Geräte
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private double _scanProgress;
    [ObservableProperty] private string _scanSummary = "";

    public ObservableCollection<KeyValue> Link { get; } = [];
    public ObservableCollection<AppRowVm> TopApps { get; } = [];
    public ObservableCollection<AppRowVm> Apps { get; } = [];
    public ObservableCollection<ConnRowVm> Connections { get; } = [];
    public ObservableCollection<PortRowVm> Ports { get; } = [];
    public ObservableCollection<DeviceRowVm> Devices { get; } = [];
    public ObservableCollection<AdapterRowVm> Adapters { get; } = [];

    private readonly Dictionary<string, AppRowVm> _appRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FlowKey, ConnRowVm> _connRows = new();
    private List<AppUsage> _usage = [];
    private long _linkSpeed;

    public bool IsAdmin => TrafficMonitor.CanMeasure;

    public NetworkViewModel()
    {
        _slow.Tick += async (_, _) => await LoadUsageAsync();
    }

    public void OnActivated() { }

    /// <summary>Seite sichtbar: Messung starten.</summary>
    public void Start()
    {
        _mon.Acquire();
        Error = _mon.Error;
        _mon.Tick += OnMonitorTick;
        HardwareService.Instance.Updated += OnHardware;
        _slow.Start();
        RefreshLink();
        _ = LoadUsageAsync();
        RefreshTables();
    }

    public void Stop()
    {
        _mon.Tick -= OnMonitorTick;
        HardwareService.Instance.Updated -= OnHardware;
        _mon.Release();
        _slow.Stop();
    }

    private void OnMonitorTick() => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        _tick++;
        RefreshTables();
    });

    // ───────────── Messfeld + Verlauf (aus den Adapter-Zählern, funktioniert auch ohne Admin) ─────────────

    private void OnHardware(HardwareSnapshot s)
    {
        DownText = Format.Speed(s.NetDown);
        UpText = Format.Speed(s.NetUp);
        DownBits = Bits(s.NetDown);
        UpBits = Bits(s.NetUp);
        double link = _linkSpeed > 0 ? _linkSpeed / 8.0 : 0;
        DownMeter = link > 0 ? Math.Min(100, 100 * s.NetDown / link) : 0;
        UpMeter = link > 0 ? Math.Min(100, 100 * s.NetUp / link) : 0;
        DownHistory = s.NetDownHistory;
        UpHistory = s.NetUpHistory;
        double max = 1;
        foreach (var v in s.NetDownHistory) max = Math.Max(max, v);
        foreach (var v in s.NetUpHistory) max = Math.Max(max, v);
        ChartMax = max * 1.15;
        ChartScale = Format.Speed(ChartMax);
        if (!_mon.IsRunning && _tick++ % 3 == 0) RefreshTables(); // ohne Admin trotzdem Verbindungen zeigen
    }

    private static string Bits(double bytesPerSec)
    {
        double bits = bytesPerSec * 8;
        return bits >= 1e9 ? $"{bits / 1e9:0.00} Gbit/s" : bits >= 1e6 ? $"{bits / 1e6:0.0} Mbit/s" : $"{bits / 1e3:0} kbit/s";
    }

    // ───────────── Verbindung ─────────────

    private void RefreshLink()
    {
        var all = NetworkInfo.Adapters();
        var main = all.FirstOrDefault(a => a.IsUp && a.Gateways.Count > 0);
        _linkSpeed = main?.SpeedBps ?? 0;
        Link.Clear();
        if (main != null)
        {
            Link.Add(new KeyValue(Loc.T("Network.Adapter"), main.Name == main.Type ? main.Name : $"{main.Name} ({main.Type})"));
            if (main.SpeedBps > 0) Link.Add(new KeyValue(Loc.T("Network.LinkSpeed"), Bits(main.SpeedBps / 8.0)));
            if (main.Wifi is { } w)
            {
                Link.Add(new KeyValue(Loc.T("Network.Wifi"), w.Ssid));
                Link.Add(new KeyValue(Loc.T("Network.Signal"), $"{w.SignalPercent} %"));
                Link.Add(new KeyValue(Loc.T("Network.WifiRate"), $"{w.RxMbps:0} / {w.TxMbps:0} Mbit/s"));
            }
            Link.Add(new KeyValue(Loc.T("Network.LocalIp"), string.Join("\n", main.IPv4.Select(a => a.Split('/')[0]))));
            Link.Add(new KeyValue(Loc.T("Network.Router"), string.Join("\n", V4First(main.Gateways).Take(2))));
            Link.Add(new KeyValue(Loc.T("Network.DnsServers"), string.Join("\n", V4First(main.Dns).Take(3))));
        }
        Adapters.Clear();
        foreach (var a in all) Adapters.Add(new AdapterRowVm(a, AdapterLines(a)));
    }

    /// <summary>IPv4 zuerst. Im Screenshot-Modus nur IPv4 – globale IPv6-Adressen verraten den Anschluss.</summary>
    private static IEnumerable<string> V4First(IEnumerable<string> list) =>
        list.Where(a => !a.Contains(':')).Concat(App.ScreenshotMode ? [] : list.Where(a => a.Contains(':')));

    private static List<KeyValue> AdapterLines(AdapterInfo a)
    {
        var l = new List<(string, string)>
        {
            (Loc.T("Network.Type"), a.Type),
            (Loc.T("Network.Status"), a.IsUp ? Loc.T("Network.Connected") : Loc.T("Network.Disconnected")),
        };
        if (a.SpeedBps > 0) l.Add((Loc.T("Network.LinkSpeed"), Bits(a.SpeedBps / 8.0)));
        if (a.Wifi is { } w)
        {
            l.Add((Loc.T("Network.Wifi"), w.Ssid));
            l.Add((Loc.T("Network.Signal"), $"{w.SignalPercent} %"));
            l.Add((Loc.T("Network.WifiRate"), $"{w.RxMbps:0} / {w.TxMbps:0} Mbit/s"));
            if (w.Channel > 0) l.Add((Loc.T("Network.Channel"), w.Channel.ToString()));
        }
        if (a.Mac != null) l.Add(("MAC", a.Mac));
        if (a.IPv4.Count > 0) l.Add(("IPv4", string.Join("\n", a.IPv4)));
        if (a.IPv6.Count > 0) l.Add(("IPv6", string.Join("\n", a.IPv6.Take(4))));
        if (a.Gateways.Count > 0) l.Add((Loc.T("Network.Router"), string.Join("\n", V4First(a.Gateways))));
        if (a.Dns.Count > 0) l.Add((Loc.T("Network.DnsServers"), string.Join("\n", V4First(a.Dns).Take(4))));
        l.Add(("DHCP", a.Dhcp ? Loc.T("Network.DhcpOn", a.DhcpServer ?? "—") : Loc.T("Network.DhcpOff")));
        if (a.DnsSuffix != null) l.Add((Loc.T("Network.Suffix"), a.DnsSuffix));
        if (a.IsUp) l.Add((Loc.T("Network.SinceBoot"), $"↓ {Format.Bytes(a.BytesReceived)}   ↑ {Format.Bytes(a.BytesSent)}"));
        return l.Select(x => new KeyValue(x.Item1, x.Item2)).ToList();
    }

    // ───────────── Datenverbrauch (Windows-Statistik) ─────────────

    private async Task LoadUsageAsync()
    {
        try
        {
            var today = await NetworkInfo.UsageAsync(DateTime.Today);
            TodayDown = Format.Bytes(today.Sum(u => u.Received));
            TodayUp = Format.Bytes(today.Sum(u => u.Sent));
            _usage = UsageDays == 1 ? today : await NetworkInfo.UsageAsync(DateTime.Today.AddDays(1 - UsageDays));
            RefreshApps();
        }
        catch { /* Statistik nicht verfügbar */ }
    }

    partial void OnUsageDaysChanged(int value) => _ = LoadUsageAsync();

    // ───────────── Tabellen ─────────────

    private void RefreshTables()
    {
        RefreshApps();
        if (Tab == "connections" || _tick % 3 == 0) RefreshConnections();
    }

    private void RefreshApps()
    {
        var seen = new HashSet<AppRowVm>();
        foreach (var (pid, flow) in _mon.Apps)
        {
            var app = AppIdentity.ForPid(pid);
            seen.Add(Row(app));
        }
        foreach (var u in _usage) seen.Add(Row(u.Path != null ? AppIdentity.ForPath(u.Path, u.Name) : AppIdentity.ForPath("", u.Name)));

        // Live-Raten pro EXE zusammenfassen (mehrere Prozesse wie bei Browsern)
        var live = new Dictionary<AppRowVm, (long Down, long Up)>();
        foreach (var (pid, flow) in _mon.Apps)
        {
            var row = Row(AppIdentity.ForPid(pid));
            live.TryGetValue(row, out var v);
            live[row] = (v.Down + flow.RateDown, v.Up + flow.RateUp);
        }
        var usage = _usage.ToDictionary(u => u.Path ?? "?" + u.Name, StringComparer.OrdinalIgnoreCase);
        long maxPeriod = Math.Max(1, _usage.Count > 0 ? _usage.Max(u => u.Received + u.Sent) : 1);

        foreach (var row in seen)
        {
            live.TryGetValue(row, out var r);
            row.RateDown = r.Down > 0 ? Format.Speed(r.Down) : "";
            row.RateUp = r.Up > 0 ? Format.Speed(r.Up) : "";
            row.IsActive = r.Down + r.Up > 0;
            var key = row.App.Path.Length > 0 ? row.App.Path : "?" + row.App.Name;
            if (usage.TryGetValue(key, out var u))
            {
                row.PeriodDown = Format.Bytes(u.Received);
                row.PeriodUp = Format.Bytes(u.Sent);
                row.Share = 100.0 * (u.Received + u.Sent) / maxPeriod;
                row.SortKey = (r.Down + r.Up) * 1000 + u.Received + u.Sent;
            }
            else
            {
                row.PeriodDown = row.PeriodUp = "";
                row.Share = 0;
                row.SortKey = (r.Down + r.Up) * 1000;
            }
        }

        var filtered = seen.Where(r => Search.Length == 0 || r.App.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                                                         || r.App.Path.Contains(Search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.SortKey).Take(150).ToList();
        Sync(Apps, filtered);
        Sync(TopApps, seen.Where(r => r.IsActive).OrderByDescending(r => r.SortKey).Take(6).ToList());
    }

    private AppRowVm Row(AppIdentity app)
    {
        var key = app.Path.Length > 0 ? app.Path : "?" + app.Name;
        if (!_appRows.TryGetValue(key, out var row)) _appRows[key] = row = new AppRowVm(app);
        return row;
    }

    private void RefreshConnections()
    {
        var sockets = NetNative.Sockets();
        int established = 0;
        var keys = new HashSet<FlowKey>();
        foreach (var s in sockets)
        {
            if (s.Udp || s.State == TcpState.Listen) continue;
            if (IPAddress.IsLoopback(s.RemoteIp) || s.RemoteIp.Equals(IPAddress.Any) || s.RemoteIp.Equals(IPAddress.IPv6Any)) continue;
            if (s.State == TcpState.Established) established++;
            var key = new FlowKey(s.Pid, Normalize(s.RemoteIp), s.RemotePort, false);
            keys.Add(key);
            var row = ConnRow(key);
            row.State = StateText(s.State);
        }
        foreach (var (key, flow) in _mon.Flows)
        {
            var k = key with { Remote = Normalize(key.Remote) };
            if (!keys.Add(k) && !_connRows.ContainsKey(k)) continue;
            var row = ConnRow(k);
            if (k.Udp) row.State = "";
        }

        var rows = new List<ConnRowVm>();
        foreach (var k in keys)
        {
            var row = ConnRow(k);
            var flow = _mon.Flows.TryGetValue(k, out var f) ? f : _mon.Flows.TryGetValue(k with { Remote = MapV6(k.Remote) }, out var f6) ? f6 : null;
            row.RateDown = flow is { RateDown: > 0 } ? Format.Speed(flow.RateDown) : "";
            row.RateUp = flow is { RateUp: > 0 } ? Format.Speed(flow.RateUp) : "";
            row.Total = flow != null && flow.TotalDown + flow.TotalUp > 0 ? Format.Bytes(flow.TotalDown + flow.TotalUp) : "";
            row.IsActive = flow != null && DateTime.Now - flow.LastActive < TimeSpan.FromSeconds(10) && flow.TotalDown + flow.TotalUp > 0;
            row.Host = HostNames.Get(k.Remote) ?? (NetNative.IsLocal(k.Remote) ? Loc.T("Network.LocalNet") : "");
            row.SortKey = flow == null ? 0 : (flow.RateDown + flow.RateUp) * 1000 + Math.Min(999, (flow.TotalDown + flow.TotalUp) / 1_000_000);
            if (OnlyActive && !row.IsActive && row.State != StateText(TcpState.Established)) continue;
            if (Search.Length > 0 && !(row.App.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                                        || row.Host.Contains(Search, StringComparison.OrdinalIgnoreCase)
                                        || row.Address.Contains(Search, StringComparison.OrdinalIgnoreCase))) continue;
            rows.Add(row);
        }
        Sync(Connections, rows.OrderByDescending(r => r.SortKey).ThenBy(r => r.App.Name).Take(300).ToList());

        // Offene Ports: hier kann von außen etwas hereinkommen
        var ports = sockets.Where(s => s.State == TcpState.Listen && !IPAddress.IsLoopback(s.LocalIp))
            .GroupBy(s => (s.Udp, s.LocalPort, s.Pid))
            .Select(g =>
            {
                bool exposed = g.Any(s => s.LocalIp.Equals(IPAddress.Any) || s.LocalIp.Equals(IPAddress.IPv6Any));
                var scope = exposed ? Loc.T("Network.ScopeAll") : string.Join(", ", g.Select(s => s.LocalIp.ToString()).Distinct());
                return new PortRowVm(AppIdentity.ForPid(g.Key.Pid), g.Key.Udp ? "UDP" : "TCP", g.Key.LocalPort, scope, exposed);
            })
            .Where(p => !p.App.Name.Equals("System Idle", StringComparison.Ordinal))
            .OrderBy(p => p.Protocol).ThenBy(p => p.Port).ToList();
        if (Ports.Count != ports.Count || !Ports.Zip(ports).All(x => x.First.Port == x.Second.Port && x.First.Protocol == x.Second.Protocol))
        {
            Ports.Clear();
            foreach (var p in ports) Ports.Add(p);
        }

        ConnectionsText = established.ToString("N0", Loc.Culture);
        ListeningText = Loc.T("Network.OpenPorts", ports.Count(p => p.Protocol == "TCP"), ports.Count(p => p.Protocol == "UDP"));

        // alte Zeilen vergessen
        foreach (var k in _connRows.Keys.Where(k => !keys.Contains(k)).ToList()) _connRows.Remove(k);
    }

    private ConnRowVm ConnRow(FlowKey k)
    {
        if (!_connRows.TryGetValue(k, out var row)) _connRows[k] = row = new ConnRowVm(AppIdentity.ForPid(k.Pid), k.Remote, k.RemotePort, k.Udp);
        return row;
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    private static IPAddress MapV6(IPAddress ip) => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? ip.MapToIPv6() : ip;

    private static string StateText(TcpState s) => s switch
    {
        TcpState.Established => Loc.T("Network.StateEstablished"),
        TcpState.SynSent or TcpState.SynReceived => Loc.T("Network.StateConnecting"),
        TcpState.TimeWait or TcpState.CloseWait or TcpState.FinWait1 or TcpState.FinWait2 or TcpState.LastAck or TcpState.Closing => Loc.T("Network.StateClosing"),
        _ => s.ToString(),
    };

    /// <summary>Liste an die gewünschte Reihenfolge angleichen, ohne sie neu aufzubauen (Scrollposition bleibt).</summary>
    private static void Sync<T>(ObservableCollection<T> target, IList<T> desired) where T : class
    {
        var want = new HashSet<T>(desired);
        for (int i = target.Count - 1; i >= 0; i--)
            if (!want.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            int idx = target.IndexOf(desired[i]);
            if (idx < 0) target.Insert(i, desired[i]);
            else if (idx != i) target.Move(idx, i);
        }
    }

    partial void OnSearchChanged(string value) => RefreshTables();
    partial void OnOnlyActiveChanged(bool value) => RefreshConnections();
    public bool IsOverview => Tab == "overview";
    public bool IsApps => Tab == "apps";
    public bool IsConnections => Tab == "connections";
    public bool IsDevices => Tab == "devices";
    public bool IsAdapters => Tab == "adapters";

    partial void OnTabChanged(string value)
    {
        if (value == "devices" && Devices.Count == 0 && !IsScanning) ScanCommand.Execute(null); // beim ersten Öffnen gleich scannen
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsApps));
        OnPropertyChanged(nameof(IsConnections));
        OnPropertyChanged(nameof(IsDevices));
        OnPropertyChanged(nameof(IsAdapters));
        if (value == "adapters") RefreshLink();
        RefreshTables();
    }

    // ───────────── Befehle ─────────────

    [RelayCommand]
    private void SetTab(string tab) => Tab = tab;

    [RelayCommand]
    private void SetUsageDays(string days) => UsageDays = int.Parse(days);

    [RelayCommand]
    private void SetOnlyActive(string value) => OnlyActive = value == "True";

    [RelayCommand]
    private async Task ShowPublicIp()
    {
        LoadingPublicIp = true;
        PublicIp = await NetworkInfo.PublicIpAsync() ?? Loc.T("Network.PublicIpFailed");
        LoadingPublicIp = false;
    }

    [RelayCommand]
    private void RefreshAdapters() => RefreshLink();

    [RelayCommand]
    private async Task Scan()
    {
        if (IsScanning)
        {
            _scanCts?.Cancel();
            return;
        }
        IsScanning = true;
        ScanProgress = 0;
        _scanCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p => ScanProgress = p * 100);
            var found = await Task.Run(() => LanScanner.ScanAsync(progress, _scanCts.Token));
            Devices.Clear();
            foreach (var d in found)
            {
                Devices.Add(new DeviceRowVm(
                    d.Name?.Split('.')[0] is { Length: > 0 } n ? n : Loc.T("Network.UnknownDevice"),
                    d.Ip.ToString(),
                    d.Mac ?? "—",
                    d.RandomMac ? Loc.T("Network.RandomMac") : d.Vendor ?? "",
                    d.PingMs is { } ms ? $"{ms} ms" : "",
                    d.IsRouter, d.IsSelf));
            }
            ScanSummary = Loc.T("Network.ScanSummary", found.Count, DateTime.Now.ToString("t", Loc.Culture));
        }
        catch (OperationCanceledException) { /* abgebrochen */ }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void RevealApp(object? row)
    {
        var path = row switch { AppRowVm a => a.App.Path, ConnRowVm c => c.App.Path, PortRowVm p => p.App.Path, _ => "" };
        if (path.Length > 0 && System.IO.File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
