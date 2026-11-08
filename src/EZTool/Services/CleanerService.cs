using System.Diagnostics;
using EZTool.Core;

namespace EZTool.Services;

/// <summary>Beschreibt eine Kategorie von Dateien, die gefahrlos entfernt werden kann.</summary>
public sealed class CleanRule
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public string Icon { get; init; } = "";
    public bool DefaultOn { get; init; } = true;

    /// <summary>Ordner (oder einzelne Dateien), die durchsucht werden.</summary>
    public Func<IEnumerable<string>> Roots { get; init; } = () => [];

    /// <summary>Dateimuster, z. B. "*" oder "thumbcache_*.db".</summary>
    public string Pattern { get; init; } = "*";

    /// <summary>Nur Dateien, die älter sind als diese Zeitspanne (Schutz für gerade benutzte Dateien).</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;

    public bool Recursive { get; init; } = true;
    public bool RemoveEmptyFolders { get; init; } = true;
    public bool IsRecycleBin { get; init; }

    /// <summary>Prozesse, die laufend Dateien sperren (z. B. Browser).</summary>
    public string[] Processes { get; init; } = [];

    /// <summary>Eigene Analyse statt Dateisuche (z. B. Windows.old).</summary>
    public Func<CancellationToken, ScanResult>? CustomScan { get; init; }

    /// <summary>Eigene Bereinigung über ein Windows-Werkzeug (DISM, Datenträgerbereinigung).</summary>
    public Func<CancellationToken, CleanResult>? CustomClean { get; init; }

    /// <summary>Größe lässt sich vorab nicht bestimmen – wird nach der Bereinigung gemessen.</summary>
    public bool SizeUnknown { get; init; }

    public Func<bool>? Available { get; init; }

    public bool IsAvailable() => Available?.Invoke() ?? (IsRecycleBin || Roots().Any(p => Directory.Exists(p) || File.Exists(p)));
}

public sealed class ScanResult
{
    public long Bytes { get; set; }
    public int Files { get; set; }
    public List<(string Path, long Size)> Items { get; } = [];
}

public sealed class CleanResult
{
    public long FreedBytes { get; set; }
    public int Deleted { get; set; }
    public int Skipped { get; set; }
}

