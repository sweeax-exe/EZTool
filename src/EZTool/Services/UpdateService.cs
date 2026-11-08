using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace EZTool.Services;

public sealed record UpdateInfo(Version Version, string Notes, string ZipUrl, string? ShaUrl, string PageUrl);

/// <summary>
/// Updates über GitHub-Releases: fragt die neueste Version ab, lädt das Paket, prüft die SHA-256-Prüfsumme,
/// ersetzt die Programmdateien nach dem Beenden und startet EZTool neu.
/// </summary>
public sealed class UpdateService
{
    public static UpdateService Instance { get; } = new();

    /// <summary>GitHub-Repository "besitzer/name".</summary>
    public const string Repo = "sweeax-exe/EZTool";
    public const string RepoUrl = "https://github.com/" + Repo;
    private const string AssetName = "EZTool-win-x64.zip";
    private const string ShaName = "SHA256SUMS.txt";

    public static Version Current { get; } = typeof(App).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    /// <summary>Entwickler-Build (bin\…) – dort wird nichts automatisch ersetzt.</summary>
    public static bool IsDevBuild => (Environment.ProcessPath ?? "").Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    public UpdateInfo? Available { get; private set; }
    public DateTime? LastCheck { get; private set; }
    public event Action<UpdateInfo?>? Changed;

    private System.Threading.Timer? _timer;

    /// <summary>Automatische Prüfung: 30 s nach dem Start, danach alle 6 Stunden.</summary>
    public void StartAutoCheck() =>
        _timer = new System.Threading.Timer(_ => Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            if (!SettingsService.Current.AutoUpdateCheck) return;
            var info = await CheckAsync();
            if (info == null) return;

            // Unauffällig installieren, wenn EZTool nur im Infobereich läuft und kein Spiel aktiv ist
            bool idle = Application.Current?.MainWindow is { IsVisible: false } && !GamingService.Instance.IsActive;
            if (SettingsService.Current.AutoUpdateInstall && idle && !IsDevBuild)
            {
                var (ok, _) = await InstallAsync(info, null, restartHidden: true);
                if (ok) return;
            }
            TrayService.Instance?.Notify($"EZTool {info.Version} ist da", "Klicke hier, um das Update zu installieren.", openModule: "settings");
        }), null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(6));

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"EZTool/{Current}");
        return http;
    }

    /// <summary>Fragt GitHub nach der neuesten Version. Gibt sie zurück, wenn sie neuer ist.</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var http = NewClient();
            using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            LastCheck = DateTime.Now;
            if (!resp.IsSuccessStatusCode)
            {
                Available = null;
                Changed?.Invoke(null);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
            if (!Version.TryParse(tag, out var version)) return null;

            string? zip = null, sha = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (name == AssetName) zip = url;
                if (name == ShaName) sha = url;
            }

            var info = zip != null && version > Current
                ? new UpdateInfo(version, root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "", zip, sha,
                    root.GetProperty("html_url").GetString() ?? RepoUrl + "/releases")
                : null;
            Available = info;
            Changed?.Invoke(info);
            return info;
        }
        catch
        {
            LastCheck = DateTime.Now;
            return null;
        }
    }

    /// <summary>Lädt, prüft und installiert das Update. EZTool beendet sich dabei und startet neu.</summary>
    public async Task<(bool Ok, string Message)> InstallAsync(UpdateInfo info, IProgress<string>? progress, bool restartHidden = false)
    {
        if (IsDevBuild) return (false, "Dies ist ein Entwickler-Build – bitte die Version von GitHub verwenden.");
        var exe = Environment.ProcessPath;
        var installDir = Path.GetDirectoryName(exe);
        if (exe == null || installDir == null) return (false, "Programmpfad unbekannt.");

        var work = Path.Combine(Path.GetTempPath(), "EZTool-Update-" + info.Version);
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            var zipPath = Path.Combine(work, AssetName);

            using var http = NewClient();
            progress?.Report("Lade Update herunter…");
            await using (var src = await http.GetStreamAsync(info.ZipUrl))
            await using (var dst = File.Create(zipPath))
                await src.CopyToAsync(dst);

            // Prüfsumme kontrollieren – ein beschädigter oder manipulierter Download wird verworfen
            if (info.ShaUrl != null)
            {
                progress?.Report("Prüfe Prüfsumme…");
                var sums = await http.GetStringAsync(info.ShaUrl);
                var expected = sums.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.EndsWith(AssetName, StringComparison.OrdinalIgnoreCase))?.Split(' ')[0];
                await using var fs = File.OpenRead(zipPath);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                if (expected == null || !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return (false, "Die Prüfsumme des Downloads stimmt nicht. Das Update wurde nicht installiert.");
            }

            progress?.Report("Entpacke…");
            var newDir = Path.Combine(work, "neu");
            ZipFile.ExtractToDirectory(zipPath, newDir);
            if (!File.Exists(Path.Combine(newDir, "EZTool.exe"))) return (false, "Das Update-Paket ist unvollständig.");

            // Kleines Skript: wartet auf das Ende von EZTool, kopiert die neuen Dateien, startet neu
            var script = Path.Combine(work, "update.cmd");
            int pid = Environment.ProcessId;
            var args = restartHidden ? "--tray" : "";
            File.WriteAllText(script, $"""
                @echo off
                :wait
                tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)
                robocopy "{newDir}" "{installDir}" /E /R:5 /W:1 /NFL /NDL /NJH /NJS >nul
                start "" "{exe}" {args}
                """, Encoding.ASCII);

            progress?.Report("Starte neu…");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false });
            Application.Current?.Dispatcher.BeginInvoke(() => ((App)Application.Current).ExitApp());
            return (true, "Update wird installiert – EZTool startet gleich neu.");
        }
        catch (Exception ex)
        {
            return (false, "Update fehlgeschlagen: " + ex.Message);
        }
    }
}
