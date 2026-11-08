using System.Management;
using EZTool.Core;
using LibreHardwareMonitor.Hardware;

namespace EZTool.Services;

public sealed class DiskHealth
{
    public int Number { get; init; }
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
    public string Kind { get; init; } = "";
    public long Size { get; init; }
    public string Letters { get; init; } = "";

    /// <summary>0 = gut, 1 = Warnung, 2 = kritisch, sonst unbekannt.</summary>
    public int Status { get; init; } = -1;
    public string StatusText => Status switch { 0 => Loc.T("Disks.Status.Healthy"), 1 => Loc.T("Disks.Status.Warning"), 2 => Loc.T("Disks.Status.Critical"), _ => Loc.T("Disks.Status.Unknown") };

    public double? RemainingLife { get; init; }
    public double? Temperature { get; init; }
    public double? TemperatureMax { get; init; }
    public long? PowerOnHours { get; init; }
    public double? WrittenTb { get; init; }
    public double? ReadTb { get; init; }
    public long? ReadErrors { get; init; }
    public long? WriteErrors { get; init; }

    public double LifeGauge => RemainingLife ?? 0;
    public string LifeText => RemainingLife is { } r ? $"{r:0}" : "—";
    public string TempText => Temperature is { } t ? $"{t:0} °C" + (TemperatureMax is { } m and > 0 ? Loc.T("Disks.TempMax", m) : "") : "—";
    public string HoursText => PowerOnHours is { } h ? Loc.T("Disks.Hours", h) + (h > 24 * 60 ? Loc.T("Disks.Years", h / 24.0 / 365) : "") : "—";
    public string WrittenText => WrittenTb is { } w ? $"{w:0.00} TB" : "—";
    public string ReadText => ReadTb is { } r ? $"{r:0.00} TB" : "—";
    public string ErrorsText => ReadErrors is null && WriteErrors is null ? "—" : $"{ReadErrors ?? 0:N0} / {WriteErrors ?? 0:N0}";
    public bool HasErrors => (ReadErrors ?? 0) + (WriteErrors ?? 0) > 0;
}

/// <summary>Gesundheitsdaten aller physischen Datenträger (Windows-Speicherverwaltung + LibreHardwareMonitor).</summary>
public static class DiskHealthService
{
    private static readonly ManagementScope Scope = new(@"\\.\root\Microsoft\Windows\Storage");

    public static List<DiskHealth> Load()
    {
        var result = new List<DiskHealth>();
        var letters = ReadLetters();
        var lhm = HardwareService.Instance.Latest?.Nodes.Where(n => n.Type == HardwareType.Storage).ToList() ?? [];

        using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
        foreach (ManagementObject disk in searcher.Get())
        {
            using (disk)
            {
                try
                {
                    int number = int.TryParse(disk["DeviceId"]?.ToString(), out var n) ? n : -1;
                    var model = (disk["FriendlyName"]?.ToString() ?? Loc.T("Disks.Kind.Generic")).Trim();
                    var media = Convert.ToInt32(disk["MediaType"] ?? 0);
                    var bus = Convert.ToInt32(disk["BusType"] ?? 0);
                    var kind = bus == 17 ? Loc.T("Disks.Kind.Nvme") : media == 4 ? "SSD" : media == 3 ? Loc.T("Disks.Kind.Hdd") : bus == 7 ? "USB" : Loc.T("Disks.Kind.Generic");
                    bool isSsd = bus == 17 || media == 4;

                    // Zuverlässigkeitszähler (benötigt Admin-Rechte)
                    double? wear = null, temp = null, tempMax = null;
                    long? hours = null, rErr = null, wErr = null;
                    try
                    {
                        foreach (ManagementObject rc in disk.GetRelated("MSFT_StorageReliabilityCounter"))
                        {
                            using (rc)
                            {
                                wear = Num(rc["Wear"]);
                                temp = Num(rc["Temperature"]);
                                tempMax = Num(rc["TemperatureMax"]);
                                hours = (long?)Num(rc["PowerOnHours"]);
                                rErr = (long?)Num(rc["ReadErrorsTotal"]);
                                wErr = (long?)Num(rc["WriteErrorsTotal"]);
                            }
                        }
                    }
                    catch { /* ohne Admin-Rechte nicht verfügbar */ }

                    // Ergänzung aus LibreHardwareMonitor (gleicher Modellname)
                    var node = lhm.FirstOrDefault(h => h.Name.Equals(model, StringComparison.OrdinalIgnoreCase))
                               ?? lhm.FirstOrDefault(h => h.Name.Contains(model, StringComparison.OrdinalIgnoreCase) || model.Contains(h.Name, StringComparison.OrdinalIgnoreCase));
                    double? Sensor(SensorType t, params string[] names) =>
                        node?.Sensors.FirstOrDefault(s => s.Type == t && names.Any(x => s.Name.Equals(x, StringComparison.OrdinalIgnoreCase)))?.Value;

                    var lifeLhm = Sensor(SensorType.Level, "Remaining Life");
                    var usedLhm = Sensor(SensorType.Level, "Percentage Used");
                    double? remaining = lifeLhm ?? (usedLhm != null ? 100 - usedLhm : null) ?? (wear is > 0 ? 100 - wear : null)
                                        ?? (isSsd && wear == 0 ? 100 : null);
                    temp ??= Sensor(SensorType.Temperature, "Temperature", "Composite Temperature");
                    if (temp is 0) temp = Sensor(SensorType.Temperature, "Temperature", "Composite Temperature");

                    result.Add(new DiskHealth
                    {
                        Number = number,
                        Model = model,
                        Serial = disk["SerialNumber"]?.ToString()?.Trim() ?? "",
                        Kind = kind,
                        Size = (long)Convert.ToUInt64(disk["Size"] ?? 0UL),
                        Letters = letters.TryGetValue(number, out var l) ? string.Join("  ", l.Order()) : "",
                        Status = Convert.ToInt32(disk["HealthStatus"] ?? 5),
                        RemainingLife = remaining is { } r ? Math.Clamp(r, 0, 100) : null,
                        Temperature = temp is > 0 ? temp : null,
                        TemperatureMax = tempMax,
                        PowerOnHours = hours is > 0 ? hours : null,
                        WrittenTb = Sensor(SensorType.Data, "Data Written") is { } w ? w / 1024 : null,
                        ReadTb = Sensor(SensorType.Data, "Data Read") is { } rd ? rd / 1024 : null,
                        ReadErrors = rErr,
                        WriteErrors = wErr,
                    });
                }
                catch { /* einzelner Datenträger nicht lesbar */ }
            }
        }
        return result.OrderBy(d => d.Number).ToList();
    }

    private static double? Num(object? o)
    {
        try { return o == null ? null : Convert.ToDouble(o); }
        catch { return null; }
    }

    private static Dictionary<int, List<string>> ReadLetters()
    {
        var map = new Dictionary<int, List<string>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, new ObjectQuery("SELECT DiskNumber, DriveLetter FROM MSFT_Partition"));
            foreach (ManagementObject p in searcher.Get())
            {
                using (p)
                {
                    var letter = Convert.ToChar(p["DriveLetter"] ?? '\0');
                    if (letter == '\0' || !char.IsLetter(letter)) continue;
                    var disk = Convert.ToInt32(p["DiskNumber"]);
                    if (!map.TryGetValue(disk, out var l)) map[disk] = l = [];
                    l.Add(letter + ":");
                }
            }
        }
        catch { /* egal */ }
        return map;
    }
}
