using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace EZTool.Services.Network;

public enum TcpState { Unknown = 0, Closed = 1, Listen = 2, SynSent = 3, SynReceived = 4, Established = 5, FinWait1 = 6, FinWait2 = 7, CloseWait = 8, Closing = 9, LastAck = 10, TimeWait = 11, DeleteTcb = 12 }

public sealed record SocketRow(bool Udp, IPAddress LocalIp, int LocalPort, IPAddress RemoteIp, int RemotePort, TcpState State, int Pid);

public sealed record WifiInfo(string Ssid, string Bssid, int SignalPercent, double RxMbps, double TxMbps, int Channel);

/// <summary>Windows-Netzwerk-APIs: Verbindungstabellen (wie netstat), ARP-Tabelle, WLAN-Daten, Laufwerkspfade.</summary>
public static class NetNative
{
    private const int AfInet = 2, AfInet6 = 23;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpNetTable(IntPtr table, ref int size, bool order);

    private static IntPtr Fetch(Func<IntPtr, int, (uint Result, int Size)> call)
    {
        int size = 0;
        var (_, needed) = call(IntPtr.Zero, size);
        size = needed;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buf = Marshal.AllocHGlobal(Math.Max(size, 4) + 4096);
            var (res, again) = call(buf, Math.Max(size, 4) + 4096);
            if (res == 0) return buf;
            Marshal.FreeHGlobal(buf);
            size = again;
        }
        return IntPtr.Zero;
    }

    /// <summary>Alle TCP-Verbindungen und UDP-Ports mit zugehörigem Prozess.</summary>
    public static List<SocketRow> Sockets()
    {
        var list = new List<SocketRow>(512);
        foreach (int af in new[] { AfInet, AfInet6 })
        {
            // TCP_TABLE_OWNER_PID_ALL = 5
            var tcp = Fetch((p, s) => { int sz = s; var r = GetExtendedTcpTable(p, ref sz, false, af, 5, 0); return (r, sz); });
            if (tcp != IntPtr.Zero)
            {
                try
                {
                    int n = Marshal.ReadInt32(tcp);
                    int rowSize = af == AfInet ? 24 : 56;
                    for (int i = 0; i < n; i++)
                    {
                        var row = tcp + 4 + i * rowSize;
                        if (af == AfInet)
                        {
                            list.Add(new SocketRow(false,
                                new IPAddress((uint)Marshal.ReadInt32(row + 4)), Port(row + 8),
                                new IPAddress((uint)Marshal.ReadInt32(row + 12)), Port(row + 16),
                                (TcpState)Marshal.ReadInt32(row), Marshal.ReadInt32(row + 20)));
                        }
                        else
                        {
                            list.Add(new SocketRow(false,
                                V6(row, Marshal.ReadInt32(row + 16)), Port(row + 20),
                                V6(row + 24, Marshal.ReadInt32(row + 40)), Port(row + 44),
                                (TcpState)Marshal.ReadInt32(row + 48), Marshal.ReadInt32(row + 52)));
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(tcp); }
            }

            // UDP_TABLE_OWNER_PID = 1
            var udp = Fetch((p, s) => { int sz = s; var r = GetExtendedUdpTable(p, ref sz, false, af, 1, 0); return (r, sz); });
            if (udp != IntPtr.Zero)
            {
                try
                {
                    int n = Marshal.ReadInt32(udp);
                    int rowSize = af == AfInet ? 12 : 28;
                    var any = af == AfInet ? IPAddress.Any : IPAddress.IPv6Any;
                    for (int i = 0; i < n; i++)
                    {
                        var row = udp + 4 + i * rowSize;
                        if (af == AfInet)
                            list.Add(new SocketRow(true, new IPAddress((uint)Marshal.ReadInt32(row)), Port(row + 4), any, 0, TcpState.Listen, Marshal.ReadInt32(row + 8)));
                        else
                            list.Add(new SocketRow(true, V6(row, Marshal.ReadInt32(row + 16)), Port(row + 20), any, 0, TcpState.Listen, Marshal.ReadInt32(row + 24)));
                    }
                }
                finally { Marshal.FreeHGlobal(udp); }
            }
        }
        return list;
    }

    private static int Port(IntPtr p)
    {
        // Ports stehen in Netzwerk-Byte-Reihenfolge in den unteren 16 Bit
        var v = (uint)Marshal.ReadInt32(p);
        return (int)(((v & 0xFF) << 8) | ((v >> 8) & 0xFF));
    }

    private static IPAddress V6(IntPtr p, int scope)
    {
        var b = new byte[16];
        Marshal.Copy(p, b, 0, 16);
        return new IPAddress(b, (uint)scope);
    }

    /// <summary>ARP-Tabelle: IPv4-Adresse → MAC-Adresse der Geräte, mit denen der PC zuletzt gesprochen hat.</summary>
    public static Dictionary<IPAddress, string> ArpTable()
    {
        var map = new Dictionary<IPAddress, string>();
        var buf = Fetch((p, s) => { int sz = s; var r = GetIpNetTable(p, ref sz, false); return (r, sz); });
        if (buf == IntPtr.Zero) return map;
        try
        {
            int n = Marshal.ReadInt32(buf);
            // MIB_IPNETROW: dwIndex, dwPhysAddrLen, bPhysAddr[8], dwAddr, dwType = 24 Bytes
            for (int i = 0; i < n; i++)
            {
                var row = buf + 4 + i * 24;
                int len = Marshal.ReadInt32(row + 4);
                int type = Marshal.ReadInt32(row + 20);
                if (len != 6 || type is not (3 or 4)) continue; // nur dynamische/statische, keine ungültigen
                var mac = new byte[6];
                Marshal.Copy(row + 8, mac, 0, 6);
                if (mac.All(x => x == 0) || mac.All(x => x == 0xFF) || (mac[0] & 1) == 1) continue; // Broadcast/Multicast
                map[new IPAddress((uint)Marshal.ReadInt32(row + 16))] = FormatMac(mac);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return map;
    }

    public static string FormatMac(byte[] mac) => string.Join(":", mac.Select(b => b.ToString("X2")));

    // ───────────── WLAN ─────────────

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode, IntPtr reserved, out int size, out IntPtr data, IntPtr valueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr p);

    /// <summary>SSID, Signal und Datenrate eines verbundenen WLAN-Adapters (null = nicht verbunden / kein WLAN).</summary>
    public static WifiInfo? Wifi(string adapterId)
    {
        if (!Guid.TryParse(adapterId, out var guid)) return null;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return null;
            try
            {
                // wlan_intf_opcode_current_connection = 7
                if (WlanQueryInterface(h, ref guid, 7, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) return null;
                try
                {
                    if (Marshal.ReadInt32(data) != 1) return null; // wlan_interface_state_connected
                    const int assoc = 8 + 512;                     // nach isState, ConnectionMode, strProfileName[256]
                    int ssidLen = Math.Clamp(Marshal.ReadInt32(data + assoc), 0, 32);
                    var ssid = new byte[ssidLen];
                    Marshal.Copy(data + assoc + 4, ssid, 0, ssidLen);
                    var bssid = new byte[6];
                    Marshal.Copy(data + assoc + 40, bssid, 0, 6);
                    int signal = Marshal.ReadInt32(data + assoc + 56);
                    int rx = Marshal.ReadInt32(data + assoc + 60), tx = Marshal.ReadInt32(data + assoc + 64);

                    int channel = 0;
                    // wlan_intf_opcode_channel_number = 8
                    if (WlanQueryInterface(h, ref guid, 8, IntPtr.Zero, out _, out var ch, IntPtr.Zero) == 0)
                    {
                        channel = Marshal.ReadInt32(ch);
                        WlanFreeMemory(ch);
                    }
                    return new WifiInfo(Encoding.UTF8.GetString(ssid), FormatMac(bssid), signal, rx / 1000.0, tx / 1000.0, channel);
                }
                finally { WlanFreeMemory(data); }
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }
        catch
        {
            return null; // kein WLAN-Dienst
        }
    }

    // ───────────── Prozesse ─────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    /// <summary>Vollständiger Pfad der EXE eines Prozesses ("" = nicht lesbar, z. B. System).</summary>
    public static string ProcessPath(int pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    // ───────────── Pfade ─────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string device, StringBuilder target, int max);

    private static Dictionary<string, string>? _devices;

    /// <summary>"\device\harddiskvolume3\programme\x.exe" → "C:\programme\x.exe".</summary>
    public static string DevicePathToDos(string path)
    {
        if (!path.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase)) return path;
        _devices ??= BuildDeviceMap();
        foreach (var (dev, drive) in _devices)
            if (path.StartsWith(dev + @"\", StringComparison.OrdinalIgnoreCase))
                return drive + path[dev.Length..];
        return path;
    }

    private static Dictionary<string, string> BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DriveInfo.GetDrives())
        {
            var letter = d.Name.TrimEnd('\\');
            var sb = new StringBuilder(512);
            if (QueryDosDevice(letter, sb, sb.Capacity) > 0) map[sb.ToString()] = letter;
        }
        return map;
    }

    /// <summary>Private, Link-Local- oder Loopback-Adresse?</summary>
    public static bool IsLocal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224;
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal;
    }

    public static ushort ReadPortBE(ReadOnlySpan<byte> b) => BinaryPrimitives.ReadUInt16BigEndian(b);
}