public static partial class CleanerService
{
    private static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
    private static string Local => Env(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Env(Environment.SpecialFolder.ApplicationData);
    private static string Windows => Env(Environment.SpecialFolder.Windows);
    private static string ProgramData => Env(Environment.SpecialFolder.CommonApplicationData);

    public static IReadOnlyList<CleanRule> GetRules()
    {
        var rules = new List<CleanRule>
        {
            new()
            {
                Id = "user-temp", Category = "System", Icon = "",
                Name = "Temporäre Dateien (Benutzer)",
                Description = "Reste von Installationen und Programmen im TEMP-Ordner. Nur Dateien älter als 24 Stunden.",
                Roots = () => [Path.GetTempPath()], MinAge = TimeSpan.FromHours(24)
            },
            new()
            {
                Id = "win-temp", Category = "System", Icon = "",
                Name = "Temporäre Dateien (Windows)",
                Description = "Systemweiter Temp-Ordner von Windows. Nur Dateien älter als 24 Stunden.",
                Roots = () => [Path.Combine(Windows, "Temp")], MinAge = TimeSpan.FromHours(24)
            },
            new()
            {
                Id = "wu-cache", Category = "System", Icon = "",
                Name = "Windows-Update-Cache",
                Description = "Bereits installierte Update-Pakete. Windows lädt sie bei Bedarf erneut.",
                Roots = () => [Path.Combine(Windows, "SoftwareDistribution", "Download")], MinAge = TimeSpan.FromDays(2)
            },
            new()
            {
                Id = "delivery-opt", Category = "System", Icon = "",
                Name = "Übermittlungsoptimierung",
                Description = "Zwischengespeicherte Update-Dateien für die Peer-to-Peer-Verteilung.",
                Roots = () => [Path.Combine(Windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")]
            },
            new()
            {
                Id = "crash", Category = "System", Icon = "",
                Name = "Absturzberichte & Speicherabbilder",
                Description = "Fehlerberichte (WER), Minidumps und Crash-Dumps von Programmen.",
                Roots = () =>
                [
                    Path.Combine(Local, "CrashDumps"),
                    Path.Combine(Windows, "Minidump"),
                    Path.Combine(Windows, "MEMORY.DMP"),
                    Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"),
                    Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"),
                    Path.Combine(Local, @"Microsoft\Windows\WER\ReportArchive"),
                    Path.Combine(Local, @"Microsoft\Windows\WER\ReportQueue"),
                ]
            },
            new()
            {
                Id = "thumbs", Category = "System", Icon = "", DefaultOn = false,
                Name = "Miniaturansichten-Cache",
                Description = "Vorschaubilder des Explorers. Werden automatisch neu erstellt.",
                Roots = () => [Path.Combine(Local, @"Microsoft\Windows\Explorer")], Pattern = "thumbcache_*.db",
                Recursive = false, RemoveEmptyFolders = false
            },
            new()
            {
                Id = "win-logs", Category = "System", Icon = "", DefaultOn = false,
                Name = "Windows-Protokolle",
                Description = "Alte Log-Dateien von Windows-Komponenten (älter als 14 Tage).",
                Roots = () => [Path.Combine(Windows, "Logs")], Pattern = "*.log", MinAge = TimeSpan.FromDays(14),
                RemoveEmptyFolders = false
            },
            new()
            {
                Id = "recycle", Category = "System", Icon = "", DefaultOn = false, IsRecycleBin = true,
                Name = "Papierkorb",
                Description = "Leert den Papierkorb auf allen Laufwerken endgültig."
            },
        };

        // ── Browser: nur Cache – niemals Verlauf, Passwörter oder Cookies ──
        foreach (var (name, dir, proc) in new[]
                 {
                     ("Google Chrome", Path.Combine(Local, @"Google\Chrome\User Data"), "chrome"),
                     ("Microsoft Edge", Path.Combine(Local, @"Microsoft\Edge\User Data"), "msedge"),
                     ("Brave", Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"), "brave"),
                     ("Vivaldi", Path.Combine(Local, @"Vivaldi\User Data"), "vivaldi"),
                     ("Opera", Path.Combine(Local, @"Opera Software\Opera Stable"), "opera"),
                     ("Opera GX", Path.Combine(Local, @"Opera Software\Opera GX Stable"), "opera"),
                 })
        {
            rules.Add(new CleanRule
            {
                Id = "browser-" + name, Category = "Browser", Icon = "",
                Name = $"{name} – Cache",
                Description = "Zwischengespeicherte Webseiten-Daten. Verlauf, Passwörter und Cookies bleiben unangetastet.",
                Processes = [proc],
                Roots = () => ChromiumCacheDirs(dir)
            });
        }

        rules.Add(new CleanRule
        {
            Id = "browser-firefox", Category = "Browser", Icon = "",
            Name = "Mozilla Firefox – Cache",
            Description = "Zwischengespeicherte Webseiten-Daten. Verlauf, Passwörter und Cookies bleiben unangetastet.",
            Processes = ["firefox"],
            Roots = () => SubDirs(Path.Combine(Local, @"Mozilla\Firefox\Profiles")).Select(p => Path.Combine(p, "cache2"))
        });

        // ── Anwendungen ──
        rules.Add(new CleanRule
        {
            Id = "apps-cache", Category = "Anwendungen", Icon = "",
            Name = "App-Caches (Discord, Steam, VS Code, Teams …)",
            Description = "Web-Caches von Electron- und Chromium-basierten Programmen.",
            Processes = ["discord", "steamwebhelper", "code", "ms-teams", "slack"],
            Roots = () => new[]
            {
                @"discord\Cache", @"discord\Code Cache", @"discord\GPUCache",
                @"Code\Cache", @"Code\CachedData", @"Code\Code Cache", @"Code\GPUCache",
                @"Slack\Cache", @"Slack\Code Cache", @"Slack\GPUCache",
                @"Microsoft\Teams\Cache", @"Microsoft\Teams\Code Cache", @"Microsoft\Teams\GPUCache",
            }.Select(p => Path.Combine(Roaming, p))
                .Append(Path.Combine(Local, @"Steam\htmlcache"))
        });

        rules.Add(new CleanRule
        {
            Id = "shader", Category = "Anwendungen", Icon = "", DefaultOn = false,
            Name = "Grafik-Shader-Cache",
            Description = "DirectX-, NVIDIA- und AMD-Shader. Wird neu aufgebaut – Spiele laden beim ersten Start evtl. etwas länger.",
            Roots = () =>
            [
                Path.Combine(Local, "D3DSCache"),
                Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"),
                Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"), Path.Combine(Local, @"AMD\GLCache"),
            ]
        });

        rules.Add(new CleanRule
        {
            Id = "dev-cache", Category = "Anwendungen", Icon = "", DefaultOn = false,
            Name = "Entwickler-Caches (npm, pip, Yarn)",
            Description = "Paket-Caches von Entwicklerwerkzeugen. Pakete werden bei Bedarf neu geladen.",
            Roots = () =>
            [
                Path.Combine(Local, "npm-cache"),
                Path.Combine(Local, @"pip\Cache"),
                Path.Combine(Local, @"Yarn\Cache"),
            ]
        });

        rules.AddRange(DeepCleanRules());
        return rules.Where(r => r.IsAvailable()).ToList();
    }

    private static IEnumerable<string> SubDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch { return []; }
    }

    private static IEnumerable<string> ChromiumCacheDirs(string userData)
    {
        if (!Directory.Exists(userData)) yield break;

        // Opera speichert das Profil direkt im Hauptordner.
        var profiles = SubDirs(userData)
            .Where(p => Path.GetFileName(p) is "Default" || Path.GetFileName(p).StartsWith("Profile ", StringComparison.Ordinal))
            .Append(userData);

        foreach (var profile in profiles)
        {
            yield return Path.Combine(profile, "Cache");
            yield return Path.Combine(profile, "Code Cache");
            yield return Path.Combine(profile, "GPUCache");
        }
        yield return Path.Combine(userData, "ShaderCache");
        yield return Path.Combine(userData, "GrShaderCache");
    }

    public static bool IsAnyRunning(CleanRule rule) =>
        rule.Processes.Any(p =>
        {
            try { return Process.GetProcessesByName(p).Length > 0; }
            catch { return false; }
        });

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    public static ScanResult Scan(CleanRule rule, CancellationToken token = default)
    {
        if (rule.CustomScan != null) return rule.CustomScan(token);
        var result = new ScanResult();
        if (rule.IsRecycleBin)
        {
            var (size, items) = Native.QueryRecycleBin();
            result.Bytes = size;
            result.Files = (int)Math.Min(items, int.MaxValue);
            return result;
        }

        var cutoff = DateTime.Now - rule.MinAge;
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = rule.Recursive,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        foreach (var root in rule.Roots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(root))
                {
                    var fi = new FileInfo(root);
                    if (fi.LastWriteTime <= cutoff) Add(result, fi);
                    continue;
                }
                if (!Directory.Exists(root)) continue;

                foreach (var fi in new DirectoryInfo(root).EnumerateFiles(rule.Pattern, opts))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (rule.MinAge == TimeSpan.Zero || fi.LastWriteTime <= cutoff) Add(result, fi);
                    }
                    catch { /* Datei verschwunden */ }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Ordner nicht lesbar */ }
        }
        return result;
    }

