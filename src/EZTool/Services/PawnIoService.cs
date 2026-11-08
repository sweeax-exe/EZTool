using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Win32;

namespace EZTool.Services;

/// <summary>
/// PawnIO ist der (signierte) Treiber, über den LibreHardwareMonitor CPU-Temperaturen, -Takt und -Leistung liest.
/// EZTool lädt auf Wunsch den offiziellen Installer von GitHub, prüft die digitale Signatur und startet ihn –
/// genauso wie es LibreHardwareMonitor selbst macht.
/// </summary>
public static class PawnIoService
{
    private const string LatestApi = "https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest";
    private const string FallbackUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
    public const string ProjectUrl = "https://github.com/namazso/PawnIO.Setup";

    private static readonly Version MinimumVersion = new(2, 0, 0, 0);

    /// <summary>Installierte Version (null = nicht installiert). Wird bei jedem Aufruf frisch gelesen.</summary>
    public static Version? InstalledVersion
    {
        get
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var k = bk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                    if (Version.TryParse(k?.GetValue("DisplayVersion") as string, out var v)) return v;
                }
                catch { /* weiter */ }
            }
            return null;
        }
    }

    public static bool IsInstalled => InstalledVersion != null;
    public static bool NeedsUpdate => InstalledVersion is { } v && v < MinimumVersion;

    public sealed record Result(bool Success, string Message);

    public static async Task<Result> InstallAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "EZTool");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "PawnIO_setup.exe");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EZTool/" + typeof(PawnIoService).Assembly.GetName().Version?.ToString(3));

        // 1) Download-Adresse der neuesten offiziellen Version ermitteln
        progress?.Report("Suche aktuelle PawnIO-Version…");
        var url = FallbackUrl;
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi, token));
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == "PawnIO_setup.exe")
                {
                    var u = asset.GetProperty("browser_download_url").GetString();
                    if (u != null && u.StartsWith("https://github.com/namazso/PawnIO.Setup/releases/download/", StringComparison.Ordinal)) url = u;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Fallback-Adresse verwenden */ }

        // 2) Herunterladen
        progress?.Report("Lade offiziellen Installer…");
        try
        {
            await using var src = await http.GetStreamAsync(url, token);
            await using var dst = File.Create(file);
            await src.CopyToAsync(dst, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new Result(false, "Download fehlgeschlagen: " + ex.Message);
        }

        // 3) Digitale Signatur prüfen – ohne gültige Signatur wird nichts ausgeführt
        progress?.Report("Prüfe digitale Signatur…");
        if (!VerifySignature(file, out var signer))
        {
            TryDelete(file);
            return new Result(false, "Die digitale Signatur des Installers ist ungültig. Aus Sicherheitsgründen wurde nichts installiert.");
        }

        // 4) Offiziellen Installer starten (er zeigt seinen eigenen Dialog)
        progress?.Report("Installer läuft…");
        int exit;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(file, "-install") { UseShellExecute = true })
                             ?? throw new InvalidOperationException("Installer konnte nicht gestartet werden.");
            await proc.WaitForExitAsync(token);
            exit = proc.ExitCode;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new Result(false, "Installer konnte nicht gestartet werden: " + ex.Message);
        }
        finally
        {
            TryDelete(file);
        }

        if (!IsInstalled) return new Result(false, $"PawnIO wurde nicht installiert (Code {exit}).");

        // 5) Sensoren neu initialisieren – ab jetzt gibt es CPU-Temperaturen
        HardwareService.Instance.Reinitialize();
        var msg = $"PawnIO {InstalledVersion} wurde installiert (signiert von {signer}). CPU-Temperatur, -Takt und -Leistung erscheinen in wenigen Sekunden.";
        if (exit == 3010) msg += "\n\nWindows meldet, dass ein Neustart nötig ist, damit alles greift.";
        return new Result(true, msg);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* egal */ }
    }

    // ───────────── Authenticode-Prüfung (WinVerifyTrust) ─────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>Prüft, ob die Datei eine gültige, vertrauenswürdige Authenticode-Signatur hat.</summary>
    public static bool VerifySignature(string file, out string signer)
    {
        signer = "";
        var path = Marshal.StringToCoTaskMemUni(file);
        var fileInfo = IntPtr.Zero;
        var data = IntPtr.Zero;
        try
        {
            var fi = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
            fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fi, fileInfo, false);

            var wd = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = fileInfo,
            };
            data = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            Marshal.StructureToPtr(wd, data, false);

            if (WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, data) != 0) return false;

#pragma warning disable SYSLIB0057
            var cert = X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            signer = new X509Certificate2(cert).GetNameInfo(X509NameType.SimpleName, false);
            return true;
        }
        catch { return false; }
        finally
        {
            Marshal.FreeCoTaskMem(path);
            if (fileInfo != IntPtr.Zero) Marshal.FreeHGlobal(fileInfo);
            if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
        }
    }

    /// <summary>Gemeinsamer Ablauf mit Rückfrage und Ergebnis-Dialog (Dashboard, Einstellungen, Start-Hinweis).</summary>
    public static async Task InstallWithDialogsAsync()
    {
        var update = NeedsUpdate;
        if (!Dialogs.NeonDialog.Show(update ? "PawnIO aktualisieren?" : "PawnIO installieren?",
                "Für CPU-Temperatur, -Takt und -Leistung braucht EZTool den kostenlosen Open-Source-Treiber PawnIO " +
                "(wird auch von LibreHardwareMonitor genutzt).\n\n" +
                "EZTool lädt dafür den offiziellen Installer (ca. 3,4 MB) von github.com/namazso/PawnIO.Setup, " +
                "prüft dessen digitale Signatur und startet ihn. Der Installer zeigt danach seinen eigenen Dialog.",
                update ? "Aktualisieren" : "Installieren"))
            return;

        Result result;
        try { result = await InstallAsync(); }
        catch (Exception ex) { result = new Result(false, ex.Message); }

        Dialogs.NeonDialog.Show(result.Success ? "PawnIO ist bereit" : "Installation nicht abgeschlossen", result.Message,
            cancelText: null, danger: !result.Success);
    }
}
