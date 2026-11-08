using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace EZTool.Services;

/// <summary>
/// Tiefenreinigung über die Windows-eigenen Werkzeuge. Diese räumen Systemdateien auf, die man niemals
/// "von Hand" löschen sollte – deshalb überlässt EZTool die eigentliche Arbeit DISM bzw. der Datenträgerbereinigung.
/// </summary>
public static partial class CleanerService
{
    private static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static string SystemDrive => Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    private static long FreeSpace()
    {
        try { return new DriveInfo(SystemDrive).AvailableFreeSpace; }
        catch { return 0; }
    }

    private static IEnumerable<CleanRule> DeepCleanRules()
    {
        if (!IsAdmin) yield break;

        // ── Komponentenspeicher (WinSxS) ──
        yield return new CleanRule
        {
            Id = "winsxs", Category = "Tiefenreinigung", Icon = "\uE895", DefaultOn = false, SizeUnknown = true,
            Name = "Alte Windows-Updates (Komponentenspeicher)",
            Description = "Entfernt durch neuere Updates ersetzte Systemkomponenten über DISM. Sicher, dauert aber 5–20 Minuten. " +
                          "Bereits installierte Updates bleiben deinstallierbar.",
            Available = () => true,
            CustomScan = _ => new ScanResult(),
            CustomClean = _ =>
            {
                var before = FreeSpace();
                var dism = Path.Combine(Environment.SystemDirectory, "Dism.exe");
                using var p = Process.Start(new ProcessStartInfo(dism, "/Online /Cleanup-Image /StartComponentCleanup /Quiet")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
                // Bewusst ohne Abbruch: DISM darf nie mitten in der Arbeit beendet werden.
                p?.WaitForExit();
                var freed = Math.Max(0, FreeSpace() - before);
                return new CleanResult { FreedBytes = freed, Deleted = p?.ExitCode == 0 ? 1 : 0, Skipped = p?.ExitCode == 0 ? 0 : 1 };
            }
        };

        // ── Vorherige Windows-Installation ──
        var old = Path.Combine(SystemDrive, "Windows.old");
        yield return new CleanRule
        {
            Id = "windows-old", Category = "Tiefenreinigung", Icon = "\uE7B8", DefaultOn = false,
            Name = "Vorherige Windows-Installation (Windows.old)",
            Description = "Bleibt nach einem großen Windows-Update zurück. Danach ist kein Zurücksetzen auf die alte Version mehr möglich.",
            Available = () => Directory.Exists(old),
            CustomScan = token =>
            {
                var r = new ScanResult();
                try
                {
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var f in new DirectoryInfo(old).EnumerateFiles("*", opts))
                    {
                        token.ThrowIfCancellationRequested();
                        r.Bytes += f.Length;
                        r.Files++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* teilweise nicht lesbar */ }
                return r;
            },
            CustomClean = _ => RunDiskCleanup("Previous Installations")
        };
    }

    /// <summary>
    /// Führt eine einzelne Kategorie der Windows-Datenträgerbereinigung (cleanmgr) aus.
    /// Die Auswahl wird nur für diesen Lauf gesetzt und danach wieder entfernt.
    /// </summary>
    private static CleanResult RunDiskCleanup(string handler)
    {
        const string flag = "StateFlags0777";
        var key = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\{handler}";
        var before = FreeSpace();
        int exit = -1;
        try
        {
            using (var k = Registry.LocalMachine.OpenSubKey(key, true))
            {
                if (k == null) return new CleanResult { Skipped = 1 };
                k.SetValue(flag, 2, RegistryValueKind.DWord);
            }

            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cleanmgr.exe"), "/sagerun:777")
            {
                UseShellExecute = false,
            });
            p?.WaitForExit();
            exit = p?.ExitCode ?? -1;
        }
        finally
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(key, true);
                k?.DeleteValue(flag, false);
            }
            catch { /* egal */ }
        }

        var freed = Math.Max(0, FreeSpace() - before);
        return new CleanResult { FreedBytes = freed, Deleted = exit == 0 ? 1 : 0, Skipped = exit == 0 ? 0 : 1 };
    }
}
