using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EZTool.Services;

public sealed class GameSession
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Game { get; set; } = "";
    public double AvgFps { get; set; }
    public double Low1Fps { get; set; }
    public double MaxFps { get; set; }
    public long Frames { get; set; }
    public double? MaxCpuTemp { get; set; }
    public double? MaxGpuTemp { get; set; }
    public double AvgCpuLoad { get; set; }
    public double? AvgGpuLoad { get; set; }
    public List<DateTime> Times { get; set; } = [];
    public List<double?> FpsSeries { get; set; } = [];
    public List<double?> CpuTempSeries { get; set; } = [];
    public List<double?> GpuTempSeries { get; set; } = [];
    public List<string> Actions { get; set; } = [];

    public string DurationText => (End - Start) is var d && d.TotalHours >= 1 ? $"{(int)d.TotalHours} Std {d.Minutes} Min" : $"{(int)(End - Start).TotalMinutes} Min";
    public string FpsText => AvgFps > 0 ? $"{AvgFps:0}" : "—";
    public string LowText => Low1Fps > 0 ? $"{Low1Fps:0}" : "—";
    public string TempText => $"{(MaxCpuTemp is { } c ? $"{c:0}" : "—")} / {(MaxGpuTemp is { } g ? $"{g:0}" : "—")} °C";
}

/// <summary>
/// Gaming-Modus: optimiert das System für die Spielzeit und stellt danach alles wieder her.
/// Währenddessen werden FPS, Temperaturen und Auslastung für den Session-Bericht aufgezeichnet.
/// </summary>
public sealed class GamingService
{
    public static GamingService Instance { get; } = new();
    public static string SessionFolder => Path.Combine(SettingsService.Folder, "Sessions");

    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    public bool IsActive { get; private set; }
    public DateTime Since { get; private set; }
    public List<string> AppliedActions { get; } = [];

    public event Action? Changed;
    public event Action<GameSession>? SessionFinished;

    private string? _previousScheme;
    private bool _stoppedUpdates;
    private readonly List<string> _closedApps = [];
    private GameSession? _session;
    private readonly Dictionary<string, int> _gameSeconds = new();

    public async Task ActivateAsync()
    {
        if (IsActive) return;
        var cfg = SettingsService.Current;
        AppliedActions.Clear();
        _closedApps.Clear();
        _gameSeconds.Clear();

        await Task.Run(() =>
        {
            if (cfg.BoostPowerPlan) TrySetHighPerformance();
            if (cfg.BoostPurgeRam && PurgeStandbyList()) AppliedActions.Add("Standby-Arbeitsspeicher geleert");
            if (cfg.BoostPauseUpdates) TryPauseUpdates();
            if (cfg.BoostCloseApps.Count > 0) CloseApps(cfg.BoostCloseApps);
        });

        IsActive = true;
        Since = DateTime.Now;
        _session = new GameSession { Start = Since, Actions = [.. AppliedActions] };
        FpsService.Instance.Acquire();
        FpsService.Instance.StartRecording();
        HardwareService.Instance.Updated += Record;
        Changed?.Invoke();
    }

