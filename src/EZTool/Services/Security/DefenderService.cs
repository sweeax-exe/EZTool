using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace EZTool.Services.Security;

public sealed record DefenderStatus(
    bool Available, bool AntivirusEnabled, bool RealTime, bool BehaviorMonitor, bool TamperProtected, string RunningMode,
    DateTime? SignaturesUpdated, string SignatureVersion, string EngineVersion,
    DateTime? QuickScanEnd, DateTime? FullScanEnd, string? OtherAntivirus);

public sealed record DefenderPrefs(
    IReadOnlyDictionary<Guid, int> AsrRules, int ControlledFolderAccess, int Pua, int NetworkProtection, int CloudBlockLevel, int MapsReporting);

public sealed record ThreatInfo(long Id, string Name, int Severity, bool Active, bool Executed, DateTime? Detected, IReadOnlyList<string> Files, string Status);

public sealed record AsrRule(Guid Id, string Key, bool Strict);

/// <summary>
/// Anbindung an Microsoft Defender: Status und Einstellungen über WMI lesen, Scans über MpCmdRun.exe,
/// Änderungen über die offiziellen PowerShell-Befehle (Set-MpPreference …). EZTool hat keinen eigenen
/// Scanner – die Erkennung kommt von Defender.
/// </summary>
public static class DefenderService
{
    private const string Ns = @"root\Microsoft\Windows\Defender";

    /// <summary>
    /// Regeln zur Verringerung der Angriffsfläche (ASR). Strict = kann seltene Programme oder Admin-Werkzeuge
    /// blockieren, deshalb nicht in der Empfehlung enthalten.
    /// </summary>
    public static readonly AsrRule[] AsrRules =
    [
        new(new("be9ba2d9-53ea-4cdc-84e5-9b1eeee46550"), "Email", false),
        new(new("d4f940ab-401b-4efc-aadc-ad5f3c50688a"), "OfficeChild", false),
        new(new("3b576869-a4ec-4529-8536-b80a7769e899"), "OfficeExe", false),
        new(new("75668c1f-73b5-4cf0-bb93-3ecf5cb7cc84"), "OfficeInject", false),
        new(new("26190899-1602-49e8-8b27-eb1d0a1ce869"), "OutlookChild", false),
        new(new("92e97fa1-2edf-4476-bdd6-9dd0b4dddc7b"), "Win32Macro", false),
        new(new("d3e037e1-3eb8-44c8-a917-57927947596d"), "ScriptDownload", false),
        new(new("5beb7efe-fd9a-4556-801d-275e5ffc04cc"), "Obfuscated", false),
        new(new("7674ba52-37eb-4a4f-a9a1-f0f9a1619a2c"), "AdobeChild", false),
        new(new("9e6c4e1f-7d60-472f-ba1a-a39ef669e4b2"), "Lsass", false),
        new(new("e6db77e5-3df2-4cf1-b95a-636979351e5b"), "WmiPersistence", false),
        new(new("b2b3f03d-6a65-4f7b-a9c7-1c7ef74a9ba4"), "Usb", false),
        new(new("56a863a9-875e-4185-98a7-b882c64b5ce5"), "VulnerableDrivers", false),
        new(new("c1db55ab-c21a-4637-bb3f-a12568109d35"), "Ransomware", false),
        new(new("01443614-cd74-433a-b99e-2ecdc07bfc25"), "Prevalence", true),
        new(new("d1e49aac-8f56-4280-b9ba-993a6d77406c"), "PsExec", true),
    ];

    // ───────────── Lesen ─────────────