    private static void Add(ScanResult r, FileInfo fi)
    {
        r.Items.Add((fi.FullName, fi.Length));
        r.Bytes += fi.Length;
        r.Files++;
    }

    public static CleanResult Clean(CleanRule rule, ScanResult scan, CancellationToken token = default)
    {
        if (rule.CustomClean != null) return rule.CustomClean(token);
        var result = new CleanResult();
        if (rule.IsRecycleBin)
        {
            if (Native.EmptyRecycleBin())
            {
                result.FreedBytes = scan.Bytes;
                result.Deleted = scan.Files;
            }
            return result;
        }

        foreach (var (path, size) in scan.Items)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
                File.Delete(path);
                result.FreedBytes += size;
                result.Deleted++;
            }
            catch
            {
                // Datei wird gerade benutzt oder ist geschützt → überspringen.
                result.Skipped++;
            }
        }

        if (rule.RemoveEmptyFolders)
        {
            foreach (var root in rule.Roots().Where(Directory.Exists)) RemoveEmptySubfolders(root);
        }
        return result;
    }

    /// <summary>Löscht leere Unterordner, aber niemals den Wurzelordner selbst.</summary>
    private static void RemoveEmptySubfolders(string root)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*", Options))
            {
                RemoveEmptySubfolders(dir);
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir, false);
                }
                catch { /* gesperrt */ }
            }
        }
        catch { /* nicht lesbar */ }
    }
}
