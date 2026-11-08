using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using EZTool.Core;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace EZTool.Services;

public sealed record FpsReading(int Pid, string Process, double Fps, double FrameTimeMs);

/// <summary>
/// Misst die Bilder pro Sekunde des Programms im Vordergrund über Windows-ETW-Ereignisse
/// (DirectX "Present" – dieselbe Quelle wie PresentMon). Benötigt Administrator-Rechte.
/// Erfasst werden DirectX-9/10/11/12-Spiele; Vulkan/OpenGL nur, wenn sie über DXGI präsentieren.
/// </summary>
public sealed class FpsService
{
    public static FpsService Instance { get; } = new();

    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1;
    private const string SessionName = "EZTool-FPS";

    private readonly object _lock = new();
    private readonly Dictionary<int, Queue<double>> _frames = new();
    private readonly Dictionary<int, double> _lastFrame = new();
    private TraceEventSession? _session;
    private Timer? _timer;
    private int _users;
    private volatile int _foregroundPid;
    private List<float>? _recording;

    /// <summary>Letzte Messung des Vordergrund-Programms (null = kein Spiel / keine Frames).</summary>
    public FpsReading? Current { get; private set; }

    /// <summary>Jede Sekunde (UI-Thread).</summary>
    public event Action<FpsReading?>? Updated;

    public bool IsRunning => _session != null;
    public string? Error { get; private set; }

    public static bool CanMeasure => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Messung starten (mehrere Nutzer möglich: Overlay und Gaming-Modus).</summary>
    public void Acquire()
    {
        lock (_lock)
        {
            if (_users++ > 0) return;
        }
        if (!CanMeasure)
        {
            Error = Loc.T("Gaming.Fps.NeedsAdmin");
            return;
        }
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue);
            _session.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue);
            _session.Source.AllEvents += OnEvent;
            var s = _session;
            new Thread(() =>
            {
                try { s.Source.Process(); } catch { /* Sitzung beendet */ }
            }) { IsBackground = true, Name = "EZTool.FPS" }.Start();
            _timer = new Timer(_ => Tick(), null, 1000, 1000);
            Error = null;
        }
        catch (Exception ex)
        {
            Error = Loc.T("Gaming.Fps.StartFailed", ex.Message);
            _session?.Dispose();
            _session = null;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_users == 0 || --_users > 0) return;
        }
        _timer?.Dispose();
        _timer = null;
        try { _session?.Dispose(); } catch { /* egal */ }
        _session = null;
        Current = null;
        lock (_lock)
        {
            _frames.Clear();
            _lastFrame.Clear();
        }
    }

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        bool present = (e.ProviderGuid == Dxgi && id is DxgiPresentStart or DxgiPresentMpoStart) || (e.ProviderGuid == D3D9 && id == D3D9PresentStart);
        if (!present) return;

        double t = e.TimeStampRelativeMSec;
        int pid = e.ProcessID;
        lock (_lock)
        {
            if (!_frames.TryGetValue(pid, out var q)) _frames[pid] = q = new Queue<double>();
            q.Enqueue(t);
            if (_recording != null && pid == _foregroundPid && _lastFrame.TryGetValue(pid, out var prev))
            {
                var dt = t - prev;
                if (dt is > 0 and < 1000) _recording.Add((float)dt);
            }
            _lastFrame[pid] = t;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private readonly Dictionary<int, string> _names = new();

    private void Tick()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var fg);
        _foregroundPid = (int)fg;

        FpsReading? reading = null;
        lock (_lock)
        {
            double newest = _lastFrame.Count > 0 ? _lastFrame.Values.Max() : 0;
            foreach (var (pid, q) in _frames)
                while (q.Count > 0 && q.Peek() < newest - 2000) q.Dequeue();
            foreach (var dead in _frames.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList())
            {
                _frames.Remove(dead);
                _lastFrame.Remove(dead);
            }

            if (_frames.TryGetValue(_foregroundPid, out var fq) && fq.Count > 1 && _lastFrame.TryGetValue(_foregroundPid, out var last) && newest - last < 1500)
            {
                var window = fq.Where(t => t >= last - 1000).ToList();
                if (window.Count > 1)
                {
                    double span = window[^1] - window[0];
                    double fps = span > 0 ? (window.Count - 1) * 1000.0 / span : 0;
                    reading = new FpsReading(_foregroundPid, NameOf(_foregroundPid), Math.Round(fps, 1), span / Math.Max(1, window.Count - 1));
                }
            }
        }

        Current = reading;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Updated?.Invoke(reading));
    }

    private string NameOf(int pid)
    {
        if (_names.TryGetValue(pid, out var n)) return n;
        try
        {
            using var p = Process.GetProcessById(pid);
            n = p.MainWindowTitle is { Length: > 0 } title ? title : p.ProcessName;
        }
        catch { n = Loc.T("Gaming.Fps.FallbackName"); }
        if (_names.Count > 200) _names.Clear();
        return _names[pid] = n;
    }

    /// <summary>Startet die Aufzeichnung aller Frame-Zeiten des Vordergrund-Programms (für den Session-Bericht).</summary>
    public void StartRecording()
    {
        lock (_lock) _recording = new List<float>(100_000);
    }

    public List<float> StopRecording()
    {
        lock (_lock)
        {
            var r = _recording ?? [];
            _recording = null;
            return r;
        }
    }
}
