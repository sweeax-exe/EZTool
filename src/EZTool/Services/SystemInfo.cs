using System.Management;
using Microsoft.Win32;

namespace EZTool.Services;

/// <summary>Statische Systeminformationen (einmalig per WMI/Registry gelesen).</summary>
public sealed class SystemInfo
{
    public static SystemInfo? Current { get; private set; }

    public string ComputerName { get; init; } = Environment.MachineName;
    public string UserName { get; init; } = Environment.UserName;
    public string OsName { get; init; } = "Windows";
    public string OsBuild { get; init; } = "";
    public string Motherboard { get; init; } = "—";
    public string CpuName { get; init; } = "—";
    public int Cores { get; init; }
    public int Threads { get; init; }
    public string GpuName { get; init; } = "—";
    public string RamInfo { get; init; } = "—";
    public string BiosVersion { get; init; } = "—";

    public static Task<SystemInfo> LoadAsync() => Task.Run(() =>
    {
        var info = new SystemInfo
        {
            OsName = Try(ReadOsName, "Windows"),
            OsBuild = Try(ReadBuild, ""),
            Motherboard = Try(() => Wmi("Win32_BaseBoard", o => $"{o["Manufacturer"]} {o["Product"]}".Trim()), "—"),
            CpuName = Try(() => Wmi("Win32_Processor", o => (o["Name"]?.ToString() ?? "").Trim()), "—"),
            Cores = Try(() => int.Parse(Wmi("Win32_Processor", o => o["NumberOfCores"]?.ToString() ?? "0")), 0),
            Threads = Environment.ProcessorCount,
            GpuName = Try(() => string.Join(" + ", WmiAll("Win32_VideoController", o => o["Name"]?.ToString() ?? "")
                .Where(n => n.Length > 0 && !n.Contains("Basic", StringComparison.OrdinalIgnoreCase))), "—"),
            RamInfo = Try(ReadRam, "—"),
            BiosVersion = Try(() => Wmi("Win32_BIOS", o => o["SMBIOSBIOSVersion"]?.ToString() ?? ""), "—"),
        };
        Current = info;
        return info;
    });

    private static string ReadOsName()
    {
        var name = Wmi("Win32_OperatingSystem", o => o["Caption"]?.ToString() ?? "Windows").Replace("Microsoft ", "");
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var display = key?.GetValue("DisplayVersion") as string;
        return display != null ? $"{name} · {display}" : name;
    }

    private static string ReadBuild()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return $"Build {key?.GetValue("CurrentBuild")}.{key?.GetValue("UBR")}";
    }

    private static string ReadRam()
    {
        var modules = WmiAll("Win32_PhysicalMemory", o => (
            Capacity: Convert.ToUInt64(o["Capacity"] ?? 0UL),
            Speed: Convert.ToUInt32(o["ConfiguredClockSpeed"] ?? o["Speed"] ?? 0u))).ToList();
        if (modules.Count == 0) return "—";
        var total = modules.Sum(m => (double)m.Capacity);
        var speed = modules.Max(m => m.Speed);
        return $"{Core.Format.Bytes(total)} · {modules.Count}× Modul" + (speed > 0 ? $" · {speed} MT/s" : "");
    }

    private static T Wmi<T>(string cls, Func<ManagementBaseObject, T> sel) => WmiAll(cls, sel).First();

    private static IEnumerable<T> WmiAll<T>(string cls, Func<ManagementBaseObject, T> sel)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT * FROM {cls}");
        using var results = searcher.Get();
        return results.Cast<ManagementBaseObject>().Select(sel).ToList();
    }

    private static T Try<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }
}
