using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using EZTool.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace EZTool.Services.Network;

public sealed record FlowKey(int Pid, IPAddress Remote, int RemotePort, bool Udp);

/// <summary>Byte-Zähler einer App oder Verbindung. Wird vom ETW-Thread hochgezählt, vom Timer ausgewertet.</summary>
public sealed class Flow
{
    internal long SecDown, SecUp;
    public long TotalDown, TotalUp;
    public long RateDown, RateUp;
    public DateTime LastActive;
}

/// <summary>
/// Misst den Datenverkehr pro App und Verbindung live über die Windows-ETW-Quelle "Microsoft-Windows-Kernel-Network".
/// Jedes gesendete und empfangene TCP/UDP-Paket kommt mit Prozess-ID, Größe und Gegenstelle an. Benötigt Administrator-Rechte
/// und läuft nur, solange die Netzwerk-Seite offen ist.
/// </summary>
public sealed class TrafficMonitor
{
    public static TrafficMonitor Instance { get; } = new();

    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private const string SessionName = "EZTool-Network";

    public ConcurrentDictionary<FlowKey, Flow> Flows { get; } = new();
    public ConcurrentDictionary<int, Flow> Apps { get; } = new();
    public long TotalDown, TotalUp, RateDown, RateUp;
    private long _secDown, _secUp;

    private TraceEventSession? _session;
    private Timer? _timer;
    private int _users;
    private readonly object _lock = new();

    public bool IsRunning => _session != null;
    public string? Error { get; private set; }
    public DateTime Since { get; private set; } = DateTime.Now;

    /// <summary>Jede Sekunde, aus einem Timer-Thread.</summary>
    public event Action? Tick;

    public static bool CanMeasure => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public void Acquire()
    {
        lock (_lock)
        {
            if (_users++ > 0) return;
            if (!CanMeasure)
            {
                Error = Loc.T("Network.NeedsAdmin");
                return;
            }
            try
            {
                // Eine liegengebliebene Sitzung (z. B. nach Absturz) zuerst schließen
                TraceEventSession.GetActiveSession(SessionName)?.Stop(true);

                _session = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = 16 };
                _session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, 0x30); // IPv4 + IPv6
                _session.Source.AllEvents += OnEvent;
                var s = _session;
                new Thread(() =>
                {
                    try { s.Source.Process(); } catch { /* Sitzung beendet */ }
                }) { IsBackground = true, Name = "EZTool.Network" }.Start();
                _timer = new Timer(_ => OnTick(), null, 1000, 1000);
                Since = DateTime.Now;
                Error = null;
            }
            catch (Exception ex)
            {
                Error = Loc.T("Network.StartFailed", ex.Message);
                _session?.Dispose();
                _session = null;
            }
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_users == 0 || --_users > 0) return;
            _timer?.Dispose();
            _timer = null;
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>
    /// Ereignisse von Hand dekodieren (schnell, ohne Allokation). Aufbau laut Manifest:
    /// PID, size, daddr, saddr, dport, sport – daddr/dport ist bei Senden und Empfangen immer die Gegenstelle.
    /// </summary>
    private unsafe void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        bool send, v6, udp;
        switch (id)
        {
            case 10: send = true; v6 = false; udp = false; break;
            case 11: send = false; v6 = false; udp = false; break;
            case 26: send = true; v6 = true; udp = false; break;
            case 27: send = false; v6 = true; udp = false; break;
            case 42: send = true; v6 = false; udp = true; break;
            case 43: send = false; v6 = false; udp = true; break;
            case 58: send = true; v6 = true; udp = true; break;
            case 59: send = false; v6 = true; udp = true; break;
            default: return;
        }
        int need = v6 ? 44 : 20;
        if (e.EventDataLength < need) return;
        var data = new ReadOnlySpan<byte>((void*)e.DataStart, need);
        int pid = MemoryMarshal.Read<int>(data);
        int size = MemoryMarshal.Read<int>(data[4..]);
        if (size <= 0) return;
        IPAddress remote;
        int port;
        if (v6)
        {
            remote = new IPAddress(data.Slice(8, 16));
            port = NetNative.ReadPortBE(data.Slice(40, 2));
        }
        else
        {
            remote = new IPAddress(data.Slice(8, 4));
            port = NetNative.ReadPortBE(data.Slice(16, 2));
        }
        if (IPAddress.IsLoopback(remote) || (remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4()))) return;

        var flow = Flows.GetOrAdd(new FlowKey(pid, remote, port, udp), _ => new Flow());
        var app = Apps.GetOrAdd(pid, _ => new Flow());
        if (send)
        {
            Interlocked.Add(ref flow.SecUp, size);
            Interlocked.Add(ref app.SecUp, size);
            Interlocked.Add(ref _secUp, size);
        }
        else
        {
            Interlocked.Add(ref flow.SecDown, size);
            Interlocked.Add(ref app.SecDown, size);
            Interlocked.Add(ref _secDown, size);
        }
    }

    private void OnTick()
    {
        var now = DateTime.Now;
        RateDown = Interlocked.Exchange(ref _secDown, 0);
        RateUp = Interlocked.Exchange(ref _secUp, 0);
        TotalDown += RateDown;
        TotalUp += RateUp;
        foreach (var f in Flows.Values.Concat(Apps.Values)) Roll(f, now);

        // Verbindungen ohne Verkehr seit 3 Minuten vergessen, damit die Liste nicht endlos wächst
        foreach (var (k, f) in Flows)
            if (now - f.LastActive > TimeSpan.FromMinutes(3)) Flows.TryRemove(k, out _);
        Tick?.Invoke();
    }

    private static void Roll(Flow f, DateTime now)
    {
        f.RateDown = Interlocked.Exchange(ref f.SecDown, 0);
        f.RateUp = Interlocked.Exchange(ref f.SecUp, 0);
        f.TotalDown += f.RateDown;
        f.TotalUp += f.RateUp;
        if (f.RateDown + f.RateUp > 0 || f.LastActive == default) f.LastActive = now;
    }

    public void Reset()
    {
        Flows.Clear();
        Apps.Clear();
        TotalDown = TotalUp = 0;
        Since = DateTime.Now;
    }
}