    public async Task<GameSession?> DeactivateAsync()
    {
        if (!IsActive) return null;
        HardwareService.Instance.Updated -= Record;
        var frames = FpsService.Instance.StopRecording();
        FpsService.Instance.Release();

        var cfg = SettingsService.Current;
        await Task.Run(() =>
        {
            if (_previousScheme != null) RunPowercfg($"/setactive {_previousScheme}");
            if (_stoppedUpdates)
            {
                try
                {
                    using var sc = new ServiceController("wuauserv");
                    if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
                }
                catch { /* Windows startet den Dienst bei Bedarf ohnehin selbst */ }
            }
            if (cfg.BoostRestartApps)
            {
                foreach (var exe in _closedApps.Where(File.Exists))
                {
                    try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); }
                    catch { /* egal */ }
                }
            }
        });
        _previousScheme = null;
        _stoppedUpdates = false;
        IsActive = false;

        var session = _session;
        _session = null;
        if (session != null)
        {
            session.End = DateTime.Now;
            session.Game = _gameSeconds.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "Kein Spiel erkannt";
            if (frames.Count > 10)
            {
                var sorted = frames.OrderByDescending(f => f).ToList();
                double avgFrame = frames.Average(f => (double)f);
                double p99 = sorted[Math.Max(0, (int)(sorted.Count * 0.01) - 1)];
                session.Frames = frames.Count;
                session.AvgFps = Math.Round(1000 / avgFrame, 1);
                session.Low1Fps = Math.Round(1000 / p99, 1);
                session.MaxFps = Math.Round(session.FpsSeries.Max() ?? 0, 1);
            }
            Save(session);
            SessionFinished?.Invoke(session);
        }
        Changed?.Invoke();
        return session;
    }

    /// <summary>Jede Sekunde: Werte für den Bericht mitschreiben.</summary>
    private void Record(HardwareSnapshot s)
    {
        var session = _session;
        if (session == null) return;
        var fps = FpsService.Instance.Current;
        session.Times.Add(DateTime.Now);
        session.FpsSeries.Add(fps?.Fps);
        session.CpuTempSeries.Add(s.CpuTemp);
        session.GpuTempSeries.Add(s.GpuTemp);
        if (s.CpuTemp is { } ct) session.MaxCpuTemp = Math.Max(session.MaxCpuTemp ?? 0, ct);
        if (s.GpuTemp is { } gt) session.MaxGpuTemp = Math.Max(session.MaxGpuTemp ?? 0, gt);
        int n = session.Times.Count;
        session.AvgCpuLoad += (s.CpuLoad - session.AvgCpuLoad) / n;
        if (s.GpuLoad is { } gl) session.AvgGpuLoad = (session.AvgGpuLoad ?? 0) + (gl - (session.AvgGpuLoad ?? 0)) / n;
        if (fps is { Fps: > 5 }) _gameSeconds[fps.Process] = _gameSeconds.GetValueOrDefault(fps.Process) + 1;
    }

    // ───────────── Energieplan ─────────────

    private void TrySetHighPerformance()
    {
        var active = RunPowercfg("/getactivescheme");
        var m = Regex.Match(active, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (!m.Success || m.Value.Equals(HighPerformance, StringComparison.OrdinalIgnoreCase)) return;

        var list = RunPowercfg("/list");
        string? target = list.Contains(HighPerformance, StringComparison.OrdinalIgnoreCase) ? HighPerformance : null;
        if (target == null)
        {
            // Auf manchen Systemen ist "Höchstleistung" ausgeblendet – eine Kopie anlegen
            var dup = Regex.Match(RunPowercfg($"/duplicatescheme {HighPerformance}"), @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            if (dup.Success) target = dup.Value;
        }
        if (target == null) return;
        RunPowercfg($"/setactive {target}");
        _previousScheme = m.Value;
        AppliedActions.Add("Energieplan „Höchstleistung“ aktiv");
    }

    private static string RunPowercfg(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return o;
        }
        catch { return ""; }
    }

    // ───────────── Windows Update ─────────────

    private void TryPauseUpdates()
    {
        try
        {
            using var sc = new ServiceController("wuauserv");
            if (sc.Status != ServiceControllerStatus.Running) return;
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            _stoppedUpdates = true;
            AppliedActions.Add("Windows Update angehalten");
        }
        catch { /* gerade beschäftigt – dann eben nicht */ }
    }

    // ───────────── Apps schließen ─────────────

    private void CloseApps(IEnumerable<string> names)
    {
        var closed = new List<string>();
        foreach (var name in names)
        {
            if (ProcessService.IsCritical(name)) continue;
            var procs = Process.GetProcessesByName(name);
            if (procs.Length == 0) continue;
            foreach (var p in procs)
            {
                using (p)
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (path != null && !_closedApps.Contains(path, StringComparer.OrdinalIgnoreCase)) _closedApps.Add(path);
                    }
                    catch { /* Pfad nicht lesbar */ }
                    try { p.Kill(); } catch { /* bereits beendet */ }
                }
            }
            closed.Add(name);
        }
        if (closed.Count > 0) AppliedActions.Add("Geschlossen: " + string.Join(", ", closed));
    }

    // ───────────── Standby-Liste leeren ─────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public int Count; public LUID Luid; public int Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int len, IntPtr prev, IntPtr retLen);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    /// <summary>
    /// Leert die Standby-Liste (zwischengespeicherte, aktuell ungenutzte Daten im RAM). Gefahrlos:
    /// Windows füllt sie bei Bedarf neu. Hilft gegen Mikroruckler, wenn der Speicher knapp ist.
    /// </summary>
    public static bool PurgeStandbyList()
    {
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x20 | 0x8, out var token)) return false;
            try
            {
                if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var luid)) return false;
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 };
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            }
            finally { CloseHandle(token); }

            int command = 4; // MemoryPurgeStandbyList
            return NtSetSystemInformation(80 /* SystemMemoryListInformation */, ref command, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    // ───────────── Berichte speichern/laden ─────────────

    private static void Save(GameSession s)
    {
        try
        {
            Directory.CreateDirectory(SessionFolder);
            File.WriteAllText(Path.Combine(SessionFolder, $"session-{s.Start:yyyyMMdd-HHmmss}.json"), JsonSerializer.Serialize(s));
        }
        catch { /* egal */ }
    }

    public static List<GameSession> LoadSessions()
    {
        var list = new List<GameSession>();
        try
        {
            if (!Directory.Exists(SessionFolder)) return list;
            foreach (var f in Directory.GetFiles(SessionFolder, "session-*.json").OrderDescending().Take(30))
            {
                try
                {
                    if (JsonSerializer.Deserialize<GameSession>(File.ReadAllText(f)) is { } s) list.Add(s);
                }
                catch { /* defekt */ }
            }
        }
        catch { /* egal */ }
        return list;
    }
}
