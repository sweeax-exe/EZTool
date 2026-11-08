using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Controls;
using EZTool.Core;
using EZTool.Services;

namespace EZTool.Modules.Gaming;

public sealed class GamingModule : IModule
{
    public string Id => "gaming";
    public string Title => "Gaming-Modus";
    public string Subtitle => "Ein Klick: Höchstleistung, freier Arbeitsspeicher, Ruhe im Hintergrund – plus Live-FPS und Bericht nach jeder Session.";
    public string Icon => "";
    public int Order => 110;

    public FrameworkElement CreateView() => new GamingView { DataContext = new GamingViewModel() };
}

public sealed partial class AppChoice : ObservableObject
{
    public required string Name { get; init; }
    public string Display { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ImageSource? _icon;
}

public sealed partial class GamingViewModel : ObservableObject
{
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xB5, 0x47), Cyan = Color.FromRgb(0x00, 0xE5, 0xFF), Magenta = Color.FromRgb(0xFF, 0x3D, 0x9A);
    private readonly AppSettings _cfg = SettingsService.Current;
    private readonly Queue<double> _fpsQueue = new();
    private bool _visible;

    public ObservableCollection<string> Actions { get; } = [];
    public ObservableCollection<AppChoice> Apps { get; } = [];
    public ObservableCollection<GameSession> Sessions { get; } = [];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ToggleCommand))] private bool _isBusy;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _sinceText = "";

    [ObservableProperty] private string _fpsText = "0";
    [ObservableProperty] private string _frameTimeText = "";
    [ObservableProperty] private string _gameName = "Kein Spiel im Vordergrund";
    [ObservableProperty] private double[] _fpsHistory = [];
    [ObservableProperty] private string _cpuTemp = "—";
    [ObservableProperty] private string _gpuTemp = "—";
    [ObservableProperty] private string _measureHint = "";

    [ObservableProperty] private bool _boostPowerPlan;
    [ObservableProperty] private bool _boostPurgeRam;
    [ObservableProperty] private bool _boostPauseUpdates;
    [ObservableProperty] private bool _boostRestartApps;

    [ObservableProperty] private GameSession? _selectedSession;
    [ObservableProperty] private IReadOnlyList<DateTime>? _reportTimes;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _reportFps;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _reportTemps;

    public GamingViewModel()
    {
        BoostPowerPlan = _cfg.BoostPowerPlan;
        BoostPurgeRam = _cfg.BoostPurgeRam;
        BoostPauseUpdates = _cfg.BoostPauseUpdates;
        BoostRestartApps = _cfg.BoostRestartApps;

        var g = GamingService.Instance;
        g.Changed += SyncState;
        g.SessionFinished += s =>
        {
            Sessions.Insert(0, s);
            SelectedSession = s;
        };
        FpsService.Instance.Updated += OnFps;
        HardwareService.Instance.Updated += s =>
        {
            CpuTemp = s.CpuTemp is { } c ? $"{c:0} °C" : "—";
            GpuTemp = s.GpuTemp is { } t ? $"{t:0} °C" : "—";
            if (IsActive) SinceText = $"aktiv seit {Format.Duration(DateTime.Now - g.Since)}";
        };
        foreach (var s in GamingService.LoadSessions()) Sessions.Add(s);
        SelectedSession = Sessions.FirstOrDefault();
        SyncState();
    }

    partial void OnBoostPowerPlanChanged(bool value) { _cfg.BoostPowerPlan = value; SettingsService.Save(); }
    partial void OnBoostPurgeRamChanged(bool value) { _cfg.BoostPurgeRam = value; SettingsService.Save(); }
    partial void OnBoostPauseUpdatesChanged(bool value) { _cfg.BoostPauseUpdates = value; SettingsService.Save(); }
    partial void OnBoostRestartAppsChanged(bool value) { _cfg.BoostRestartApps = value; SettingsService.Save(); }

    /// <summary>FPS nur messen, solange die Seite sichtbar ist (oder der Modus läuft).</summary>
    public void SetActive(bool visible)
    {
        if (visible == _visible) return;
        _visible = visible;
        if (visible)
        {
            FpsService.Instance.Acquire();
            MeasureHint = FpsService.Instance.Error ?? "Wechsle ins Spiel – die FPS des Fensters im Vordergrund werden gemessen.";
            _ = LoadAppsAsync();
        }
        else FpsService.Instance.Release();
    }

    private void OnFps(FpsReading? r)
    {
        if (r != null)
        {
            FpsText = $"{r.Fps:0}";
            FrameTimeText = $"{r.FrameTimeMs:0.0} ms Frametime";
            GameName = r.Process;
        }
        else
        {
            FpsText = "0";
            FrameTimeText = "";
            GameName = "Kein Spiel im Vordergrund";
        }
        _fpsQueue.Enqueue(r?.Fps ?? 0);
        while (_fpsQueue.Count > 60) _fpsQueue.Dequeue();
        FpsHistory = _fpsQueue.ToArray();
    }

    private void SyncState()
    {
        var g = GamingService.Instance;
        IsActive = g.IsActive;
        Actions.Clear();
        foreach (var a in g.AppliedActions) Actions.Add(a);
        if (g.IsActive && Actions.Count == 0) Actions.Add("FPS- und Temperatur-Aufzeichnung läuft");
        SinceText = g.IsActive ? $"aktiv seit {Format.Duration(DateTime.Now - g.Since)}" : "";
    }

    private bool CanToggle() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleAsync()
    {
        IsBusy = true;
        try
        {
            if (GamingService.Instance.IsActive)
            {
                var report = await GamingService.Instance.DeactivateAsync();
                if (report != null)
                    TrayService.Instance?.Notify("Session-Bericht bereit",
                        $"{report.Game}: Ø {report.FpsText} FPS · 1 % Low {report.LowText} · max {report.TempText}", openModule: "gaming");
            }
            else
            {
                _cfg.BoostCloseApps = Apps.Where(a => a.IsSelected).Select(a => a.Name).ToList();
                SettingsService.Save();
                await GamingService.Instance.ActivateAsync();
            }
        }
        finally { IsBusy = false; }
    }

    /// <summary>Laufende Programme, die man beim Spielen schließen könnte (z. B. Browser, Cloud-Sync).</summary>
    private async Task LoadAppsAsync()
    {
        var selected = new HashSet<string>(_cfg.BoostCloseApps, StringComparer.OrdinalIgnoreCase);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = await Task.Run(() => ProcessService.Sample()
            .Where(p => p.Path.Length > 0 && !p.Path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) && !ProcessService.IsCritical(p.Name)
                        && !p.Name.Equals("steam", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Path: g.First().Path, Memory: g.Sum(x => x.Memory)))
            .OrderByDescending(x => selected.Contains(x.Name)).ThenByDescending(x => x.Memory)
            .Take(18)
            .Select(x => (x.Name, x.Path, Info: ProcessService.FileInfo(x.Path), Icon: Native.ExtractIcon(x.Path, 0)))
            .ToList());

        Apps.Clear();
        foreach (var c in candidates)
            Apps.Add(new AppChoice { Name = c.Name, Display = c.Info.Description is { Length: > 0 } d ? d : c.Name, IsSelected = selected.Contains(c.Name), Icon = c.Icon });
        // Gespeicherte, aber gerade nicht laufende Apps trotzdem anzeigen
        foreach (var name in selected.Where(n => Apps.All(a => !a.Name.Equals(n, StringComparison.OrdinalIgnoreCase))))
            Apps.Add(new AppChoice { Name = name, Display = name, IsSelected = true });
        foreach (var a in Apps)
            a.PropertyChanged += (_, _) =>
            {
                _cfg.BoostCloseApps = Apps.Where(x => x.IsSelected).Select(x => x.Name).ToList();
                SettingsService.Save();
            };
    }

    partial void OnSelectedSessionChanged(GameSession? value)
    {
        if (value == null)
        {
            ReportTimes = null;
            return;
        }
        ReportTimes = value.Times;
        ReportFps = [new ChartSeries("FPS", Amber, value.FpsSeries, "")];
        ReportTemps = [new ChartSeries("CPU", Cyan, value.CpuTempSeries, "°C"), new ChartSeries("GPU", Magenta, value.GpuTempSeries, "°C")];
    }
}
