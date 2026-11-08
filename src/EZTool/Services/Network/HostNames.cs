using System.Collections.Concurrent;
using System.Management;
using System.Net;

namespace EZTool.Services.Network;

/// <summary>
/// Findet zu einer IP-Adresse den Namen, unter dem sie aufgerufen wurde ("Discord → gateway.discord.gg").
/// Quelle 1 ist der DNS-Cache von Windows (was Programme zuletzt nachgeschlagen haben),
/// Quelle 2 eine Rückwärtssuche (PTR) für alles andere.
/// </summary>
public static class HostNames
{
    private static readonly ConcurrentDictionary<IPAddress, string?> Names = new();
    private static readonly ConcurrentDictionary<IPAddress, byte> Pending = new();
    private static readonly SemaphoreSlim PtrSlots = new(6);
    private static DateTime _cacheRead;
    private static int _reading;

    /// <summary>Bekannter Name oder null. Stößt bei Bedarf die Suche im Hintergrund an.</summary>
    public static string? Get(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        RefreshCacheIfOld();
        if (Names.TryGetValue(ip, out var name)) return name;
        if (Pending.TryAdd(ip, 0)) _ = ReverseAsync(ip);
        return null;
    }

    private static void RefreshCacheIfOld()
    {
        if (DateTime.UtcNow - _cacheRead < TimeSpan.FromSeconds(15) || Interlocked.Exchange(ref _reading, 1) == 1) return;
        _cacheRead = DateTime.UtcNow;
        Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Entry, Data, Type FROM MSFT_DNSClientCache");
                foreach (ManagementObject o in searcher.Get())
                {
                    using (o)
                    {
                        var type = Convert.ToInt32(o["Type"]);
                        if (type is not (1 or 28)) continue; // A / AAAA
                        if (o["Data"] is string data && IPAddress.TryParse(data, out var ip) && o["Entry"] is string entry && entry.Length > 0)
                            Names[ip] = entry.ToLowerInvariant(); // DNS-Cache schlägt eine evtl. schon vorhandene PTR-Antwort
                    }
                }
            }
            catch { /* WMI nicht verfügbar */ }
            finally { Interlocked.Exchange(ref _reading, 0); }
        });
    }

    private static async Task ReverseAsync(IPAddress ip)
    {
        await PtrSlots.WaitAsync();
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(2500)) == lookup && lookup.IsCompletedSuccessfully)
            {
                var host = lookup.Result.HostName;
                Names.TryAdd(ip, string.IsNullOrEmpty(host) || host == ip.ToString() ? null : host.ToLowerInvariant());
            }
            else Names.TryAdd(ip, null);
        }
        catch
        {
            Names.TryAdd(ip, null);
        }
        finally
        {
            PtrSlots.Release();
            Pending.TryRemove(ip, out _);
        }
    }

    /// <summary>Für den Geräte-Scan: Namen direkt eintragen (z. B. vom Router).</summary>
    public static void Remember(IPAddress ip, string name) => Names[ip] = name;
}
