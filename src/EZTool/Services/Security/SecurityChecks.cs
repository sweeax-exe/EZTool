using System.Diagnostics;
using System.Management;
using EZTool.Core;
using EZTool.Services.Network;
using Microsoft.Win32;

namespace EZTool.Services.Security;

public enum CheckLevel { Good, Warn, Bad, Info }

/// <summary>Ein Prüfpunkt des Sicherheits-Checks. Action = Windows-Seite, auf der man es ändert.</summary>
public sealed record CheckResult(string Id, string Title, string Detail, CheckLevel Level, int Weight, string? Action);

public sealed record Suspect(string Path, string Name, IReadOnlyList<string> Reasons, int Risk);

/// <summary>
/// Sicherheits-Check: liest Windows-Einstellungen, die man sonst an zehn verschiedenen Stellen suchen müsste.
/// Ändert nichts – jeder Punkt führt zur passenden Windows-Seite.
/// </summary>
public static class SecurityChecks
{
    public static List<CheckResult> Run(DefenderStatus d, DefenderPrefs p)
    {
        var list = new List<CheckResult>();

        // Virenschutz
        if (d.OtherAntivirus != null)
            list.Add(new("av", Loc.T("Security.Check.Av"), Loc.T("Security.Check.AvOther", d.OtherAntivirus), CheckLevel.Good, 25, null));
        else if (d.Available && d.AntivirusEnabled && d.RealTime)
            list.Add(new("av", Loc.T("Security.Check.Av"), Loc.T("Security.Check.AvOn"), CheckLevel.Good, 25, "windowsdefender://threat/"));
        else
            list.Add(new("av", Loc.T("Security.Check.Av"), Loc.T("Security.Check.AvOff"), CheckLevel.Bad, 25, "windowsdefender://threat/"));

        if (d.Available && d.OtherAntivirus == null)
        {
            var age = d.SignaturesUpdated is { } t ? DateTime.Now - t : TimeSpan.MaxValue;
            list.Add(new("sig", Loc.T("Security.Check.Signatures"),
                d.SignaturesUpdated is { } u ? Loc.T("Security.Check.SignaturesAge", Format.Age(u), d.SignatureVersion) : Loc.T("Security.Check.Unknown"),
                age < TimeSpan.FromDays(2) ? CheckLevel.Good : age < TimeSpan.FromDays(7) ? CheckLevel.Warn : CheckLevel.Bad, 10, "windowsdefender://threat/"));

            list.Add(new("pua", Loc.T("Security.Check.Pua"), p.Pua == 1 ? Loc.T("Security.Check.On") : Loc.T("Security.Check.PuaOff"),
                p.Pua == 1 ? CheckLevel.Good : CheckLevel.Warn, 5, null));
        }

        // Firewall
        var fw = FirewallProfiles();
        if (fw.Count > 0)
        {
            var off = fw.Where(x => !x.On).Select(x => x.Name).ToList();
            list.Add(new("fw", Loc.T("Security.Check.Firewall"),
                off.Count == 0 ? Loc.T("Security.Check.FirewallOn") : Loc.T("Security.Check.FirewallOff", string.Join(", ", off)),
                off.Count == 0 ? CheckLevel.Good : CheckLevel.Bad, 15, "windowsdefender://network/"));
        }

        // SmartScreen
        var ss = Reg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled") as string;
        var ssPolicy = Reg(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableSmartScreen");
        bool ssOff = string.Equals(ss, "Off", StringComparison.OrdinalIgnoreCase) || ssPolicy is int and 0;
        list.Add(new("ss", "SmartScreen", ssOff ? Loc.T("Security.Check.SmartScreenOff") : Loc.T("Security.Check.SmartScreenOn"),
            ssOff ? CheckLevel.Bad : CheckLevel.Good, 8, "windowsdefender://appbrowser/"));

        // Benutzerkontensteuerung
        var lua = Reg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA");
        var consent = Reg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin");
        var uacLevel = lua is int and 0 ? CheckLevel.Bad : consent is int and 0 ? CheckLevel.Warn : CheckLevel.Good;
        list.Add(new("uac", Loc.T("Security.Check.Uac"),
            uacLevel switch { CheckLevel.Bad => Loc.T("Security.Check.UacOff"), CheckLevel.Warn => Loc.T("Security.Check.UacSilent"), _ => Loc.T("Security.Check.On") },
            uacLevel, 10, "UserAccountControlSettings.exe"));

        // Speicher-Integrität (Kernisolierung)
        var hvci = DeviceGuardRunning(2);
        list.Add(new("hvci", Loc.T("Security.Check.Hvci"), hvci ? Loc.T("Security.Check.On") : Loc.T("Security.Check.HvciOff"),
            hvci ? CheckLevel.Good : CheckLevel.Warn, 8, "windowsdefender://coreisolation/"));

        // Secure Boot
        var sb = Reg(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        list.Add(new("sb", "Secure Boot", sb is 1 ? Loc.T("Security.Check.On") : Loc.T("Security.Check.SecureBootOff"),
            sb is 1 ? CheckLevel.Good : CheckLevel.Warn, 6, null));

        // TPM
        var tpm = Tpm();
        list.Add(new("tpm", "TPM", tpm ?? Loc.T("Security.Check.TpmMissing"), tpm != null ? CheckLevel.Good : CheckLevel.Warn, 4, null));

        // BitLocker
        var bl = BitLocker();
        list.Add(new("bl", "BitLocker", bl switch
        {
            1 => Loc.T("Security.Check.BitLockerOn"),
            0 => Loc.T("Security.Check.BitLockerOff"),
            _ => Loc.T("Security.Check.BitLockerNone"),
        }, bl == 1 ? CheckLevel.Good : bl == 0 ? CheckLevel.Warn : CheckLevel.Info, 6, bl == 0 ? "control.exe /name Microsoft.BitLockerDriveEncryption" : null));

        // Remotedesktop
        var rdp = Reg(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections");
        bool rdpOn = rdp is 0;
        list.Add(new("rdp", Loc.T("Security.Check.Rdp"), rdpOn ? Loc.T("Security.Check.RdpOn") : Loc.T("Security.Check.Off"),
            rdpOn ? CheckLevel.Warn : CheckLevel.Good, 5, "ms-settings:remotedesktop"));

        // SMBv1 (WannaCry-Lücke)
        var smb1Start = Reg(@"SYSTEM\CurrentControlSet\Services\mrxsmb10", "Start");
        bool smb1 = smb1Start is int st && st != 4;
        list.Add(new("smb1", "SMB 1.0", smb1 ? Loc.T("Security.Check.Smb1On") : Loc.T("Security.Check.Smb1Off"),
            smb1 ? CheckLevel.Bad : CheckLevel.Good, 5, smb1 ? "optionalfeatures.exe" : null));

        return list;
    }

    /// <summary>Ausstehende Windows-Updates (kann je nach Rechner 10–60 Sekunden dauern).</summary>
    public static async Task<CheckResult> WindowsUpdateAsync()
    {
        var count = await Task.Run(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
                if (type == null) return -1;
                dynamic session = Activator.CreateInstance(type)!;
                dynamic searcher = session.CreateUpdateSearcher();
                searcher.Online = false; // nur bekannte Updates, kein langer Server-Abgleich
                dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
                return (int)result.Updates.Count;
            }
            catch
            {
                return -1;
            }
        });
        return new("wu", "Windows Update",
            count < 0 ? Loc.T("Security.Check.Unknown") : count == 0 ? Loc.T("Security.Check.UpdatesNone") : Loc.T("Security.Check.UpdatesPending", count),
            count < 0 ? CheckLevel.Info : count == 0 ? CheckLevel.Good : CheckLevel.Warn, 8, "ms-settings:windowsupdate");
    }

    /// <summary>Punktzahl 0–100: gewichteter Anteil der erfüllten Punkte (Warnung zählt halb).</summary>
    public static int Score(IEnumerable<CheckResult> checks)
    {
        double got = 0, max = 0;
        foreach (var c in checks.Where(c => c.Level != CheckLevel.Info))
        {
            max += c.Weight;
            got += c.Level switch { CheckLevel.Good => c.Weight, CheckLevel.Warn => c.Weight * 0.5, _ => 0 };
        }
        return max == 0 ? 0 : (int)Math.Round(100 * got / max);
    }

    public static void Open(string action)
    {
        try
        {
            var parts = action.Split(' ', 2);
            if (parts[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo(parts[0], parts.Length > 1 ? parts[1] : "") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(action) { UseShellExecute = true });
        }
        catch { /* Seite gibt es auf dieser Windows-Version nicht */ }
    }

    // ───────────── Einzelabfragen ─────────────

    private static object? Reg(string key, string value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(key);
            return k?.GetValue(value);
        }
        catch { return null; }
    }

    private static List<(string Name, bool On)> FirewallProfiles()
    {
        var list = new List<(string, bool)>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Name, Enabled FROM MSFT_NetFirewallProfile");
            foreach (ManagementObject o in s.Get())
                using (o)
                    list.Add((o["Name"] as string ?? "?", Convert.ToInt32(o["Enabled"]) == 1));
        }
        catch { /* nicht verfügbar */ }
        return list;
    }

