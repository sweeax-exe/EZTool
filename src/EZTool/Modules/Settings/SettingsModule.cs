using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Services;

namespace EZTool.Modules.Settings;

public sealed class SettingsModule : IModule
{
    public string Id => "settings";
    public string Title => "Einstellungen";
    public string Subtitle => "Autostart, Hintergrundbetrieb, Sensor-Treiber, Alarme und Overlay.";
    public string Icon => "";
    public int Order => 1000;

    public FrameworkElement CreateView() => new SettingsView { DataContext = new SettingsViewModel() };
}

public sealed partial class SettingsViewModel : ObservableObject, IActivatable
{
    private readonly AppSettings _cfg = SettingsService.Current;
    private bool _loading;

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private int _intervalIndex;

    [ObservableProperty] private string _driverStatus = "";
    [ObservableProperty] private bool _driverOk;
    [ObservableProperty] private string _driverButtonText = "PawnIO installieren";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(InstallDriverCommand))]
    private bool _isInstalling;

    [ObservableProperty] private bool _alertsEnabled;
    [ObservableProperty] private int _cpuTempLimit;
    [ObservableProperty] private int _gpuTempLimit;
    [ObservableProperty] private int _driveFreeLimit;

    [ObservableProperty] private bool _overlayEnabled;
    [ObservableProperty] private string _overlayCorner = "TopRight";
    [ObservableProperty] private double _overlayOpacity;

    // ── Programm-Updates ──
    [ObservableProperty] private bool _autoUpdateCheck;
    [ObservableProperty] private bool _autoUpdateInstall;
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateNotes = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CheckUpdatesCommand), nameof(InstallUpdateCommand))]
    private bool _isUpdating;

    public string RepoUrl => UpdateService.RepoUrl;

    partial void OnAutoUpdateCheckChanged(bool value) => Save(c => c.AutoUpdateCheck = value);
    partial void OnAutoUpdateInstallChanged(bool value) => Save(c => c.AutoUpdateInstall = value);

    private void ShowUpdate(UpdateInfo? info)
    {
        UpdateAvailable = info != null;
        UpdateNotes = info?.Notes.Trim() ?? "";
        UpdateStatus = info != null
            ? $"Version {info.Version} ist verfügbar (installiert: {UpdateService.Current})."
            : UpdateService.Instance.LastCheck is { } t
                ? $"EZTool {UpdateService.Current} ist aktuell · geprüft um {t:HH:mm} Uhr"
                : $"Installiert: EZTool {UpdateService.Current}";
    }

    private bool CanUpdate() => !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task CheckUpdatesAsync()
    {
        IsUpdating = true;
        UpdateStatus = "Frage GitHub nach neuen Versionen…";
        try { ShowUpdate(await UpdateService.Instance.CheckAsync()); }
        finally { IsUpdating = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (UpdateService.Instance.Available is not { } info) return;
        IsUpdating = true;
        var progress = new Progress<string>(s => UpdateStatus = s);
        var (ok, msg) = await UpdateService.Instance.InstallAsync(info, progress);
        UpdateStatus = msg;
        if (!ok)
        {
            IsUpdating = false;
            NeonDialog.Show("Update nicht installiert", msg, cancelText: null, danger: true);
        }
    }

    public bool IsAdmin { get; } = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public string Version => "v" + typeof(App).Assembly.GetName().Version?.ToString(3);
    public string SettingsPath => App.ScreenshotMode ? @"%LOCALAPPDATA%\EZTool" : SettingsService.Folder;

    private static readonly int[] Intervals = [500, 1000, 2000];

    public SettingsViewModel()
    {
        _loading = true;
        CloseToTray = _cfg.CloseToTray;
        IntervalIndex = Math.Max(0, Array.IndexOf(Intervals, _cfg.UpdateIntervalMs));
        AlertsEnabled = _cfg.AlertsEnabled;
        CpuTempLimit = _cfg.CpuTempLimit;
        GpuTempLimit = _cfg.GpuTempLimit;
        DriveFreeLimit = _cfg.DriveFreeLimitPercent;
        OverlayEnabled = _cfg.OverlayEnabled;
        OverlayCorner = _cfg.OverlayCorner;
        OverlayOpacity = _cfg.OverlayOpacity;
        AutoUpdateCheck = _cfg.AutoUpdateCheck;
        AutoUpdateInstall = _cfg.AutoUpdateInstall;
        _loading = false;
        ShowUpdate(UpdateService.Instance.Available);
        UpdateService.Instance.Changed += ShowUpdate;
        SettingsService.Changed += c =>
        {
            // z. B. Overlay über das Tray-Menü umgeschaltet
            if (OverlayEnabled != c.OverlayEnabled) { _loading = true; OverlayEnabled = c.OverlayEnabled; _loading = false; }
        };
    }

    public void OnActivated()
    {
        _loading = true;
        StartWithWindows = StartupTaskService.IsEnabled();
        _loading = false;
        RefreshDriver();
    }

    private void RefreshDriver()
    {
        var v = PawnIoService.InstalledVersion;
        DriverOk = v != null && !PawnIoService.NeedsUpdate;
        DriverStatus = v == null ? "Nicht installiert – CPU-Temperatur, -Takt und -Leistung sind nicht verfügbar."
            : PawnIoService.NeedsUpdate ? $"Version {v} ist veraltet – bitte aktualisieren."
            : $"Version {v} installiert – alle Sensoren aktiv.";
        DriverButtonText = v == null ? "PawnIO installieren" : "Neu installieren / aktualisieren";
    }

    // ── Speichern bei jeder Änderung ──
    partial void OnCloseToTrayChanged(bool value) => Save(c => c.CloseToTray = value);
    partial void OnIntervalIndexChanged(int value) => Save(c => c.UpdateIntervalMs = Intervals[Math.Clamp(value, 0, 2)]);
    partial void OnAlertsEnabledChanged(bool value) => Save(c => c.AlertsEnabled = value);
    partial void OnCpuTempLimitChanged(int value) => Save(c => c.CpuTempLimit = value);
    partial void OnGpuTempLimitChanged(int value) => Save(c => c.GpuTempLimit = value);
    partial void OnDriveFreeLimitChanged(int value) => Save(c => c.DriveFreeLimitPercent = value);
    partial void OnOverlayEnabledChanged(bool value) => Save(c => c.OverlayEnabled = value);
    partial void OnOverlayCornerChanged(string value) => Save(c => c.OverlayCorner = value);
    partial void OnOverlayOpacityChanged(double value) => Save(c => c.OverlayOpacity = Math.Round(value, 2));

    private void Save(Action<AppSettings> apply)
    {
        if (_loading) return;
        apply(_cfg);
        SettingsService.Save();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        try
        {
            if (value) StartupTaskService.Enable();
            else StartupTaskService.Disable();
        }
        catch (Exception ex)
        {
            _loading = true;
            StartWithWindows = !value;
            _loading = false;
            NeonDialog.Show("Nicht möglich", "Der Windows-Autostart konnte nicht geändert werden" +
                (IsAdmin ? "." : " – dafür sind Administrator-Rechte nötig.") + "\n\n" + ex.Message, cancelText: null, danger: true);
        }
    }

    [RelayCommand]
    private void SetCorner(string corner) => OverlayCorner = corner;

    [RelayCommand]
    private void SetInterval(string index) => IntervalIndex = int.Parse(index);

    private bool CanInstall() => !IsInstalling && IsAdmin;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallDriverAsync()
    {
        IsInstalling = true;
        try { await PawnIoService.InstallWithDialogsAsync(); }
        finally
        {
            IsInstalling = false;
            RefreshDriver();
        }
    }

    [RelayCommand]
    private static void TestAlert() => AlertService.Test();

    [RelayCommand]
    private static void OpenFolder(string which)
    {
        var dir = which == "backups" ? ProgramService.BackupFolder : SettingsService.Folder;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
