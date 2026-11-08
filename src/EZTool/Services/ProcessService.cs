using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace EZTool.Services;

public sealed record ProcSample(int Pid, string Name, string Path, long CpuTicks, long Memory);

/// <summary>Liest alle Prozesse mit Pfad, CPU-Zeit und Speicher – auch geschützte, soweit Windows es erlaubt.</summary>
public static class ProcessService
{
    /// <summary>Windows-Kernprozesse: Beenden würde das System abstürzen lassen oder abmelden.</summary>
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Memory Compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "fontdrvhost", "sihost", "MsMpEng", "NisSrv", "SecurityHealthService", "MpDefenderCoreService",
        "spoolsv", "WUDFHost", "audiodg", "conhost", "ctfmon", "RuntimeBroker", "StartMenuExperienceHost", "ShellExperienceHost",
        "SearchHost", "TextInputHost", "LsaIso", "secure system", "WmiPrvSE", "dllhost", "taskhostw", "EZTool",
    };

    public static bool IsCritical(string name) => Critical.Contains(name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    private const uint QueryLimited = 0x1000;

    private static readonly Dictionary<int, string> PathCache = new();

    public static List<ProcSample> Sample()
    {
        var list = new List<ProcSample>(400);
        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    var pid = p.Id;
                    alive.Add(pid);
                    long ticks = 0;
                    string path = "";
                    var h = OpenProcess(QueryLimited, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            if (GetProcessTimes(h, out _, out _, out var k, out var u)) ticks = k + u;
                            if (!PathCache.TryGetValue(pid, out path!))
                            {
                                var sb = new StringBuilder(1024);
                                int size = sb.Capacity;
                                path = QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
                                PathCache[pid] = path;
                            }
                        }
                        finally { CloseHandle(h); }
                    }
                    list.Add(new ProcSample(pid, p.ProcessName, path, ticks, p.WorkingSet64));
                }
                catch { /* Prozess beendet sich gerade */ }
            }
        }
        foreach (var dead in PathCache.Keys.Where(k => !alive.Contains(k)).ToList()) PathCache.Remove(dead);
        return list;
    }

    public static (int Killed, int Failed) Kill(IEnumerable<int> pids)
    {
        int ok = 0, fail = 0;
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (IsCritical(p.ProcessName)) { fail++; continue; }
                p.Kill();
                ok++;
            }
            catch { fail++; }
        }
        return (ok, fail);
    }

    private static readonly Dictionary<string, (string Description, string Company)> InfoCache = new(StringComparer.OrdinalIgnoreCase);

    public static (string Description, string Company) FileInfo(string path)
    {
        if (string.IsNullOrEmpty(path)) return ("", "");
        lock (InfoCache)
        {
            if (InfoCache.TryGetValue(path, out var cached)) return cached;
        }
        (string, string) info = ("", "");
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            info = (v.FileDescription?.Trim() ?? "", v.CompanyName?.Trim() ?? "");
        }
        catch { /* egal */ }
        lock (InfoCache) InfoCache[path] = info;
        return info;
    }
}
