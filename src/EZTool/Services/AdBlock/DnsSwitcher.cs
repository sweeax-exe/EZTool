using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

namespace EZTool.Services.AdBlock;

/// <summary>
/// Stellt die DNS-Server der Netzwerkadapter auf EZTool um und wieder zurück.
/// Vor jeder Änderung wird der Originalzustand in eine Datei gesichert – auch nach einem Absturz
/// kann EZTool beim nächsten Start (oder "EZTool.exe --restore-dns") alles zurücksetzen.
/// </summary>
public static class DnsSwitcher
{
    public sealed class AdapterBackup
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Fest eingetragene Server vor der Änderung. Leer = automatisch (DHCP / Router).</summary>
        public List<string> StaticV4 { get; set; } = [];
        public List<string> StaticV6 { get; set; } = [];
        /// <summary>Server, die vorher tatsächlich benutzt wurden (meist der Router) – für lokale Namen wie fritz.box.</summary>
        public List<string> Effective { get; set; } = [];
        public string DnsSuffix { get; set; } = "";
    }

    private static string BackupFile => Path.Combine(SettingsService.Folder, "AdBlock", "dns-backup.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static bool HasBackup => File.Exists(BackupFile);

    public static List<AdapterBackup> LoadBackup()
    {
        try
        {
            if (File.Exists(BackupFile)) return JsonSerializer.Deserialize<List<AdapterBackup>>(File.ReadAllText(BackupFile)) ?? [];
        }
        catch { /* defekt → leer */ }
        return [];
    }

    private static void SaveBackup(List<AdapterBackup> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
        var tmp = BackupFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, BackupFile, true);
    }

    /// <summary>Adapter, über die der PC ins Internet geht (Ethernet/WLAN mit Gateway). VPN-Adapter bleiben unangetastet.</summary>
    private static IEnumerable<NetworkInterface> InternetAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n =>
            n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit
            && n.Supports(NetworkInterfaceComponent.IPv4)
            && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)));

    /// <summary>
    /// Leitet alle Internet-Adapter auf 127.0.0.1 / ::1 um. Adapter, die schon umgestellt sind, werden übersprungen,
    /// neu verbundene (z. B. WLAN nach LAN) kommen dazu. Gibt die Anzahl der umgestellten Adapter zurück.
    /// </summary>
    public static int Apply()
    {
        lock (Gate)
        {
            var backup = LoadBackup();
            int changed = 0;
            foreach (var nic in InternetAdapters())
            {
                var props = nic.GetIPProperties();
                bool alreadyOurs = props.DnsAddresses.Any(IPAddress.IsLoopback);
                if (!backup.Any(b => b.Id == nic.Id))
                {
                    // Steht schon auf 127.0.0.1, aber ohne Sicherung (z. B. Datei gelöscht)? Dann ist "automatisch" der sichere Rückweg.
                    backup.Add(new AdapterBackup
                    {
                        Id = nic.Id,
                        Name = nic.Name,
                        StaticV4 = alreadyOurs ? [] : ReadStatic(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + nic.Id),
                        StaticV6 = alreadyOurs ? [] : ReadStatic(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\" + nic.Id),
                        Effective = props.DnsAddresses.Where(a => !IPAddress.IsLoopback(a)).Select(a => a.ToString()).ToList(),
                        DnsSuffix = props.DnsSuffix ?? "",
                    });
                    SaveBackup(backup); // erst sichern, dann ändern
                }
                if (alreadyOurs && props.DnsAddresses.All(IPAddress.IsLoopback)) continue;

                Netsh($"interface ipv4 set dnsservers name=\"{nic.Name}\" source=static address=127.0.0.1 register=primary validate=no");
                if (nic.Supports(NetworkInterfaceComponent.IPv6))
                    Netsh($"interface ipv6 set dnsservers name=\"{nic.Name}\" source=static address=::1 register=primary validate=no");
                changed++;
            }
            if (changed > 0) FlushCache();
            return changed;
        }
    }

    /// <summary>Stellt alle gesicherten Adapter auf ihren Originalzustand zurück und löscht die Sicherung.</summary>
    public static void Restore()
    {
        lock (Gate)
        {
            var backup = LoadBackup();
            if (backup.Count == 0)
            {
                TryDelete();
                return;
            }
            var names = NetworkInterface.GetAllNetworkInterfaces().ToDictionary(n => n.Id, n => n.Name);
            foreach (var b in backup)
            {
                var name = names.TryGetValue(b.Id, out var current) ? current : b.Name; // Adapter kann umbenannt worden sein
                RestoreFamily("ipv4", name, b.StaticV4);
                RestoreFamily("ipv6", name, b.StaticV6);
            }
            FlushCache();
            TryDelete();
        }
    }

    private static void RestoreFamily(string family, string name, List<string> servers)
    {
        if (servers.Count == 0)
        {
            Netsh($"interface {family} set dnsservers name=\"{name}\" source=dhcp");
            return;
        }
        Netsh($"interface {family} set dnsservers name=\"{name}\" source=static address={servers[0]} register=primary validate=no");
        for (int i = 1; i < servers.Count; i++)
            Netsh($"interface {family} add dnsservers name=\"{name}\" address={servers[i]} index={i + 1} validate=no");
    }

    private static void TryDelete()
    {
        try { File.Delete(BackupFile); } catch { /* egal */ }
    }

    /// <summary>Die DNS-Server, die vor der Umstellung galten (für lokale Namen). IPv4 zuerst.</summary>
    public static List<IPAddress> LocalResolvers()
    {
        var list = new List<IPAddress>();
        foreach (var b in LoadBackup())
        foreach (var s in b.Effective)
            if (IPAddress.TryParse(s, out var ip) && !IPAddress.IsLoopback(ip) && !list.Contains(ip)) list.Add(ip);
        return list.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList();
    }

    public static List<string> LocalSuffixes() =>
        LoadBackup().Select(b => b.DnsSuffix.Trim('.').ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList();

    private static List<string> ReadStatic(string key)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            var v = k?.GetValue("NameServer") as string;
            return string.IsNullOrWhiteSpace(v) ? [] : v.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Where(s => IPAddress.TryParse(s, out var ip) && !IPAddress.IsLoopback(ip)).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Windows-DNS-Cache leeren, damit Sperren und Freigaben sofort gelten.</summary>
    public static void FlushCache() => Run("ipconfig", "/flushdns");

    private static void Netsh(string args) => Run("netsh", args);

    private static void Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
        }
        catch { /* einzelner Adapter darf den Rest nicht aufhalten */ }
    }
}