    private static bool DeviceGuardRunning(uint service)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (ManagementObject o in s.Get())
                using (o)
                    if (o["SecurityServicesRunning"] is uint[] running) return running.Contains(service);
        }
        catch { /* nicht verfügbar */ }
        return false;
    }

    private static string? Tpm()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftTpm", "SELECT * FROM Win32_Tpm");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    bool on = o["IsEnabled_InitialValue"] is true && o["IsActivated_InitialValue"] is true;
                    var ver = (o["SpecVersion"] as string ?? "").Split(',')[0].Trim();
                    return on ? $"TPM {ver}" : null;
                }
            }
        }
        catch { /* kein TPM / keine Rechte */ }
        return null;
    }

    /// <summary>1 = Systemlaufwerk verschlüsselt, 0 = nicht, -1 = BitLocker nicht verfügbar (z. B. Windows Home).</summary>
    private static int BitLocker()
    {
        try
        {
            var drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
            using var s = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='{drive}'");
            foreach (ManagementObject o in s.Get())
                using (o)
                    return Convert.ToInt32(o["ProtectionStatus"]) == 1 ? 1 : 0;
        }
        catch { /* Namespace fehlt */ }
        return -1;
    }

    // ───────────── Auffällige Programme ─────────────

    private static readonly Dictionary<string, (DateTime Stamp, bool Signed)> SignatureCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sucht Programme, die in Ordnern liegen, in die jeder Benutzer schreiben kann (AppData, Temp, Downloads …),
    /// und keine gültige digitale Signatur haben – und die mit Windows starten, gerade laufen oder ins Internet gehen.
    /// Das ist nicht automatisch Schadsoftware, aber genau das Muster, das man prüfen sollte.
    /// </summary>
    public static List<Suspect> FindSuspects()
    {
        var reasons = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string reason)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            if (!reasons.TryGetValue(path, out var set)) reasons[path] = set = [];
            set.Add(reason);
        }

        try
        {
            foreach (var e in AutostartService.LoadAll())
                if (e.IsEnabled && !e.FileMissing) Add(e.ExePath, "autostart");
        }
        catch { /* egal */ }

        var pidPaths = new Dictionary<int, string>();
        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                var path = NetNative.ProcessPath(proc.Id);
                if (path.Length == 0) continue;
                pidPaths[proc.Id] = path;
                Add(path, "running");
            }
        }

        try
        {
            foreach (var s in NetNative.Sockets())
                if (!s.Udp && s.State == TcpState.Established && !System.Net.IPAddress.IsLoopback(s.RemoteIp) && !NetNative.IsLocal(s.RemoteIp)
                    && pidPaths.TryGetValue(s.Pid, out var p))
                    Add(p, "internet");
        }
        catch { /* egal */ }

        var result = new List<Suspect>();
        foreach (var (path, why) in reasons)
        {
            var location = RiskyLocation(path);
            if (location == null) continue;
            if (IsSigned(path)) continue;
            var list = new List<string> { "unsigned", location };
            list.AddRange(why.OrderBy(w => w));
            int risk = (location is "temp" or "downloads" ? 3 : 1) + (why.Contains("autostart") ? 2 : 0) + (why.Contains("internet") ? 2 : 0);
            result.Add(new Suspect(path, Path.GetFileName(path), list, risk));
        }
        return result.OrderByDescending(s => s.Risk).ThenBy(s => s.Name).ToList();
    }

    /// <summary>Ordner, in die Programme ohne Admin-Rechte schreiben können. null = Programm- oder Systemordner.</summary>
    private static string? RiskyLocation(string path)
    {
        string P(Environment.SpecialFolder f) => Environment.GetFolderPath(f).TrimEnd('\\') + "\\";
        var temp = Path.GetTempPath();
        var profile = P(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads") + "\\";
        if (path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)) return "temp";
        if (path.StartsWith(downloads, StringComparison.OrdinalIgnoreCase)) return "downloads";
        if (path.StartsWith(P(Environment.SpecialFolder.Desktop), StringComparison.OrdinalIgnoreCase)) return "desktop";
        if (path.StartsWith(P(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase)) return "programdata";
        if (path.StartsWith(@"C:\Users\Public\", StringComparison.OrdinalIgnoreCase)) return "public";
        if (path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)) return "userfolder";
        return null;
    }

    private static bool IsSigned(string path)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (SignatureCache)
                if (SignatureCache.TryGetValue(path, out var c) && c.Stamp == stamp) return c.Signed;
            bool signed = PawnIoService.VerifySignature(path, out _);
            lock (SignatureCache) SignatureCache[path] = (stamp, signed);
            return signed;
        }
        catch { return true; } // im Zweifel nicht als auffällig melden
    }
}
