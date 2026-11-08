using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Services;
using LibreHardwareMonitor.Hardware;

namespace EZTool.Modules.Hardware;

public sealed class HardwareModule : IModule
{
    public string Id => "hardware";
    public string Title => Loc.T("Hardware.Title");
    public string Subtitle => Loc.T("Hardware.Subtitle");
    public string Icon => "";
    public int Order => 30;

    public FrameworkElement CreateView() => new HardwareView { DataContext = new HardwareViewModel() };
}

public sealed partial class HardwareViewModel : ObservableObject
{
    public ObservableCollection<HardwareNodeVm> Nodes { get; } = [];

    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private int _sensorCount;

    private string _signature = "";

    public HardwareViewModel()
    {
        HardwareService.Instance.Updated += Apply;
        if (HardwareService.Instance.Latest is { } s) Apply(s);
    }

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSearchChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void SetFilter(string f) => Filter = f;

    private void Apply(HardwareSnapshot s)
    {
        var signature = string.Join("|", s.Nodes.Select(n => n.Id + ":" + n.Sensors.Count));
        if (signature != _signature)
        {
            _signature = signature;
            Nodes.Clear();
            foreach (var node in s.Nodes.Where(n => n.Sensors.Count > 0)) Nodes.Add(new HardwareNodeVm(node));
            SensorCount = s.Nodes.Sum(n => n.Sensors.Count);
            ApplyFilter();
        }

        var byId = new Dictionary<string, SensorReading>();
        foreach (var r in s.Nodes.SelectMany(n => n.Sensors)) byId.TryAdd(r.Id, r);
        foreach (var node in Nodes)
        foreach (var group in node.Groups)
        foreach (var sensor in group.Sensors)
            if (byId.TryGetValue(sensor.Id, out var r)) sensor.Update(r);

        IsLoading = Nodes.Count == 0;
    }

    private void ApplyFilter()
    {
        var q = Search.Trim();
        foreach (var node in Nodes)
        {
            bool anyNode = false;
            foreach (var g in node.Groups)
            {
                bool typeOk = Filter switch
                {
                    "temp" => g.Type == SensorType.Temperature,
                    "load" => g.Type is SensorType.Load or SensorType.Level,
                    "clock" => g.Type is SensorType.Clock or SensorType.Frequency,
                    "power" => g.Type is SensorType.Power or SensorType.Voltage or SensorType.Current or SensorType.Energy,
                    "fan" => g.Type is SensorType.Fan or SensorType.Control,
                    _ => true
                };
                bool anyVisible = false;
                foreach (var s in g.Sensors)
                {
                    s.IsVisible = typeOk && (q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                           || node.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
                    anyVisible |= s.IsVisible;
                }
                g.IsVisible = anyVisible;
                anyNode |= anyVisible;
            }
            node.IsVisible = anyNode;
        }
    }
}

public sealed partial class HardwareNodeVm : ObservableObject
{
    public string Name { get; }
    public string TypeLabel { get; }
    public string Icon { get; }
    public List<SensorGroupVm> Groups { get; }

    [ObservableProperty] private bool _isVisible = true;

    public HardwareNodeVm(HardwareNode node)
    {
        Name = node.Name;
        (TypeLabel, Icon) = node.Type switch
        {
            HardwareType.Cpu => (Loc.T("Hardware.Type.Cpu"), ""),
            HardwareType.GpuNvidia => (Loc.T("Hardware.Type.GpuNvidia"), ""),
            HardwareType.GpuAmd => (Loc.T("Hardware.Type.GpuAmd"), ""),
            HardwareType.GpuIntel => (Loc.T("Hardware.Type.GpuIntel"), ""),
            HardwareType.Memory => (Loc.T("Hardware.Type.Memory"), ""),
            HardwareType.Motherboard => (Loc.T("Hardware.Type.Motherboard"), ""),
            HardwareType.SuperIO => (Loc.T("Hardware.Type.SuperIO"), ""),
            HardwareType.EmbeddedController => (Loc.T("Hardware.Type.EmbeddedController"), ""),
            HardwareType.Storage => (Loc.T("Hardware.Type.Storage"), ""),
            HardwareType.Network => (Loc.T("Hardware.Type.Network"), ""),
            HardwareType.Cooler => (Loc.T("Hardware.Type.Cooler"), ""),
            HardwareType.Battery => (Loc.T("Hardware.Type.Battery"), ""),
            HardwareType.Psu => (Loc.T("Hardware.Type.Psu"), ""),
            _ => (node.Type.ToString(), "")
        };

        Groups = node.Sensors
            .GroupBy(s => s.Type)
            .OrderBy(g => GroupOrder(g.Key))
            .Select(g => new SensorGroupVm(g.Key, g.Select(s => new SensorVm(s)).ToList()))
            .ToList();
    }

