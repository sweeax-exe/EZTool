using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EZTool.Services;

public sealed partial class FolderEntry : ObservableObject
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public int Files { get; init; }
    public DateTime? LastWrite { get; init; }
    public string Note { get; init; } = "";

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
    public string Parent => System.IO.Path.GetDirectoryName(Path.TrimEnd('\\')) ?? "";

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string _status = "";
}

public static class FolderService
{
    internal const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x400000 /* RECALL_ON_DATA_ACCESS */ | (FileAttributes)0x40000 /* RECALL_ON_OPEN */;

    private static readonly EnumerationOptions Shallow = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS",
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Config.Msi", "MSOCache", "AppData", "WindowsApps",
    };

    /// <summary>Systemordner, die bei der Analyse nie betreten werden.</summary>
    public static bool IsProtected(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        if (SkipNames.Contains(name)) return true;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.StartsWith(win, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────── Große & ungenutzte Ordner ─────────────

    private sealed record Node(long Size, int Files, DateTime Newest, List<FolderEntry> Candidates);

    public static List<FolderEntry> FindStale(string root, int minAgeDays, long minSize, IProgress<string>? progress, CancellationToken token)
    {
        var cutoff = DateTime.Now.AddDays(-minAgeDays);
        var lastReport = DateTime.MinValue;
        var result = Walk(new DirectoryInfo(root), 0);
        return result.Candidates.OrderByDescending(c => c.Size).ToList();

        Node Walk(DirectoryInfo dir, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 120)
            {
                lastReport = DateTime.UtcNow;
                progress.Report(dir.FullName);
            }

            long size = 0;
            int files = 0;
            var newest = DateTime.MinValue;
            var candidates = new List<FolderEntry>();

            try
            {
                foreach (var f in dir.EnumerateFiles("*", Shallow))
                {
                    try
                    {
                        if ((f.Attributes & CloudOnly) == 0) size += f.Length;
                        files++;
                        if (f.LastWriteTime > newest) newest = f.LastWriteTime;
                    }
                    catch { /* egal */ }
                }

                foreach (var sub in dir.EnumerateDirectories("*", Shallow))
                {
                    if (IsSystemSkip(sub)) continue;
                    var child = Walk(sub, depth + 1);
                    size += child.Size;
                    files += child.Files;
                    if (child.Newest > newest) newest = child.Newest;
                    candidates.AddRange(child.Candidates);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Ordner nicht lesbar */ }

            if (newest == DateTime.MinValue)
            {
                try { newest = dir.LastWriteTime; } catch { /* egal */ }
            }

            // Nur bis Ebene 4 vorschlagen: tiefere Ordner sind meist Innereien eines Projekts/Caches,
            // deren teilweises Löschen das Ganze beschädigen würde.
            bool qualifies = depth is >= 1 and <= MaxSuggestDepth && size >= minSize && newest <= cutoff && !IsNoSuggest(dir);
            if (qualifies)
            {
                candidates = [new FolderEntry { Path = dir.FullName, Size = size, Files = files, LastWrite = newest }];
            }
            return new Node(size, files, newest, candidates);
        }
    }

    private const int MaxSuggestDepth = 4;

    internal static bool IsSystemSkip(DirectoryInfo d)
    {
        if (SkipNames.Contains(d.Name)) return true;
        // Punkt-Ordner (.nuget, .cargo, .vscode, .claude …) sind von Tools verwaltet – nie anfassen.
        if (d.Name.StartsWith('.')) return true;
        try
        {
            if ((d.Attributes & FileAttributes.System) != 0 && (d.Attributes & FileAttributes.Hidden) != 0) return true;
        }
        catch { return true; }
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return d.FullName.StartsWith(win, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNoSuggest(DirectoryInfo d)
    {
        // Wichtige Benutzerordner selbst nie vorschlagen (Inhalte schon).
        var full = d.FullName.TrimEnd('\\');
        foreach (var sf in new[]
                 {
                     Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
                 })
        {
            var p = Environment.GetFolderPath(sf).TrimEnd('\\');
            if (p.Length > 0 && full.Equals(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var users = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Users");
        return full.Equals(downloads, StringComparison.OrdinalIgnoreCase)
               || full.Equals(users, StringComparison.OrdinalIgnoreCase)
               || Path.GetDirectoryName(full)?.Equals(users, StringComparison.OrdinalIgnoreCase) == true; // C:\Users\<Name>
    }

    // ───────────── Leere Ordner ─────────────

    public static List<FolderEntry> FindEmpty(string root, IProgress<string>? progress, CancellationToken token)
    {
        var list = new List<FolderEntry>();
        var lastReport = DateTime.MinValue;
        Walk(new DirectoryInfo(root), 0);
        return list;

        bool Walk(DirectoryInfo dir, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 120)
            {
                lastReport = DateTime.UtcNow;
                progress.Report(dir.FullName);
            }

            try
            {
                if (dir.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).Any())
                {
                    foreach (var sub in dir.EnumerateDirectories("*", Shallow))
                        if (!IsSystemSkip(sub) && sub.Name is not ("node_modules" or ".git")) Walk(sub, depth + 1);
                    return false;
                }

                var subs = dir.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList();
                bool allEmpty = true;
                foreach (var sub in subs)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 || IsSystemSkip(sub) || sub.Name is "node_modules" or ".git")
                    {
                        allEmpty = false;
                        continue;
                    }
                    bool empty = Walk(sub, depth + 1);
                    if (!empty) allEmpty = false;
                }

                if (allEmpty && depth >= 1 && !IsNoSuggest(dir))
                {
                    // Unterordner durch diesen (übergeordneten) leeren Ordner ersetzen
                    var prefix = dir.FullName.TrimEnd('\\') + "\\";
                    list.RemoveAll(e => e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                    DateTime? lw = null;
                    try { lw = dir.LastWriteTime; } catch { /* egal */ }
                    list.Add(new FolderEntry { Path = dir.FullName, LastWrite = lw, Note = subs.Count > 0 ? "nur leere Unterordner" : "leer" });
                    return true;
                }
                return allEmpty;
            }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }
    }

    // ───────────── Verwaiste AppData-Ordner ─────────────

    private static readonly HashSet<string> KnownSystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Temp", "Programs", "Comms", "ConnectedDevicesPlatform", "D3DSCache", "CrashDumps", "PlaceholderTileLogoFolder",
        "Publishers", "VirtualStore", "Package Cache", "regid.1991-06.com.microsoft", "ssh", "USOPrivate", "USOShared", "SoftwareDistribution",
        "Microsoft OneDrive", "WindowsHolographicDevices", "Windows", "IsolatedStorage", "History", "Identities", "Application Data", "Desktop",
        "Documents", "Start Menu", "Templates", "Favorites", "Microsoft Help", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "ATI", "Google",
        "Mozilla", "Adobe", "Apple", "Apple Computer", "Sun", "Oracle", "Java", "Realtek", "Dell", "HP", "Lenovo", "ASUS", "MSI", "Gigabyte",
        "Steam", "EZTool", "Comms", "Diagnostics", "GroupPolicy", "Spooler", "ssh", "Sophos", "Avast Software", "AVAST Software", "Avira",
        "Kaspersky Lab", "McAfee", "Norton", "Malwarebytes", "ESET", "Bitdefender", "Docker", "dotnet", "NuGet", "npm", "npm-cache", "pip",
        "Python", "JetBrains", "Code", "GitHub Desktop", "Git", "Packages", "PeerDistRepub", "DeliveryOptimization", "Microsoft Games",
        "PackageManagement", "WindowsPowerShell", "PowerShell", "Downloaded Installations", "fontconfig", "IconCache", "Low", "LocalLow",
        "Roaming", "Local", "assembly", "Common Files", "Licenses", "Desktop Background", "Battle.net", "Epic", "EpicGamesLauncher",
        "Riot Games", "Ubisoft", "Origin", "EA Desktop", "Electronic Arts", "Razer", "Logitech", "LGHUB", "Corsair", "SteelSeries",
        "Claude", "AnthropicClaude", "claude-code", "Anthropic",
        // Windows-Dienste und -Komponenten in ProgramData/AppData
        "Whesvc", "Propagation", "WinGet", "PackagedEventProviders", "DeviceSync", "ClickToRun", "Windows Kits",
        "Windows App Certification Kit", "Windows Defender Advanced Threat Protection", "Feedback Hub", "SquirrelTemp",
        "cache", "Sentry", "BattlEye", "EasyAntiCheat", "Riot Vanguard", "FACEIT", "UnrealEngine", "Unreal Engine",
        "CrashReportClient", "Blizzard Entertainment", "Packages", "ProgramData", "SystemTemp", "Containers", "ssh",
        "MicrosoftEdge", "Edge", "Teams", "OneDrive", "WebView2", "EBWebView", "Diagnosis", "WER", "Device Stage",
    };

    private const long MinOrphanSize = 512 * 1024;

    public static List<FolderEntry> FindOrphans(IReadOnlyCollection<InstalledProgram> installed, int minAgeDays, IProgress<string>? progress, CancellationToken token)
    {
        var tokens = installed
            .SelectMany(p => new[] { p.Name, p.Publisher, Path.GetFileName(p.InstallLocation.TrimEnd('\\')), Path.GetFileNameWithoutExtension(p.IconPath) })
            .Select(Normalize)
            .Where(s => s.Length >= 3)
            .Distinct()
            .ToList();

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var proc in System.Diagnostics.Process.GetProcesses())
            {
                running.Add(Normalize(proc.ProcessName));
                proc.Dispose();
            }
        }
        catch { /* egal */ }

        var cutoff = DateTime.Now.AddDays(-minAgeDays);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bases = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local,
            Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        };

        var result = new List<FolderEntry>();
        foreach (var b in bases.Where(Directory.Exists))
        {
            foreach (var dir in new DirectoryInfo(b).EnumerateDirectories("*", Shallow))
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(dir.FullName);

                var name = dir.Name;
                if (KnownSystemFolders.Contains(name) || name.StartsWith('.') || name.StartsWith('{') || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if ((dir.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0) continue;
                }
                catch { continue; }

                var norm = Normalize(name);
                if (norm.Length < 3 || running.Contains(norm)) continue;
                bool matched = tokens.Any(t => t.Contains(norm) || (norm.Length >= 4 && norm.Contains(t) && t.Length >= 4));
                if (matched) continue;

                // Größe und letzte Änderung
                long size = 0;
                int files = 0;
                var newest = DateTime.MinValue;
                try
                {
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var f in dir.EnumerateFiles("*", opts))
                    {
                        size += f.Length;
                        files++;
                        if (f.LastWriteTime > newest) newest = f.LastWriteTime;
                    }
                }
                catch { /* egal */ }
                if (newest == DateTime.MinValue) newest = dir.LastWriteTime;
                if (newest > cutoff || size < MinOrphanSize) continue;

                result.Add(new FolderEntry
                {
                    Path = dir.FullName,
                    Size = size,
                    Files = files,
                    LastWrite = newest,
                    Note = "kein passendes Programm gefunden"
                });
            }
        }
        return result.OrderByDescending(r => r.Size).ToList();
    }

    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    // ───────────── Löschen ─────────────

    /// <summary>Verschiebt Ordner in den Papierkorb (wiederherstellbar).</summary>
    public static bool Recycle(FolderEntry e)
    {
        try
        {
            if (!Directory.Exists(e.Path)) return true;
            if (IsProtected(e.Path) || Path.GetPathRoot(e.Path)?.TrimEnd('\\').Equals(e.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true) return false;
            return Core.Native.MoveToRecycleBin(e.Path);
        }
        catch { return false; }
    }
}

