using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EZTool.Services;

/// <summary>Ein einzelner Registry-Wert, den ein Tweak setzt. Default = Windows-Standard (null = Wert löschen).</summary>
public sealed record RegValue(RegistryHive Hive, string Key, string Name, object On, object? Default, RegistryValueKind Kind = RegistryValueKind.DWord);

public sealed class Tweak
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string Icon { get; init; } = "\uE713";
    public required RegValue[] Values { get; init; }

    /// <summary>"explorer" = Explorer-Neustart nötig, "reboot" = Windows-Neustart nötig.</summary>
    public string? Needs { get; init; }

    public bool RequiresAdmin => Values.Any(v => v.Hive == RegistryHive.LocalMachine);

    /// <summary>Wird nach dem Setzen aufgerufen (z. B. um Mauseinstellungen sofort zu übernehmen).</summary>
    public Action<bool>? AfterChange { get; init; }
}

public static class TweakService
{
    private const RegistryHive CU = RegistryHive.CurrentUser, LM = RegistryHive.LocalMachine;
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    public static IReadOnlyList<Tweak> All { get; } =
    [
        // ── Datenschutz & Werbung ──
        new()
        {
            Id = "ads-start", Category = "Werbung & Datenschutz", Icon = "\uE8BB",
            Name = "Werbung und App-Vorschläge ausschalten",
            Description = "Keine „empfohlenen“ Apps im Startmenü, keine automatisch installierten Werbe-Apps, keine Tipps im Explorer.",
            Values =
            [
                new(CU, Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
                new(CU, Cdm, "SilentInstalledAppsEnabled", 0, 1),
                new(CU, Cdm, "SubscribedContent-338388Enabled", 0, 1),
                new(CU, Cdm, "SubscribedContent-338389Enabled", 0, 1),
                new(CU, Cdm, "SubscribedContent-310093Enabled", 0, 1),
                new(CU, Adv, "ShowSyncProviderNotifications", 0, 1),
            ]
        },
        new()
        {
            Id = "ads-lock", Category = "Werbung & Datenschutz", Icon = "\uE72E",
            Name = "Tipps und Werbung auf dem Sperrbildschirm aus",
            Description = "Entfernt „Wissenswertes“ und Werbeeinblendungen vom Sperrbildschirm.",
            Values =
            [
                new(CU, Cdm, "RotatingLockScreenOverlayEnabled", 0, 1),
                new(CU, Cdm, "SubscribedContent-338387Enabled", 0, 1),
            ]
        },
        new()
        {
            Id = "ad-id", Category = "Werbung & Datenschutz", Icon = "\uE77B",
            Name = "Werbe-ID deaktivieren",
            Description = "Apps können dich nicht mehr über eine geräteweite Werbe-ID wiedererkennen.",
            Values = [new(CU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)]
        },
        new()
        {
            Id = "telemetry", Category = "Werbung & Datenschutz", Icon = "\uE9D9",
            Name = "Telemetrie auf das Minimum",
            Description = "Windows sendet nur noch die minimal erforderlichen Diagnosedaten.",
            Values = [new(LM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null)]
        },
        new()
        {
            Id = "bing", Category = "Werbung & Datenschutz", Icon = "\uE721",
            Name = "Bing-Websuche im Startmenü aus",
            Description = "Die Startmenü-Suche findet nur noch Apps, Dateien und Einstellungen – keine Bing-Ergebnisse mehr.",
            Values = [new(CU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null)],
            Needs = "explorer"
        },
        new()
        {
            Id = "copilot", Category = "Werbung & Datenschutz", Icon = "\uE99A",
            Name = "Copilot ausblenden",
            Description = "Entfernt die Copilot-Schaltfläche aus der Taskleiste.",
            Values =
            [
                new(CU, Adv, "ShowCopilotButton", 0, 1),
                new(CU, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
            ],
            Needs = "explorer"
        },

        // ── Explorer & Bedienung ──
        new()
        {
            Id = "classic-menu", Category = "Explorer & Bedienung", Icon = "\uE700",
            Name = "Klassisches Rechtsklick-Menü (Windows 11)",
            Description = "Zeigt sofort das vollständige Kontextmenü – ohne Umweg über „Weitere Optionen anzeigen“.",
            Values = [new(CU, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", null, RegistryValueKind.String)],
            Needs = "explorer"
        },
        new()
        {
            Id = "file-ext", Category = "Explorer & Bedienung", Icon = "\uE8A5",
            Name = "Dateiendungen anzeigen",
            Description = "Zeigt z. B. „.exe“ oder „.pdf“ – hilft, getarnte Schadsoftware zu erkennen.",
            Values = [new(CU, Adv, "HideFileExt", 0, 1)],
            Needs = "explorer"
        },
        new()
        {
            Id = "hidden-files", Category = "Explorer & Bedienung", Icon = "\uE7B3",
            Name = "Versteckte Dateien anzeigen",
            Description = "Zeigt versteckte Ordner wie AppData im Explorer.",
            Values = [new(CU, Adv, "Hidden", 1, 2)],
            Needs = "explorer"
        },
        new()
        {
            Id = "this-pc", Category = "Explorer & Bedienung", Icon = "\uE7F8",
            Name = "Explorer öffnet „Dieser PC“",
            Description = "Der Explorer startet mit deinen Laufwerken statt mit der Startseite.",
            Values = [new(CU, Adv, "LaunchTo", 1, null)],
        },

        // ── Gaming & Leistung ──
        new()
        {
            Id = "mouse-accel", Category = "Gaming & Leistung", Icon = "\uE962",
            Name = "Mausbeschleunigung aus",
            Description = "„Zeigerbeschleunigung verbessern“ deaktivieren – Mausbewegungen werden 1:1 umgesetzt. Wichtig für präzises Zielen.",
            Values =
            [
                new(CU, @"Control Panel\Mouse", "MouseSpeed", "0", "1", RegistryValueKind.String),
                new(CU, @"Control Panel\Mouse", "MouseThreshold1", "0", "6", RegistryValueKind.String),
                new(CU, @"Control Panel\Mouse", "MouseThreshold2", "0", "10", RegistryValueKind.String),
            ],
            AfterChange = ApplyMouseNow
        },
        new()
        {
            Id = "game-dvr", Category = "Gaming & Leistung", Icon = "\uE714",
            Name = "Hintergrundaufnahme (Game DVR) aus",
            Description = "Die Xbox Game Bar zeichnet nicht mehr ständig im Hintergrund auf – spart Leistung in Spielen.",
            Values =
            [
                new(CU, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                new(CU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1),
            ]
        },
        new()
        {
            Id = "game-mode", Category = "Gaming & Leistung", Icon = "\uE7FC",
            Name = "Windows-Spielmodus an",
            Description = "Windows priorisiert Spiele und verschiebt Updates und Benachrichtigungen während des Spielens.",
            Values =
            [
                new(CU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 0),
                new(CU, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, 0),
            ]
        },
        new()
        {
            Id = "hags", Category = "Gaming & Leistung", Icon = "\uE7F4",
            Name = "Hardwarebeschleunigte GPU-Planung",
            Description = "Die Grafikkarte verwaltet ihren Speicher selbst – kann Latenz senken. Wirkt nach einem Neustart.",
            Values = [new(LM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1)],
            Needs = "reboot"
        },
    ];

    public static bool IsApplied(Tweak t) => t.Values.All(v =>
    {
        var current = Read(v);
        return current != null && string.Equals(current.ToString(), v.On.ToString(), StringComparison.OrdinalIgnoreCase);
    });

    private static object? Read(RegValue v)
    {
        try
        {
            using var bk = RegistryKey.OpenBaseKey(v.Hive, RegistryView.Registry64);
            using var k = bk.OpenSubKey(v.Key);
            return k?.GetValue(v.Name);
        }
        catch { return null; }
    }

    public static void Set(Tweak t, bool on)
    {
        foreach (var v in t.Values)
        {
            using var bk = RegistryKey.OpenBaseKey(v.Hive, RegistryView.Registry64);
            if (on)
            {
                using var k = bk.CreateSubKey(v.Key, true);
                k.SetValue(v.Name, v.On, v.Kind);
            }
            else if (v.Default == null)
            {
                using var k = bk.OpenSubKey(v.Key, true);
                k?.DeleteValue(v.Name, false);
                // Der Schlüssel für das klassische Kontextmenü muss komplett weg
                if (t.Id == "classic-menu") bk.DeleteSubKeyTree(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}", false);
            }
            else
            {
                using var k = bk.CreateSubKey(v.Key, true);
                k.SetValue(v.Name, v.Default, v.Kind);
            }
        }
        t.AfterChange?.Invoke(on);
    }

    // ── Mauseinstellungen sofort übernehmen (ohne Abmelden) ──
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, int[] values, uint winIni);

    private static void ApplyMouseNow(bool on)
    {
        const uint SPI_SETMOUSE = 0x0004, SPIF_UPDATEINIFILE = 0x01, SPIF_SENDCHANGE = 0x02;
        int[] values = on ? [0, 0, 0] : [6, 10, 1];
        SystemParametersInfo(SPI_SETMOUSE, 0, values, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    /// <summary>Explorer neu starten, damit Änderungen sofort sichtbar werden.</summary>
    public static void RestartExplorer()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); } catch { /* egal */ }
            p.Dispose();
        }
        Thread.Sleep(800);
        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true });
    }

    /// <summary>Legt einen Windows-Wiederherstellungspunkt an (Systemschutz muss aktiv sein).</summary>
    public static (bool Ok, string Message) CreateRestorePoint()
    {
        try
        {
            using var cls = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var inParams = cls.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = "EZTool – vor Windows-Tweaks";
            inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
            inParams["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
            using var result = cls.InvokeMethod("CreateRestorePoint", inParams, null);
            var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1u);
            return code == 0
                ? (true, "Wiederherstellungspunkt wurde erstellt.")
                : (false, code == 1058
                    ? "Der Systemschutz ist für Laufwerk C: deaktiviert. Aktiviere ihn unter „Wiederherstellungspunkt erstellen“ in Windows."
                    : $"Windows hat den Wiederherstellungspunkt abgelehnt (Code {code}). Windows erlaubt höchstens einen pro 24 Stunden.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
