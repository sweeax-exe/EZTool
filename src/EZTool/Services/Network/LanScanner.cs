using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EZTool.Services.Network;

public sealed record LanDevice(IPAddress Ip, string? Name, string? Mac, string? Vendor, int? PingMs, bool IsRouter, bool IsSelf, bool RandomMac);

/// <summary>
/// Findet die Geräte im eigenen Heimnetz: alle Adressen des Subnetzes anpingen, dann die ARP-Tabelle
/// (MAC-Adressen) lesen und die Namen beim Router erfragen. Der Hersteller kommt aus der öffentlichen
/// IEEE-Liste der MAC-Präfixe.
/// </summary>
public static class LanScanner
{
    private const string OuiUrl = "https://standards-oui.ieee.org/oui/oui.csv";
    private static string OuiFile => Path.Combine(SettingsService.Folder, "Network", "oui.csv");
    private static Dictionary<string, string>? _oui;

    public sealed record Subnet(IPAddress Self, IPAddress Gateway, int Prefix, string? SelfMac);

    /// <summary>Das Netz des Adapters, über den der PC ins Internet geht.</summary>
    public static Subnet? CurrentSubnet()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            var gw = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            var addr = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (gw == null || addr == null) continue;
            var mac = nic.GetPhysicalAddress().GetAddressBytes();
            return new Subnet(addr.Address, gw, addr.PrefixLength, mac.Length == 6 ? NetNative.FormatMac(mac) : null);
        }
        return null;
    }

    public static async Task<List<LanDevice>> ScanAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var net = CurrentSubnet();
        if (net == null) return [];
        var vendorTask = LoadOuiAsync();

        // Höchstens ein /24 rund um die eigene Adresse – größere Netze würden zu lange dauern
        int prefix = Math.Max(net.Prefix, 24);
        var self = net.Self.GetAddressBytes();
        uint selfInt = (uint)(self[0] << 24 | self[1] << 16 | self[2] << 8 | self[3]);
        uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint first = (selfInt & mask) + 1, last = (selfInt | ~mask) - 1;
        var targets = new List<IPAddress>();
        for (uint a = first; a <= last && targets.Count < 1024; a++)
            targets.Add(new IPAddress(new[] { (byte)(a >> 24), (byte)(a >> 16), (byte)(a >> 8), (byte)a }));

        var pings = new ConcurrentDictionary<IPAddress, int>();
        int done = 0;
        using var slots = new SemaphoreSlim(64);
        await Task.WhenAll(targets.Select(async ip =>
        {
            await slots.WaitAsync(ct);
            try
            {
                using var p = new Ping();
                var r = await p.SendPingAsync(ip, TimeSpan.FromMilliseconds(800), null, null, ct);
                if (r.Status == IPStatus.Success) pings[ip] = (int)r.RoundtripTime;
            }
            catch { /* nicht erreichbar */ }
            finally
            {
                slots.Release();
                progress?.Report(0.8 * Interlocked.Increment(ref done) / targets.Count);
            }
        }));

        // Auch Geräte, die Pings ignorieren, stehen nach dem Scan meist in der ARP-Tabelle
        var arp = NetNative.ArpTable();
        var all = new HashSet<IPAddress>(pings.Keys);
        foreach (var ip in arp.Keys)
        {
            var b = ip.GetAddressBytes();
            uint v = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            if (v >= first && v <= last) all.Add(ip);
        }
        all.Add(net.Self);

        var names = new ConcurrentDictionary<IPAddress, string>();
        int named = 0;
        await Task.WhenAll(all.Select(async ip =>
        {
            try
            {
                var lookup = Dns.GetHostEntryAsync(ip);
                if (await Task.WhenAny(lookup, Task.Delay(2000, ct)) == lookup && lookup.IsCompletedSuccessfully
                    && lookup.Result.HostName is { Length: > 0 } h && h != ip.ToString())
                {
                    names[ip] = h;
                    HostNames.Remember(ip, h.ToLowerInvariant());
                }
            }
            catch { /* kein Name */ }
            finally { progress?.Report(0.8 + 0.2 * Interlocked.Increment(ref named) / all.Count); }
        }));

        await vendorTask;
        return all.Select(ip =>
        {
            string? mac = ip.Equals(net.Self) ? net.SelfMac : arp.GetValueOrDefault(ip);
            bool random = mac != null && (Convert.ToByte(mac[..2], 16) & 0x02) != 0; // "lokal verwaltet" = zufällige MAC (Handys)
            return new LanDevice(ip, names.GetValueOrDefault(ip), mac, mac != null && !random ? Vendor(mac) : null,
                pings.TryGetValue(ip, out var ms) ? ms : null, ip.Equals(net.Gateway), ip.Equals(net.Self), random);
        })
        .OrderBy(d => d.IsRouter ? 0 : d.IsSelf ? 1 : 2)
        .ThenBy(d => BitConverter.ToUInt32(d.Ip.GetAddressBytes().Reverse().ToArray()))
        .ToList();
    }

    private static string? Vendor(string mac)
    {
        if (_oui == null) return null;
        var key = mac.Replace(":", "")[..6];
        return _oui.GetValueOrDefault(key);
    }

    /// <summary>IEEE-Liste laden (einmal herunterladen, dann 60 Tage aus dem Cache).</summary>
    private static async Task LoadOuiAsync()
    {
        if (_oui != null) return;
        try
        {
            var fresh = File.Exists(OuiFile) && DateTime.Now - File.GetLastWriteTime(OuiFile) < TimeSpan.FromDays(60);
            if (!fresh)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("EZTool");
                var text = await http.GetStringAsync(OuiUrl);
                if (text.Length > 100_000)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(OuiFile)!);
                    await File.WriteAllTextAsync(OuiFile, text);
                }
            }
            if (!File.Exists(OuiFile)) return;
            var map = new Dictionary<string, string>(40000, StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(OuiFile).Skip(1))
            {
                // Registry,Assignment,"Organization Name",Address
                var parts = SplitCsv(line);
                if (parts.Count >= 3 && parts[1].Length == 6) map[parts[1]] = parts[2].Trim();
            }
            _oui = map;
        }
        catch { /* ohne Hersteller weiter */ }
    }

    private static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                res.Add(cur.ToString());
                cur.Clear();
                if (res.Count == 3) return res; // die Adresse brauchen wir nicht
            }
            else cur.Append(c);
        }
        res.Add(cur.ToString());
        return res;
    }
}
