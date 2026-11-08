using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EZTool.Core;
using Microsoft.Win32;

namespace EZTool.Services;

public enum AutostartSource
{
    RegistryUser,
    RegistryMachine,
    RegistryMachine32,
    FolderUser,
    FolderCommon,
    Task,
}

public sealed partial class AutostartEntry : ObservableObject
{
    public required string Name { get; init; }
    public required AutostartSource Source { get; init; }
    public string Command { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string Publisher { get; init; } = "";

    /// <summary>Registry-Wertname, Dateipfad (.lnk) oder vollständiger Aufgabenpfad.</summary>
    public required string Location { get; init; }

    public bool FileMissing { get; init; }
    public bool IsWindows => Publisher.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    public string SourceLabel => Source switch
    {
        AutostartSource.RegistryUser => Loc.T("Autostart.SourceRegistryUser"),
        AutostartSource.RegistryMachine => Loc.T("Autostart.SourceRegistryMachine"),
        AutostartSource.RegistryMachine32 => Loc.T("Autostart.SourceRegistry32"),
        AutostartSource.FolderUser => Loc.T("Autostart.SourceFolderUser"),
        AutostartSource.FolderCommon => Loc.T("Autostart.SourceFolderCommon"),
        _ => Loc.T("Autostart.SourceTask"),
    };

    public string SourceIcon => Source switch
    {
        AutostartSource.Task => "\uE823",
        AutostartSource.FolderUser or AutostartSource.FolderCommon => "\uE8B7",
        _ => "\uE9E9",
    };

    public string Category => Source switch
    {
        AutostartSource.Task => "task",
        AutostartSource.FolderUser or AutostartSource.FolderCommon => "folder",
        _ => "registry",
    };

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private ImageSource? _icon;
}

/// <summary>
/// Liest und schaltet Autostart-Einträge. Ein-/Ausschalten nutzt denselben Mechanismus wie der
/// Task-Manager ("StartupApproved") – der Eintrag selbst bleibt erhalten und ist jederzeit reaktivierbar.
/// </summary>
public static class AutostartService
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private static string UserStartup => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    private static string CommonStartup => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

    public static List<AutostartEntry> LoadAll()
    {
        var list = new List<AutostartEntry>();
        ReadRun(list, RegistryHive.CurrentUser, RegistryView.Registry64, AutostartSource.RegistryUser);
        ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry64, AutostartSource.RegistryMachine);
        ReadRun(list, RegistryHive.LocalMachine, RegistryView.Registry32, AutostartSource.RegistryMachine32);
        ReadFolder(list, UserStartup, AutostartSource.FolderUser);
        ReadFolder(list, CommonStartup, AutostartSource.FolderCommon);
        try { ReadTasks(list); } catch { /* Aufgabenplanung nicht verfügbar */ }
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ───────────── Registry ─────────────