    private static int GroupOrder(SensorType t) => t switch
    {
        SensorType.Temperature => 0,
        SensorType.Load => 1,
        SensorType.Clock => 2,
        SensorType.Power => 3,
        SensorType.Fan => 4,
        SensorType.Control => 5,
        SensorType.Voltage => 6,
        SensorType.Current => 7,
        _ => 10
    };
}

public sealed partial class SensorGroupVm : ObservableObject
{
    public SensorType Type { get; }
    public string Title { get; }
    public List<SensorVm> Sensors { get; }

    [ObservableProperty] private bool _isVisible = true;

    public SensorGroupVm(SensorType type, List<SensorVm> sensors)
    {
        Type = type;
        Sensors = sensors;
        Title = type switch
        {
            SensorType.Temperature => Loc.T("Hardware.Group.Temperature"),
            SensorType.Load => Loc.T("Hardware.Group.Load"),
            SensorType.Clock => Loc.T("Hardware.Group.Clock"),
            SensorType.Power => Loc.T("Hardware.Group.Power"),
            SensorType.Fan => Loc.T("Hardware.Group.Fan"),
            SensorType.Control => Loc.T("Hardware.Group.Control"),
            SensorType.Voltage => Loc.T("Hardware.Group.Voltage"),
            SensorType.Current => Loc.T("Hardware.Group.Current"),
            SensorType.Data => Loc.T("Hardware.Group.Data"),
            SensorType.SmallData => Loc.T("Hardware.Group.SmallData"),
            SensorType.Throughput => Loc.T("Hardware.Group.Throughput"),
            SensorType.Level => Loc.T("Hardware.Group.Level"),
            SensorType.Factor => Loc.T("Hardware.Group.Factor"),
            SensorType.Frequency => Loc.T("Hardware.Group.Frequency"),
            SensorType.Energy => Loc.T("Hardware.Group.Energy"),
            SensorType.TimeSpan => Loc.T("Hardware.Group.TimeSpan"),
            _ => type.ToString()
        };
    }
}

public sealed partial class SensorVm : ObservableObject
{
    private static CultureInfo De => Loc.Culture;

    public string Id { get; }
    public string Name { get; }
    public SensorType Type { get; }
    public bool HasBar { get; }

    [ObservableProperty] private string _valueText = "—";
    [ObservableProperty] private string _rangeText = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private double _rawValue;
    [ObservableProperty] private bool _isVisible = true;

    public SensorVm(SensorReading r)
    {
        Id = r.Id;
        Name = r.Name;
        Type = r.Type;
        HasBar = r.Type is SensorType.Load or SensorType.Level or SensorType.Control or SensorType.Temperature;
        Update(r);
    }

    public void Update(SensorReading r)
    {
        ValueText = Fmt(r.Value);
        RangeText = r.Min.HasValue && r.Max.HasValue ? Loc.T("Hardware.Range", Fmt(r.Min), Fmt(r.Max)) : "";
        RawValue = r.Value ?? 0;
        Percent = Math.Clamp(RawValue, 0, 100);
    }

    private string Fmt(float? v)
    {
        if (v is not { } x || float.IsNaN(x)) return "—";
        return Type switch
        {
            SensorType.Temperature => x.ToString("0.0", De) + " °C",
            SensorType.Load or SensorType.Control or SensorType.Level or SensorType.Humidity => x.ToString("0.0", De) + " %",
            SensorType.Clock => x.ToString("0", De) + " MHz",
            SensorType.Power => x.ToString("0.0", De) + " W",
            SensorType.Voltage => x.ToString("0.000", De) + " V",
            SensorType.Current => x.ToString("0.000", De) + " A",
            SensorType.Fan => x.ToString("0", De) + " RPM",
            SensorType.Flow => x.ToString("0.0", De) + " L/h",
            SensorType.Data => x.ToString("0.0", De) + " GB",
            SensorType.SmallData => x.ToString("0", De) + " MB",
            SensorType.Throughput => Format.Speed(x),
            SensorType.Frequency => x.ToString("0", De) + " Hz",
            SensorType.Energy => x.ToString("0", De) + " mWh",
            SensorType.Noise => x.ToString("0", De) + " dBA",
            SensorType.TimeSpan => TimeSpan.FromSeconds(x).ToString(@"hh\:mm\:ss"),
            _ => x.ToString("0.00", De)
        };
    }
}
