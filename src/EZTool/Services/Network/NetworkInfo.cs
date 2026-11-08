using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Windows.Networking.Connectivity;

namespace EZTool.Services.Network;

public sealed record AdapterInfo(
    string Id, string Name, string Description, string Type, bool IsUp, long SpeedBps, string? Mac,
    IReadOnlyList<string> IPv4, IReadOnlyList<string> IPv6, IReadOnlyList<string> Gateways, IReadOnlyList<string> Dns,
    bool Dhcp, string? DhcpServer, string? DnsSuffix, long BytesReceived, long BytesSent, WifiInfo? Wifi);

public sealed record AppUsage(string Key, string Name, string? Path, long Received, long Sent);

public static class NetworkInfo
{
    /// <summary>Alle echten Netzwerkadapter (ohne Loopback), verbundene zuerst.</summary>
    public static List<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            // Filtertreiber-Instanzen ("Ethernet-QoS Packet Scheduler-0000", "WLAN-WFP …-0000") sind keine eigenen Adapter
            if (System.Text.RegularExpressions.Regex.IsMatch(n.Name, @"-\d{4}$")) continue;
            try
            {
                var p = n.GetIPProperties();
                bool up = n.OperationalStatus == OperationalStatus.Up;
                // Virtuelle Hilfsadapter ohne Verbindung (z. B. "Teredo", "isatap") weglassen
                if (!up && n.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Unknown) continue;
                var mac = n.GetPhysicalAddress().GetAddressBytes();
                var v4 = p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => $"{a.Address}/{a.PrefixLength}").ToList();
                var v6 = p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(a => a.Address.ToString()).ToList();
                bool dhcp = false;
                string? dhcpServer = null;
                try
                {
                    var p4 = p.GetIPv4Properties();
                    dhcp = p4?.IsDhcpEnabled ?? false;
                    dhcpServer = p.DhcpServerAddresses.FirstOrDefault()?.ToString();
                }
                catch { /* kein IPv4 */ }
                long rx = 0, tx = 0;
                try
                {
                    var st = n.GetIPStatistics();
                    rx = st.BytesReceived;
                    tx = st.BytesSent;
                }
                catch { /* egal */ }

                list.Add(new AdapterInfo(
                    n.Id, n.Name, n.Description, TypeName(n.NetworkInterfaceType), up, up ? n.Speed : 0,
                    mac.Length == 6 ? NetNative.FormatMac(mac) : null,
                    v4, v6,
                    p.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any)).Select(a => a.ToString()).ToList(),
                    p.DnsAddresses.Select(a => a.ToString()).ToList(),
                    dhcp, dhcpServer, string.IsNullOrEmpty(p.DnsSuffix) ? null : p.DnsSuffix, rx, tx,
                    n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && up ? NetNative.Wifi(n.Id) : null));
            }
            catch { /* Adapter verschwindet gerade */ }
        }
        return list.OrderByDescending(a => a.IsUp).ThenByDescending(a => a.Gateways.Count > 0).ThenBy(a => a.Name).ToList();
    }

    private static string TypeName(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
        NetworkInterfaceType.Ppp => "VPN / PPP",
        NetworkInterfaceType.Tunnel => "Tunnel",
        NetworkInterfaceType.Wman or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobile",
        _ => t.ToString(),
    };

    /// <summary>
    /// Datenverbrauch pro App aus der Windows-Statistik (dieselben Zahlen wie unter Einstellungen → Netzwerk → Datennutzung).
    /// Summiert über alle bekannten Netzwerke.
    /// </summary>
    public static async Task<List<AppUsage>> UsageAsync(DateTime from)
    {
        var sum = new Dictionary<string, AppUsage>(StringComparer.OrdinalIgnoreCase);
        var states = new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };
        foreach (var profile in NetworkInformation.GetConnectionProfiles())
        {
            try
            {
                var usage = await profile.GetAttributedNetworkUsageAsync(new DateTimeOffset(from), DateTimeOffset.Now, states);
                foreach (var u in usage)
                {
                    string? path = u.AttributionId.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase) ? NetNative.DevicePathToDos(u.AttributionId) : null;
                    string key = path ?? u.AttributionId;
                    string name = !string.IsNullOrWhiteSpace(u.AttributionName) ? u.AttributionName
                        : path != null ? System.IO.Path.GetFileNameWithoutExtension(path) : u.AttributionId;
                    sum[key] = sum.TryGetValue(key, out var prev)
                        ? prev with { Received = prev.Received + (long)u.BytesReceived, Sent = prev.Sent + (long)u.BytesSent }
                        : new AppUsage(key, name, path, (long)u.BytesReceived, (long)u.BytesSent);
                }
            }
            catch { /* Profil ohne Statistik */ }
        }
        return sum.Values.OrderByDescending(a => a.Received + a.Sent).ToList();
    }

    /// <summary>Öffentliche IPv4-Adresse – fragt dafür einmal bei api.ipify.org nach (nur auf Knopfdruck).</summary>
    public static async Task<string?> PublicIpAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
            return IPAddress.TryParse(ip, out _) ? ip : null;
        }
        catch
        {
            return null;
        }
    }
}