    private static void ReadRun(List<AutostartEntry> list, RegistryHive hive, RegistryView view, AutostartSource source)
    {
        try
        {
            using var bk = RegistryKey.OpenBaseKey(hive, view);
            using var run = bk.OpenSubKey(RunKey);
            if (run == null) return;
            foreach (var name in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(name) || run.GetValue(name) is not string cmd || string.IsNullOrWhiteSpace(cmd)) continue;
                var exe = ResolveExe(cmd);
                list.Add(new AutostartEntry
                {
                    Name = name,
                    Source = source,
                    Command = cmd,
                    ExePath = exe,
                    Publisher = Publisher(exe),
                    Location = name,
                    FileMissing = exe.Length > 0 && Path.IsPathRooted(exe) && !File.Exists(exe),
                    IsEnabled = ReadApproved(source, name),
                });
            }
        }
        catch { /* nicht lesbar */ }
    }

    /// <summary>Ermittelt die ausführbare Datei aus einer Befehlszeile (auch rundll32/cmd-Wrapper).</summary>
    private static string ResolveExe(string cmd)
    {
        var (exe, args) = ProgramService.SplitCommand(cmd);
        if (exe.Length > 0 && !Path.IsPathRooted(exe))
        {
            var sys = Path.Combine(Environment.SystemDirectory, exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe");
            if (File.Exists(sys)) exe = sys;
        }
        // rundll32 "C:\...\foo.dll",Entry → die DLL ist das eigentliche Ziel
        if (exe.EndsWith("rundll32.exe", StringComparison.OrdinalIgnoreCase) && args.Length > 0)
        {
            var target = ProgramService.SplitCommand(args).Exe.Split(',')[0].Trim().Trim('"');
            if (Path.IsPathRooted(target)) return target;
        }
        return exe;
    }

    private static string Publisher(string exe)
    {
        try
        {
            return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim() ?? "" : "";
        }
        catch { return ""; }
    }

    private static (RegistryHive Hive, string Sub) ApprovedLocation(AutostartSource source) => source switch
    {
        AutostartSource.RegistryUser => (RegistryHive.CurrentUser, ApprovedKey + @"\Run"),
        AutostartSource.RegistryMachine => (RegistryHive.LocalMachine, ApprovedKey + @"\Run"),
        AutostartSource.RegistryMachine32 => (RegistryHive.LocalMachine, ApprovedKey + @"\Run32"),
        AutostartSource.FolderUser => (RegistryHive.CurrentUser, ApprovedKey + @"\StartupFolder"),
        AutostartSource.FolderCommon => (RegistryHive.LocalMachine, ApprovedKey + @"\StartupFolder"),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static bool ReadApproved(AutostartSource source, string valueName)
    {
        try
        {
            var (hive, sub) = ApprovedLocation(source);
            using var bk = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = bk.OpenSubKey(sub);
            // Ungerades erstes Byte (0x03, 0x07 …) = vom Nutzer deaktiviert
            return k?.GetValue(valueName) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
        }
        catch { return true; }
    }

    private static void WriteApproved(AutostartSource source, string valueName, bool enabled)
    {
        var (hive, sub) = ApprovedLocation(source);
        using var bk = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var k = bk.CreateSubKey(sub, true);
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        k.SetValue(valueName, data, RegistryValueKind.Binary);
    }

    // ───────────── Autostart-Ordner ─────────────

    private static void ReadFolder(List<AutostartEntry> list, string folder, AutostartSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            string exe = file, cmd = file;
            if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var (target, args) = ResolveShortcut(file);
                if (target.Length > 0)
                {
                    exe = target;
                    cmd = string.IsNullOrEmpty(args) ? $"\"{target}\"" : $"\"{target}\" {args}";
                }
            }

            list.Add(new AutostartEntry
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Source = source,
                Command = cmd,
                ExePath = exe,
                Publisher = Publisher(exe),
                Location = file,
                FileMissing = Path.IsPathRooted(exe) && !File.Exists(exe),
                IsEnabled = ReadApproved(source, fileName),
            });
        }
    }

    private static (string Target, string Args) ResolveShortcut(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return ("", "");
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(lnk);
            return ((string)sc.TargetPath ?? "", (string)sc.Arguments ?? "");
        }
        catch { return ("", ""); }
    }

    // ───────────── Geplante Aufgaben (bei Anmeldung / Systemstart) ─────────────

    private const int TriggerBoot = 8, TriggerLogon = 9, ActionExec = 0;

    private static dynamic ConnectScheduler()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException(Loc.T("Autostart.TaskSchedulerUnavailable"));
        dynamic svc = Activator.CreateInstance(type)!;
        svc.Connect();
        return svc;
    }

    private static void ReadTasks(List<AutostartEntry> list)
    {
        dynamic svc = ConnectScheduler();
        Walk(svc.GetFolder("\\"));

        void Walk(dynamic folder)
        {
            string path = folder.Path;
            // Windows-eigene Aufgaben werden bewusst ausgelassen
            if (path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;

            foreach (dynamic task in folder.GetTasks(1 /* TASK_ENUM_HIDDEN */))
            {
                try
                {
                    dynamic def = task.Definition;
                    bool atStart = false;
                    foreach (dynamic trig in def.Triggers)
                    {
                        int t = trig.Type;
                        if (t is TriggerBoot or TriggerLogon) atStart = true;
                    }
                    if (!atStart) continue;

                    string exe = "", args = "";
                    foreach (dynamic action in def.Actions)
                    {
                        if ((int)action.Type != ActionExec) continue;
                        exe = Environment.ExpandEnvironmentVariables(((string)action.Path ?? "").Trim('"'));
                        args = (string)action.Arguments ?? "";
                        break;
                    }

                    var resolved = exe.Length > 0 && !Path.IsPathRooted(exe) ? ResolveExe(exe) : exe;
                    list.Add(new AutostartEntry
                    {
                        Name = (string)task.Name,
                        Source = AutostartSource.Task,
                        Command = string.IsNullOrEmpty(args) ? exe : $"{exe} {args}",
                        ExePath = resolved,
                        Publisher = Publisher(resolved),
                        Location = (string)task.Path,
                        FileMissing = resolved.Length > 0 && Path.IsPathRooted(resolved) && !File.Exists(resolved),
                        IsEnabled = (bool)task.Enabled,
                    });
                }
                catch { /* einzelne Aufgabe nicht lesbar */ }
            }

            foreach (dynamic sub in folder.GetFolders(0)) Walk(sub);
        }
    }

    private static dynamic OpenTask(dynamic svc, string fullPath)
    {
        var idx = fullPath.LastIndexOf('\\');
        var folder = idx <= 0 ? "\\" : fullPath[..idx];
        return svc.GetFolder(folder).GetTask(fullPath[(idx + 1)..]);
    }

    // ───────────── Aktionen ─────────────

    public static void SetEnabled(AutostartEntry e, bool enabled)
    {
        switch (e.Source)
        {
            case AutostartSource.Task:
                dynamic task = OpenTask(ConnectScheduler(), e.Location);
                task.Enabled = enabled;
                break;
            case AutostartSource.FolderUser or AutostartSource.FolderCommon:
                WriteApproved(e.Source, Path.GetFileName(e.Location), enabled);
                break;
            default:
                WriteApproved(e.Source, e.Location, enabled);
                break;
        }
    }

    private sealed record BackupEntry(string Name, string Source, string Location, string Command, DateTime Removed, string? TaskXml);

    /// <summary>Entfernt einen Eintrag dauerhaft. Vorher wird alles Nötige gesichert.</summary>
    public static void Delete(AutostartEntry e)
    {
        string? xml = null;
        dynamic? svc = null;
        if (e.Source == AutostartSource.Task)
        {
            svc = ConnectScheduler();
            xml = (string)OpenTask(svc, e.Location).Xml;
        }

        // Sicherung: JSON-Protokoll (+ Aufgaben-XML zum Wiederimport)
        Directory.CreateDirectory(ProgramService.BackupFolder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var safe = string.Concat(e.Name.Split(Path.GetInvalidFileNameChars()));
        File.WriteAllText(Path.Combine(ProgramService.BackupFolder, $"{stamp}-autostart-{safe}.json"),
            JsonSerializer.Serialize(new BackupEntry(e.Name, e.Source.ToString(), e.Location, e.Command, DateTime.Now, xml),
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        if (xml != null) File.WriteAllText(Path.Combine(ProgramService.BackupFolder, $"{stamp}-task-{safe}.xml"), xml);

        switch (e.Source)
        {
            case AutostartSource.Task:
                var idx = e.Location.LastIndexOf('\\');
                svc!.GetFolder(idx <= 0 ? "\\" : e.Location[..idx]).DeleteTask(e.Location[(idx + 1)..], 0);
                break;

            case AutostartSource.FolderUser or AutostartSource.FolderCommon:
                if (!Native.MoveToRecycleBin(e.Location)) throw new IOException(Loc.T("Autostart.RecycleFailed"));
                RemoveApproved(e.Source, Path.GetFileName(e.Location));
                break;

            default:
                var hive = e.Source == AutostartSource.RegistryUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
                var view = e.Source == AutostartSource.RegistryMachine32 ? RegistryView.Registry32 : RegistryView.Registry64;
                using (var bk = RegistryKey.OpenBaseKey(hive, view))
                using (var run = bk.OpenSubKey(RunKey, true) ?? throw new IOException(Loc.T("Autostart.RunKeyMissing")))
                {
                    run.DeleteValue(e.Location, false);
                }
                RemoveApproved(e.Source, e.Location);
                break;
        }
    }

    private static void RemoveApproved(AutostartSource source, string valueName)
    {
        try
        {
            var (hive, sub) = ApprovedLocation(source);
            using var bk = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = bk.OpenSubKey(sub, true);
            k?.DeleteValue(valueName, false);
        }
        catch { /* egal */ }
    }

    public static ImageSource? LoadIcon(AutostartEntry e) =>
        e.ExePath.Length > 0 && File.Exists(e.ExePath) ? Native.ExtractIcon(e.ExePath, 0) : null;

    /// <summary>Öffnet den Speicherort: Datei im Explorer, Registry-Schlüssel im Registrierungs-Editor oder die Aufgabenplanung.</summary>
    public static void Reveal(AutostartEntry e)
    {
        switch (e.Source)
        {
            case AutostartSource.Task:
                Process.Start(new ProcessStartInfo("taskschd.msc") { UseShellExecute = true });
                break;
            default:
                var target = e.Source is AutostartSource.FolderUser or AutostartSource.FolderCommon ? e.Location : e.ExePath;
                if (File.Exists(target)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
                else if (Path.GetDirectoryName(target) is { } dir && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                break;
        }
    }
}