    public static DefenderStatus Status()
    {
        string? other = OtherAntivirus();
        try
        {
            using var s = new ManagementObjectSearcher(Ns, "SELECT * FROM MSFT_MpComputerStatus");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    return new DefenderStatus(true,
                        Bool(o, "AntivirusEnabled"), Bool(o, "RealTimeProtectionEnabled"), Bool(o, "BehaviorMonitorEnabled"),
                        Bool(o, "IsTamperProtected"), o["AMRunningMode"] as string ?? "",
                        Date(o, "AntivirusSignatureLastUpdated"), o["AntivirusSignatureVersion"] as string ?? "", o["AMEngineVersion"] as string ?? "",
                        Date(o, "QuickScanEndTime"), Date(o, "FullScanEndTime"), other);
                }
            }
        }
        catch { /* Defender entfernt oder WMI kaputt */ }
        return new DefenderStatus(false, false, false, false, false, "", null, "", "", null, null, other);
    }

    public static DefenderPrefs Prefs()
    {
        var asr = new Dictionary<Guid, int>();
        try
        {
            using var s = new ManagementObjectSearcher(Ns, "SELECT * FROM MSFT_MpPreference");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    if (o["AttackSurfaceReductionRules_Ids"] is string[] ids && o["AttackSurfaceReductionRules_Actions"] is byte[] actions)
                        for (int i = 0; i < ids.Length && i < actions.Length; i++)
                            if (Guid.TryParse(ids[i], out var g)) asr[g] = actions[i];
                    return new DefenderPrefs(asr, Int(o, "EnableControlledFolderAccess"), Int(o, "PUAProtection"), Int(o, "EnableNetworkProtection"),
                        Int(o, "CloudBlockLevel"), Int(o, "MAPSReporting"));
                }
            }
        }
        catch { /* nicht verfügbar */ }
        return new DefenderPrefs(asr, 0, 0, 0, 0, 0);
    }

    public static List<ThreatInfo> Threats()
    {
        var list = new List<ThreatInfo>();
        try
        {
            var names = new Dictionary<long, (string Name, int Severity, bool Active, bool Executed)>();
            using (var s = new ManagementObjectSearcher(Ns, "SELECT * FROM MSFT_MpThreat"))
                foreach (ManagementObject o in s.Get())
                    using (o)
                        names[Convert.ToInt64(o["ThreatID"])] = (o["ThreatName"] as string ?? "?", Int(o, "SeverityID"), Bool(o, "IsActive"), Bool(o, "DidThreatExecute"));

            using (var s = new ManagementObjectSearcher(Ns, "SELECT * FROM MSFT_MpThreatDetection"))
                foreach (ManagementObject o in s.Get())
                {
                    using (o)
                    {
                        long id = Convert.ToInt64(o["ThreatID"]);
                        var n = names.TryGetValue(id, out var known) ? known : (Name: "?", Severity: 0, Active: false, Executed: false);
                        var files = (o["Resources"] as string[] ?? []).Select(r => r.StartsWith("file:_", StringComparison.OrdinalIgnoreCase) ? r[6..] : r).ToList();
                        list.Add(new ThreatInfo(id, n.Name, n.Severity, n.Active, n.Executed, Date(o, "InitialDetectionTime"), files, StatusName(Int(o, "ThreatStatusID"))));
                    }
                }
        }
        catch { /* keine Daten */ }
        return list.OrderByDescending(t => t.Detected).ToList();
    }

    private static string StatusName(int id) => id switch
    {
        1 => "Detected", 2 => "Cleaned", 3 => "Quarantined", 4 => "Removed", 5 => "Allowed", 6 => "Blocked",
        102 => "CleanFailed", 103 => "QuarantineFailed", 104 => "RemoveFailed", 106 => "BlockFailed", 107 => "Abandoned",
        _ => "Unknown",
    };

    /// <summary>Anderer registrierter Virenschutz (dann läuft Defender meist im passiven Modus).</summary>
    private static string? OtherAntivirus()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    var name = o["displayName"] as string ?? "";
                    var state = Convert.ToInt32(o["productState"]);
                    bool enabled = ((state >> 12) & 0xF) == 1; // Bits 12–15: 1 = an
                    if (enabled && !name.Contains("Defender", StringComparison.OrdinalIgnoreCase)) return name;
                }
            }
        }
        catch { /* Server-Windows ohne Sicherheitscenter */ }
        return null;
    }

    private static bool Bool(ManagementBaseObject o, string p) { try { return o[p] is true; } catch { return false; } }
    private static int Int(ManagementBaseObject o, string p) { try { return o[p] == null ? 0 : Convert.ToInt32(o[p]); } catch { return 0; } }

    private static DateTime? Date(ManagementBaseObject o, string p)
    {
        try
        {
            return o[p] switch
            {
                string s when s.Length > 0 => ManagementDateTimeConverter.ToDateTime(s),
                DateTime d => d,
                _ => null,
            };
        }
        catch { return null; }
    }

    // ───────────── Ändern (offizielle Defender-Befehle) ─────────────

    /// <summary>Führt einen Defender-PowerShell-Befehl aus. Gibt die Fehlermeldung zurück (null = ok).</summary>
    private static async Task<string?> PowerShellAsync(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return "powershell.exe";
        var errTask = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        var err = (await errTask).Trim();
        return p.ExitCode == 0 && err.Length == 0 ? null : FirstLine(err);
    }

    private static string FirstLine(string s) => s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? s;

    public static Task<string?> SetAsrAsync(Guid id, bool on) =>
        PowerShellAsync($"Add-MpPreference -AttackSurfaceReductionRules_Ids {id} -AttackSurfaceReductionRules_Actions {(on ? "Enabled" : "Disabled")}");

    public static Task<string?> SetAsrManyAsync(IEnumerable<Guid> ids, bool on)
    {
        var list = ids.ToList();
        if (list.Count == 0) return Task.FromResult<string?>(null);
        var actions = string.Join(",", list.Select(_ => on ? "Enabled" : "Disabled"));
        return PowerShellAsync($"Add-MpPreference -AttackSurfaceReductionRules_Ids {string.Join(",", list)} -AttackSurfaceReductionRules_Actions {actions}");
    }

    public static Task<string?> SetPreferenceAsync(string name, int value) => PowerShellAsync($"Set-MpPreference -{name} {value}");

    public static Task<string?> RemoveActiveThreatsAsync() => PowerShellAsync("Remove-MpThreat");

    /// <summary>Offline-Scan: Windows startet neu und prüft den PC, bevor Schadsoftware laden kann.</summary>
    public static Task<string?> StartOfflineScanAsync() => PowerShellAsync("Start-MpWDOScan");

    // ───────────── Scans (MpCmdRun) ─────────────

    public static string MpCmdRun
    {
        get
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows Defender");
                if (k?.GetValue("InstallLocation") is string dir)
                {
                    var p = Path.Combine(dir, "MpCmdRun.exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch { /* Standardpfad */ }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        }
    }

    public sealed record ScanResult(bool Ok, bool ThreatsFound, string Output);

    /// <summary>scanType: 1 = Schnell, 2 = Vollständig, 3 = Datei/Ordner (path).</summary>
    public static async Task<ScanResult> ScanAsync(int scanType, string? path, CancellationToken ct)
    {
        var args = scanType == 3 ? $"-Scan -ScanType 3 -File \"{path}\"" : $"-Scan -ScanType {scanType}";
        return await RunAsync(args, ct, cancelArgs: "-Cancel");
    }

    public static Task<ScanResult> UpdateSignaturesAsync(CancellationToken ct) => RunAsync("-SignatureUpdate", ct, null);

    private static async Task<ScanResult> RunAsync(string args, CancellationToken ct, string? cancelArgs)
    {
        var psi = new ProcessStartInfo(MpCmdRun, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return new ScanResult(false, false, "MpCmdRun.exe");
        var outTask = p.StandardOutput.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Laufenden Scan sauber bei Defender abbrechen, nicht nur MpCmdRun beenden
            if (cancelArgs != null)
            {
                try { using var c = Process.Start(new ProcessStartInfo(MpCmdRun, cancelArgs) { CreateNoWindow = true, UseShellExecute = false }); c?.WaitForExit(10000); }
                catch { /* egal */ }
            }
            try { p.Kill(); } catch { /* schon beendet */ }
            throw;
        }
        var output = (await outTask).Trim();
        // MpCmdRun: 0 = sauber, 2 = Bedrohung gefunden (und behandelt)
        return new ScanResult(p.ExitCode is 0 or 2, p.ExitCode == 2, output);
    }
}
