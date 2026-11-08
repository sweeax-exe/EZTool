using System.Text.Json;

namespace EZTool.Services;

/// <summary>Alle Einstellungen – gespeichert als JSON unter %LOCALAPPDATA%\EZTool\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>"auto" = Sprache von Windows, sonst Sprachcode (z. B. "de", "ja").</summary>
    public string Language { get; set; } = "auto";

    // Start & Hintergrund
    public bool CloseToTray { get; set; } = true;

    // Sensoren
    public int UpdateIntervalMs { get; set; } = 1000;
    public bool PawnIoPromptShown { get; set; }

    // Software-Updates
    public bool WingetAgreed { get; set; }

    // Gaming-Modus
    public bool BoostPowerPlan { get; set; } = true;
    public bool BoostPurgeRam { get; set; } = true;
    public bool BoostPauseUpdates { get; set; } = true;
    public bool BoostRestartApps { get; set; } = true;
    public List<string> BoostCloseApps { get; set; } = [];
    public bool OverlayShowFps { get; set; } = true;

    // Programm-Updates (GitHub)
    public bool AutoUpdateCheck { get; set; } = true;
    public bool AutoUpdateInstall { get; set; } = true;

    // Tastenbelegung
    public List<KeyBinding> KeyBindings { get; set; } = [];

    // Alarme
    public bool AlertsEnabled { get; set; } = true;
    public int CpuTempLimit { get; set; } = 90;
    public int GpuTempLimit { get; set; } = 85;
    public int DriveFreeLimitPercent { get; set; } = 10;

    // Overlay
    public bool OverlayEnabled { get; set; }
    public string OverlayCorner { get; set; } = "TopRight";
    public double OverlayOpacity { get; set; } = 0.9;

    // Werbeblocker (DNS)
    public bool AdBlockEnabled { get; set; }
    public List<string> AdBlockLists { get; set; } = ["adguard", "stevenblack"];
    public string AdBlockUpstream { get; set; } = "cloudflare";
    public List<string> AdBlockAllow { get; set; } = [];
    public List<string> AdBlockDeny { get; set; } = [];
}

public static class SettingsService
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EZTool");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>Wird nach jedem Speichern ausgelöst (z. B. damit das Overlay reagiert).</summary>
    public static event Action<AppSettings>? Changed;

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* defekte Datei → Standardwerte */ }
        return new AppSettings();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch { /* Speichern darf die App nie stören */ }
        Changed?.Invoke(Current);
    }
}
