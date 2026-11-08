using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EZTool.Core;
using Microsoft.Win32;

namespace EZTool.Services;

public sealed partial class InstalledProgram : ObservableObject
{
    public required string Name { get; init; }
    public string Version { get; init; } = "";
    public string Publisher { get; init; } = "";
    public DateTime? InstallDate { get; init; }
    public string InstallLocation { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string IconPath { get; init; } = "";
    public int IconIndex { get; init; }
    public required RegistryHive Hive { get; init; }
    public required RegistryView View { get; init; }
    public required string KeyName { get; init; }
    public bool IsMsi { get; init; }

    /// <summary>Microsoft-Store-/MSIX-App (KeyName = PackageFullName).</summary>
    public bool IsStore { get; init; }

    /// <summary>Systemrelevante App (z. B. Microsoft Store selbst) – wird nicht zum Deinstallieren angeboten.</summary>
    public bool IsProtected { get; init; }

    public bool CanUninstall => !IsProtected;
    public string Kind => IsStore ? "store" : "desktop";

    public string Scope => IsStore ? Loc.T("Programs.ScopeStore") : Hive == RegistryHive.CurrentUser ? Loc.T("Programs.ScopeUser") : View == RegistryView.Registry32 ? Loc.T("Programs.Scope32") : Loc.T("Programs.Scope64");
    public string RegistryPath => $@"{(Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\{ProgramService.UninstallKey}\{KeyName}";

    [ObservableProperty] private long _size;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private string _status = "";
}

public sealed partial class Leftover : ObservableObject
{
    public required string Path { get; init; }
    public required bool IsRegistry { get; init; }
    public long Size { get; init; }
    public string Kind => IsRegistry ? Loc.T("Leftover.KindRegistry") : Loc.T("Leftover.KindFolder");
    [ObservableProperty] private bool _isChecked = true;
}

public static partial class ProgramService
{
    public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledProgram> LoadAll()
    {
        var list = new List<InstalledProgram>();
        Read(list, RegistryHive.LocalMachine, RegistryView.Registry64);
        Read(list, RegistryHive.LocalMachine, RegistryView.Registry32);
        Read(list, RegistryHive.CurrentUser, RegistryView.Registry64);

        return list
            .GroupBy(p => (p.Name.ToLowerInvariant(), p.Version))
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void Read(List<InstalledProgram> list, RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(UninstallKey);
            if (root == null) return;

            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(name);
                    if (k == null) continue;

                    var display = (k.GetValue("DisplayName") as string)?.Trim();
                    var uninstall = (k.GetValue("UninstallString") as string)?.Trim() ?? "";
                    if (string.IsNullOrEmpty(display) || string.IsNullOrEmpty(uninstall)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") != null) continue;
                    var release = k.GetValue("ReleaseType") as string ?? "";
                    if (release.Contains("Update", StringComparison.OrdinalIgnoreCase) || release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) continue;

                    var (iconPath, iconIndex) = ParseIcon(k.GetValue("DisplayIcon") as string);
                    long size = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;

                    list.Add(new InstalledProgram
                    {
                        Name = display,
                        Version = (k.GetValue("DisplayVersion") as string)?.Trim() ?? "",
                        Publisher = (k.GetValue("Publisher") as string)?.Trim() ?? "",
                        InstallDate = ParseDate(k.GetValue("InstallDate") as string),
                        InstallLocation = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"') ?? "",
                        UninstallString = uninstall,
                        QuietUninstallString = (k.GetValue("QuietUninstallString") as string)?.Trim() ?? "",
                        IconPath = iconPath,
                        IconIndex = iconIndex,
                        Hive = hive,
                        View = view,
                        KeyName = name,
                        IsMsi = k.GetValue("WindowsInstaller") is int wi && wi == 1 || uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase),
                        Size = size,
                    });
                }
                catch { /* einzelner kaputter Eintrag */ }
            }
        }
        catch { /* Hive nicht lesbar */ }
    }

    private static DateTime? ParseDate(string? s) =>
        s != null && DateTime.TryParseExact(s.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static (string, int) ParseIcon(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ("", 0);
        raw = Environment.ExpandEnvironmentVariables(raw.Trim());
        int index = 0;
        var comma = raw.LastIndexOf(',');
        if (comma > 0 && int.TryParse(raw[(comma + 1)..].Trim(), out var i))
        {
            index = i;
            raw = raw[..comma];
        }
        return (raw.Trim().Trim('"'), index);
    }

    /// <summary>Lädt das Programm-Icon (aus DisplayIcon oder der Uninstall-EXE).</summary>
    public static ImageSource? LoadIcon(InstalledProgram p)
    {
        foreach (var candidate in new[] { p.IconPath, SplitCommand(p.UninstallString).Exe })
        {
            if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate)) continue;
            if (candidate.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var img = Native.ExtractIcon(candidate, candidate == p.IconPath ? p.IconIndex : 0);
            if (img != null) return img;
        }
        return null;
    }

    /// <summary>Berechnet die Größe für Programme ohne Größenangabe in der Registry.</summary>
    public static long MeasureInstallFolder(InstalledProgram p)
    {
        if (!IsSafeProgramFolder(p.InstallLocation)) return 0;
        long total = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(p.InstallLocation).EnumerateFiles("*", opts)) total += f.Length;
        }
        catch { /* egal */ }
        return total;
    }

    // ───────────── Deinstallation ─────────────

    public static (string Exe, string Args) SplitCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.Length == 0) return ("", "");
        if (cmd[0] == '"')
        {
            var end = cmd.IndexOf('"', 1);
            return end > 0 ? (cmd[1..end], cmd[(end + 1)..].Trim()) : (cmd.Trim('"'), "");
        }
        var exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (cmd[..(exe + 4)], cmd[(exe + 4)..].Trim());
        var space = cmd.IndexOf(' ');
        return space > 0 ? (cmd[..space], cmd[(space + 1)..]) : (cmd, "");
    }

    [GeneratedRegex(@"\{[0-9A-Fa-f\-]{36}\}")]
    private static partial Regex GuidRegex();

    /// <summary>Startet das Deinstallationsprogramm und wartet, bis das Programm entfernt wurde.</summary>
    public static async Task<bool> UninstallAsync(InstalledProgram p, bool quiet, CancellationToken skip)
    {
        string exe, args;
        var guid = GuidRegex().Match(p.UninstallString);
        if (p.IsMsi && guid.Success)
        {
            exe = "msiexec.exe";
            args = $"/X{guid.Value}" + (quiet ? " /passive /norestart" : "");
        }
        else if (quiet && !string.IsNullOrEmpty(p.QuietUninstallString))
        {
            (exe, args) = SplitCommand(p.QuietUninstallString);
        }
        else
        {
            (exe, args) = SplitCommand(p.UninstallString);
        }

        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
        if (Path.IsPathRooted(exe) && Path.GetDirectoryName(exe) is { } wd && Directory.Exists(wd)) psi.WorkingDirectory = wd;

        using (var proc = Process.Start(psi))
        {
            if (proc != null)
            {
                try { await proc.WaitForExitAsync(skip); }
                catch (OperationCanceledException) { return !IsStillInstalled(p); }
            }
        }

        // Viele Uninstaller (NSIS, Inno) kopieren sich in den Temp-Ordner und beenden sich sofort.
        // Deshalb warten wir, bis der Registry-Eintrag tatsächlich verschwunden ist.
        var until = DateTime.UtcNow.AddMinutes(30);
        while (IsStillInstalled(p) && DateTime.UtcNow < until && !skip.IsCancellationRequested)
        {
            try { await Task.Delay(1500, skip); }
            catch (OperationCanceledException) { break; }
        }
        return !IsStillInstalled(p);
    }

    public static bool IsStillInstalled(InstalledProgram p)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(p.Hive, p.View);
            using var k = baseKey.OpenSubKey($@"{UninstallKey}\{p.KeyName}");
            return k != null;
        }
        catch { return false; }
    }

    // ───────────── Reste finden ─────────────

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "Microsoft Shared", "WindowsApps", "Packages", "Temp", "Programs",
        "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Google", "Mozilla", "Apple", "Adobe", "Package Cache",
        "Windows Defender", "Windows NT", "Internet Explorer", "dotnet", "Reference Assemblies", "MSBuild", "Steam",
        "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications", "ODBC", "Uninstall",
    };

    [GeneratedRegex(@"[\s\-_]*(v?\d+([\.\-]\d+)*)?\s*(\((x64|x86|64-bit|32-bit|64 bit|32 bit)\))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"[,\s]+(Inc\.?|GmbH|Ltd\.?|LLC|Corporation|Corp\.?|Co\.?|AG|S\.A\.|B\.V\.|Limited|Software)$", RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffix();

    /// <summary>Namensvarianten. "Strong" = exakter Name, "weak" = ohne Versionsnummer (unsicherer).</summary>
    private static Dictionary<string, bool> NameVariants(InstalledProgram p)
    {
        var set = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        void Add(string? s, bool strong)
        {
            s = s?.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(s) || s.Length < 3 || ProtectedNames.Contains(s) || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            set[s] = strong || (set.TryGetValue(s, out var prev) && prev);
        }

        Add(p.Name, true);
        var noParen = Regex.Replace(p.Name, @"\s*\(.*?\)\s*", " ").Trim();
        Add(noParen, true);
        Add(TrailingVersion().Replace(p.Name, ""), false);
        Add(TrailingVersion().Replace(noParen, ""), false);
        if (!string.IsNullOrEmpty(p.InstallLocation)) Add(Path.GetFileName(p.InstallLocation.TrimEnd('\\')), true);
        return set;
    }

    private static IEnumerable<string> PublisherVariants(InstalledProgram p)
    {
        if (string.IsNullOrWhiteSpace(p.Publisher)) yield break;
        var a = p.Publisher.Trim();
        var b = CompanySuffix().Replace(a, "").Trim();
        foreach (var s in new[] { a, b }.Distinct(StringComparer.OrdinalIgnoreCase))
            if (s.Length >= 3 && !ProtectedNames.Contains(s) && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                yield return s;
    }

    private static string[] BaseFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) + @"\Programs",
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) + @"\Programs",
    ];

    /// <summary>Ein Ordner gilt nur dann als löschbar, wenn er tief genug liegt und kein Systemordner ist.</summary>
    public static bool IsSafeProgramFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\');
            if (!Directory.Exists(full)) return false;
            if (full.Count(c => c == '\\') < 2) return false; // z. B. "C:\Ordner" ist zu weit oben

            var forbidden = BaseFolders().Concat(
            [
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            ]).Select(f => f.TrimEnd('\\'));

            foreach (var f in forbidden)
            {
                if (string.IsNullOrEmpty(f)) continue;
                if (full.Equals(f, StringComparison.OrdinalIgnoreCase)) return false;
                if (f.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return false; // wäre Elternordner eines Systemordners
            }
            if (full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) return false;
            return !ProtectedNames.Contains(Path.GetFileName(full));
        }
        catch { return false; }
    }

    /// <param name="p">Das deinstallierte Programm.</param>
    /// <param name="stillInstalled">Alle noch installierten Programme � deren Ordner werden niemals vorgeschlagen.</param>
    public static List<Leftover> FindLeftovers(InstalledProgram p, IReadOnlyCollection<InstalledProgram> stillInstalled)
    {
        var found = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);
        var variants = NameVariants(p);
        var names = variants.Keys.ToList();
        var publishers = PublisherVariants(p).ToList();

        // Pfade, die anderen (noch installierten) Programmen geh�ren
        var owned = stillInstalled
            .Where(o => !ReferenceEquals(o, p) && !(o.Name == p.Name && o.Version == p.Version))
            .SelectMany(o => new[] { o.InstallLocation, Path.GetDirectoryName(o.IconPath) ?? "", Path.GetDirectoryName(SplitCommand(o.UninstallString).Exe) ?? "" })
            .Where(s => !string.IsNullOrWhiteSpace(s) && Path.IsPathRooted(s))
            .Select(s => s.TrimEnd('\\'))
            .ToList();

        bool BelongsToOther(string path) =>
            owned.Any(o => o.Equals(path, StringComparison.OrdinalIgnoreCase) || o.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase));

        void AddFolder(string path, bool strong)
        {
            path = path.TrimEnd('\\');
            if (found.ContainsKey(path) || !IsSafeProgramFolder(path) || BelongsToOther(path)) return;
            found[path] = new Leftover { Path = path, IsRegistry = false, Size = FolderSize(path), IsChecked = strong };
        }

        if (!string.IsNullOrEmpty(p.InstallLocation)) AddFolder(p.InstallLocation, true);

        foreach (var b in BaseFolders())
        {
            if (string.IsNullOrEmpty(b) || !Directory.Exists(b)) continue;
            foreach (var n in names)
            {
                AddFolder(Path.Combine(b, n), variants[n]);
                foreach (var pub in publishers) AddFolder(Path.Combine(b, pub, n), variants[n]);
            }
            // Herstellerordner, der nur noch leer ist
            foreach (var pub in publishers)
            {
                var dir = Path.Combine(b, pub);
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) AddFolder(dir, true);
                }
                catch { /* egal */ }
            }
        }

        // Registry: nur exakte Treffer unter "Software"
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var sw = bk.OpenSubKey("SOFTWARE");
                if (sw == null) continue;
                var prefix = (hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM") + (view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine ? @"\SOFTWARE\WOW6432Node" : @"\SOFTWARE");
                // Registry nur mit exakten Namen; standardm��ig NICHT angehakt (Nutzer entscheidet).
                foreach (var n in names.Where(x => variants[x]))
                {
                    if (KeyExists(sw, n)) found.TryAdd(prefix + "\\" + n, new Leftover { Path = prefix + "\\" + n, IsRegistry = true, IsChecked = false });
                    foreach (var pub in publishers)
                        if (KeyExists(sw, pub + "\\" + n)) found.TryAdd(prefix + "\\" + pub + "\\" + n, new Leftover { Path = prefix + "\\" + pub + "\\" + n, IsRegistry = true, IsChecked = false });
                }
            }
            catch { /* egal */ }
        }

        return found.Values.ToList();
    }

    private static bool KeyExists(RegistryKey parent, string sub)
    {
        try
        {
            using var k = parent.OpenSubKey(sub);
            return k != null;
        }
        catch { return false; }
    }

    private static long FolderSize(string path)
    {
        long total = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opts)) total += f.Length;
        }
        catch { /* egal */ }
        return total;
    }

    public static string BackupFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EZTool", "Backups");

    /// <summary>Entfernt Reste: Ordner → Papierkorb, Registry → vorher als .reg gesichert.</summary>
    public static (int Removed, int Failed) RemoveLeftovers(IEnumerable<Leftover> items)
    {
        int ok = 0, fail = 0;
        foreach (var l in items)
        {
            try
            {
                if (!l.IsRegistry)
                {
                    if (IsSafeProgramFolder(l.Path) && Native.MoveToRecycleBin(l.Path)) ok++;
                    else fail++;
                    continue;
                }

                Directory.CreateDirectory(BackupFolder);
                var file = Path.Combine(BackupFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Regex.Replace(l.Path, @"[\\/:*?""<>|]", "_")}.reg");
                var export = Process.Start(new ProcessStartInfo("reg.exe", $"export \"{l.Path}\" \"{file}\" /y")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                export?.WaitForExit(15000);
                if (export?.ExitCode != 0) { fail++; continue; }

                var (hive, view, sub) = ParseRegPath(l.Path);
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                bk.DeleteSubKeyTree(sub, false);
                ok++;
            }
            catch { fail++; }
        }
        return (ok, fail);
    }

    private static (RegistryHive, RegistryView, string) ParseRegPath(string path)
    {
        var hive = path.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        var sub = path[5..];
        var view = RegistryView.Registry64;
        if (sub.StartsWith(@"SOFTWARE\WOW6432Node\", StringComparison.OrdinalIgnoreCase))
        {
            view = RegistryView.Registry32;
            sub = "SOFTWARE\\" + sub[@"SOFTWARE\WOW6432Node\".Length..];
        }
        return (hive, view, sub);
    }
}

