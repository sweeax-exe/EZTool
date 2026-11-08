using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using EZTool.Core;
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
            Id = "ads-start", Category = Loc.T("Tweaks.Category.Privacy"), Icon = "\uE8BB",
            Name = Loc.T("Tweaks.AdsStart.Name"),
            Description = Loc.T("Tweaks.AdsStart.Description"),
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
            Id = "ads-lock", Category = Loc.T("Tweaks.Category.Privacy"), Icon = "\uE72E",
            Name = Loc.T("Tweaks.AdsLock.Name"),
            Description = Loc.T("Tweaks.AdsLock.Description"),
            Values =
            [
                new(CU, Cdm, "RotatingLockScreenOverlayEnabled", 0, 1),
                new(CU, Cdm, "SubscribedContent-338387Enabled", 0, 1),
            ]
        },
        new()
        {
            Id = "ad-id", Category = Loc.T("Tweaks.Category.Privacy"), Icon = "\uE77B",
            Name = Loc.T("Tweaks.AdId.Name"),
            Description = Loc.T("Tweaks.AdId.Description"),
            Values = [new(CU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)]
        },
        new()
        {
            Id = "telemetry", Category = Loc.T("Tweaks.Category.Privacy"), Icon = "\uE9D9",
            Name = Loc.T("Tweaks.Telemetry.Name"),
            Description = Loc.T("Tweaks.Telemetry.Description"),
            Values = [new(LM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null)]
        },
        new()
        {
            Id = "bing", Category = Loc.T("Tweaks.Category.Privacy"), Icon = "\uE721",
            Name = Loc.T("Tweaks.Bing.Name"),
            Description = Loc.T("Tweaks.Bing.Description"),
            Values = [new(CU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null)],
            Needs = "explorer"
        },
        new()
        {
            Id = "copilot", Category = Loc.T("Tweaks.Category.Privacy"), Icon = "\uE99A",
            Name = Loc.T("Tweaks.Copilot.Name"),
            Description = Loc.T("Tweaks.Copilot.Description"),
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
            Id = "classic-menu", Category = Loc.T("Tweaks.Category.Explorer"), Icon = "\uE700",
            Name = Loc.T("Tweaks.ClassicMenu.Name"),
            Description = Loc.T("Tweaks.ClassicMenu.Description"),
            Values = [new(CU, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", null, RegistryValueKind.String)],
            Needs = "explorer"
        },
        new()
        {
            Id = "file-ext", Category = Loc.T("Tweaks.Category.Explorer"), Icon = "\uE8A5",
            Name = Loc.T("Tweaks.FileExt.Name"),
            Description = Loc.T("Tweaks.FileExt.Description"),
            Values = [new(CU, Adv, "HideFileExt", 0, 1)],
            Needs = "explorer"
        },
        new()
        {
            Id = "hidden-files", Category = Loc.T("Tweaks.Category.Explorer"), Icon = "\uE7B3",
            Name = Loc.T("Tweaks.HiddenFiles.Name"),
            Description = Loc.T("Tweaks.HiddenFiles.Description"),
            Values = [new(CU, Adv, "Hidden", 1, 2)],
            Needs = "explorer"
        },
        new()
        {
            Id = "this-pc", Category = Loc.T("Tweaks.Category.Explorer"), Icon = "\uE7F8",
            Name = Loc.T("Tweaks.ThisPc.Name"),
            Description = Loc.T("Tweaks.ThisPc.Description"),
            Values = [new(CU, Adv, "LaunchTo", 1, null)],
        },

        // ── Gaming & Leistung ──
        new()
        {
            Id = "mouse-accel", Category = Loc.T("Tweaks.Category.Gaming"), Icon = "\uE962",
            Name = Loc.T("Tweaks.MouseAccel.Name"),
            Description = Loc.T("Tweaks.MouseAccel.Description"),
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
            Id = "game-dvr", Category = Loc.T("Tweaks.Category.Gaming"), Icon = "\uE714",
            Name = Loc.T("Tweaks.GameDvr.Name"),
            Description = Loc.T("Tweaks.GameDvr.Description"),
            Values =
            [
                new(CU, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                new(CU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1),
            ]
        },
        new()
        {
            Id = "game-mode", Category = Loc.T("Tweaks.Category.Gaming"), Icon = "\uE7FC",
            Name = Loc.T("Tweaks.GameMode.Name"),
            Description = Loc.T("Tweaks.GameMode.Description"),
            Values =
            [
                new(CU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 0),
                new(CU, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1, 0),
            ]
        },
        new()
        {
            Id = "hags", Category = Loc.T("Tweaks.Category.Gaming"), Icon = "\uE7F4",
            Name = Loc.T("Tweaks.Hags.Name"),
            Description = Loc.T("Tweaks.Hags.Description"),
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
            inParams["Description"] = Loc.T("Tweaks.RestorePointDescription");
            inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
            inParams["EventType"] = 100;       // BEGIN_SYSTEM_CHANGE
            using var result = cls.InvokeMethod("CreateRestorePoint", inParams, null);
            var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1u);
            return code == 0
                ? (true, Loc.T("Tweaks.RestorePointCreated"))
                : (false, code == 1058
                    ? Loc.T("Tweaks.RestorePointProtectionOff")
                    : Loc.T("Tweaks.RestorePointRejected", code));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
