using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Modules.Network;
using EZTool.Services.Security;

namespace EZTool.Modules.Security;

public sealed class SecurityModule : IModule
{
    public string Id => "security";
    public string Title => Loc.T("Security.Title");
    public string Subtitle => Loc.T("Security.Subtitle");
    public string Icon => "";
    public int Order => 400;

    public FrameworkElement CreateView() => new SecurityView { DataContext = new SecurityViewModel() };
}

public sealed record CheckVm(CheckResult Check)
{
    public string Title => Check.Title;
    public string Detail => Check.Detail;
    public string Level => Check.Level.ToString();
    public bool HasAction => Check.Action != null && Check.Level != CheckLevel.Good;
}

public sealed partial class ToggleVm : ObservableObject
{
    private readonly Func<bool, Task> _apply;
    private bool _loading;
    public string Key { get; }
    public string Title { get; }
    public string Detail { get; }
    public bool Strict { get; }
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _busy;

    public ToggleVm(string key, string title, string detail, bool isOn, bool strict, Func<bool, Task> apply)
    {
        Key = key;
        Title = title;
        Detail = detail;
        Strict = strict;
        _apply = apply;
        _loading = true;
        IsOn = isOn;
        _loading = false;
    }

    /// <summary>Wert setzen, ohne die Änderung erneut an Windows zu schicken.</summary>
    public void Load(bool value)
    {
        _loading = true;
        IsOn = value;
        _loading = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_loading) _ = _apply(value);
    }
}

public sealed record SuspectVm(Suspect Item, AppIdentity App)
{
    public string Path => Item.Path;
    public IReadOnlyList<string> Reasons => Item.Reasons.Select(r => Loc.T("Security.Reason." + r)).ToList();
    public bool High => Item.Risk >= 4;
}

public sealed record ThreatVm(ThreatInfo Threat)
{
    public string Name => Threat.Name;
    public string Severity => Loc.T("Security.Severity." + Math.Clamp(Threat.Severity, 0, 5));
    public bool Serious => Threat.Severity >= 4;
    public string When => Threat.Detected?.ToString("g", Loc.Culture) ?? "—";
    public string Status => Loc.T("Security.ThreatStatus." + Threat.Status);
    public string Files => string.Join("\n", Threat.Files.Take(3));
    public bool Active => Threat.Active;
}

public sealed partial class SecurityViewModel : ObservableObject, IActivatable
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _scanStart;
    private CancellationTokenSource? _scanCts;
    private bool _loaded;

    // Messfeld
    [ObservableProperty] private int _score;
    [ObservableProperty] private string _scoreText = "—";
    [ObservableProperty] private string _avText = "—";
    [ObservableProperty] private string _avDetail = "";
    [ObservableProperty] private bool _avOk;
    [ObservableProperty] private string _sigText = "—";
    [ObservableProperty] private string _sigDetail = "";
    [ObservableProperty] private string _lastScanText = "—";
    [ObservableProperty] private string _lastScanDetail = "";
    [ObservableProperty] private bool _tamperProtected;
    [ObservableProperty] private bool _defenderActive;

    [ObservableProperty] private string _tab = "overview";
    [ObservableProperty] private bool _loading;

    // Scan
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private string _scanTitle = "";
    [ObservableProperty] private string _scanStatus = "";
    [ObservableProperty] private string? _scanResult;
    [ObservableProperty] private bool _scanFoundThreats;

    // Härtung
    [ObservableProperty] private string? _hardeningMessage;

    // Auffälliges
    [ObservableProperty] private bool _searchingSuspects;
    [ObservableProperty] private bool _suspectsChecked;

    public ObservableCollection<CheckVm> Checks { get; } = [];
    public ObservableCollection<ToggleVm> Protections { get; } = [];
    public ObservableCollection<ToggleVm> AsrRules { get; } = [];
    public ObservableCollection<SuspectVm> Suspects { get; } = [];
    public ObservableCollection<ThreatVm> Threats { get; } = [];

    public bool HasActiveThreats => Threats.Any(t => t.Active);

    public SecurityViewModel()
    {
        _clock.Tick += (_, _) => ScanStatus = Loc.T("Security.ScanRunning", Format.Duration(DateTime.Now - _scanStart));
    }

    public void OnActivated()
    {
        if (_loaded) return;
        _loaded = true;
        _ = RefreshAsync();
    }

    public bool IsOverview => Tab == "overview";
    public bool IsScan => Tab == "scan";
    public bool IsHardening => Tab == "hardening";
    public bool IsSuspects => Tab == "suspects";
    public bool IsThreats => Tab == "threats";

    partial void OnTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsScan));
        OnPropertyChanged(nameof(IsHardening));
        OnPropertyChanged(nameof(IsSuspects));
        OnPropertyChanged(nameof(IsThreats));
        if (value == "suspects" && !SuspectsChecked && !SearchingSuspects) _ = FindSuspectsAsync();
    }

    [RelayCommand]
    private void SetTab(string tab) => Tab = tab;

    // ───────────── Laden ─────────────

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Loading = true;
        var (status, prefs, checks, threats) = await Task.Run(() =>
        {
            var s = DefenderService.Status();
            var p = DefenderService.Prefs();
            return (s, p, SecurityChecks.Run(s, p), DefenderService.Threats());
        });

        DefenderActive = status.Available && status.AntivirusEnabled && status.OtherAntivirus == null;
        TamperProtected = status.TamperProtected;
        AvOk = status.OtherAntivirus != null || (status.AntivirusEnabled && status.RealTime);
        AvText = status.OtherAntivirus != null ? status.OtherAntivirus
            : AvOk ? Loc.T("Security.Protected") : Loc.T("Security.NotProtected");
        AvDetail = status.OtherAntivirus != null ? Loc.T("Security.DefenderPassive")
            : status.RealTime ? Loc.T("Security.RealTimeOn") : Loc.T("Security.RealTimeOff");
        SigText = status.SignaturesUpdated is { } su ? Format.Age(su) : "—";
        SigDetail = status.SignatureVersion.Length > 0 ? Loc.T("Security.SigVersion", status.SignatureVersion) : "";
        var last = new[] { (status.QuickScanEnd, Loc.T("Security.QuickScan")), (status.FullScanEnd, Loc.T("Security.FullScan")) }
            .Where(x => x.Item1 is { Year: > 2000 }).OrderByDescending(x => x.Item1).FirstOrDefault();
        LastScanText = last.Item1 is { } lt ? Format.Age(lt) : "—";
        LastScanDetail = last.Item1 is { } ld ? $"{last.Item2}, {ld.ToString("g", Loc.Culture)}" : Loc.T("Security.NeverScanned");

        Checks.Clear();
        foreach (var c in checks.OrderBy(c => c.Level switch { CheckLevel.Bad => 0, CheckLevel.Warn => 1, CheckLevel.Info => 3, _ => 2 })) Checks.Add(new CheckVm(c));
        UpdateScore();
        BuildHardening(prefs);

        Threats.Clear();
        foreach (var t in threats) Threats.Add(new ThreatVm(t));
        OnPropertyChanged(nameof(HasActiveThreats));
        Loading = false;

        // Windows Update dauert länger – kommt nach
        var wu = await SecurityChecks.WindowsUpdateAsync();
        Checks.Add(new CheckVm(wu));
        UpdateScore();
    }

    private void UpdateScore()
    {
        Score = SecurityChecks.Score(Checks.Select(c => c.Check));
        ScoreText = Score.ToString();
    }

    // ───────────── Härtung ─────────────

    private void BuildHardening(DefenderPrefs p)
    {
        Protections.Clear();
        Protections.Add(new ToggleVm("cfa", Loc.T("Security.Hard.Cfa"), Loc.T("Security.Hard.CfaText"), p.ControlledFolderAccess == 1, false,
            on => SetPrefAsync("EnableControlledFolderAccess", on ? 1 : 0, "cfa")));
        Protections.Add(new ToggleVm("pua", Loc.T("Security.Hard.Pua"), Loc.T("Security.Hard.PuaText"), p.Pua == 1, false,
            on => SetPrefAsync("PUAProtection", on ? 1 : 0, "pua")));
        Protections.Add(new ToggleVm("np", Loc.T("Security.Hard.Network"), Loc.T("Security.Hard.NetworkText"), p.NetworkProtection == 1, false,
            on => SetPrefAsync("EnableNetworkProtection", on ? 1 : 0, "np")));
        Protections.Add(new ToggleVm("cloud", Loc.T("Security.Hard.Cloud"), Loc.T("Security.Hard.CloudText"), p.CloudBlockLevel >= 2, false,
            on => SetPrefAsync("CloudBlockLevel", on ? 2 : 0, "cloud")));

        AsrRules.Clear();
        foreach (var r in DefenderService.AsrRules)
        {
            bool on = p.AsrRules.TryGetValue(r.Id, out var a) && a is 1 or 6;
            var rule = r;
            AsrRules.Add(new ToggleVm(r.Id.ToString(), Loc.T("Security.Asr." + r.Key), Loc.T("Security.Asr." + r.Key + "Text"), on, r.Strict,
                v => SetAsrAsync(rule.Id, v)));
        }
    }

    private async Task SetPrefAsync(string name, int value, string key)
    {
        var t = Protections.First(x => x.Key == key);
        t.Busy = true;
        HardeningMessage = null;
        var err = await DefenderService.SetPreferenceAsync(name, value);
        await Verify(err);
        t.Busy = false;
    }

    private async Task SetAsrAsync(Guid id, bool on)
    {
        var t = AsrRules.First(x => x.Key == id.ToString());
        t.Busy = true;
        HardeningMessage = null;
        var err = await DefenderService.SetAsrAsync(id, on);
        await Verify(err);
        t.Busy = false;
    }

    /// <summary>Nach jeder Änderung nachlesen, was Windows wirklich gespeichert hat (Manipulationsschutz kann blockieren).</summary>
    private async Task Verify(string? error)
    {
        var p = await Task.Run(DefenderService.Prefs);
        var actual = new Dictionary<string, bool>
        {
            ["cfa"] = p.ControlledFolderAccess == 1,
            ["pua"] = p.Pua == 1,
            ["np"] = p.NetworkProtection == 1,
            ["cloud"] = p.CloudBlockLevel >= 2,
        };
        bool mismatch = false;
        foreach (var t in Protections)
        {
            if (t.IsOn != actual[t.Key]) mismatch = true;
            t.Load(actual[t.Key]);
        }
        foreach (var t in AsrRules)
        {
            bool on = p.AsrRules.TryGetValue(Guid.Parse(t.Key), out var a) && a is 1 or 6;
            if (t.IsOn != on) mismatch = true;
            t.Load(on);
        }
        if (error != null || mismatch)
            HardeningMessage = TamperProtected ? Loc.T("Security.Hard.Blocked") : Loc.T("Security.Hard.Failed", error ?? "—");
    }

    [RelayCommand]
    private async Task EnableRecommended()
    {
        var ids = DefenderService.AsrRules.Where(r => !r.Strict).Select(r => r.Id).ToList();
        foreach (var t in AsrRules.Where(t => !t.Strict)) t.Busy = true;
        HardeningMessage = null;
        var err = await DefenderService.SetAsrManyAsync(ids, true);
        await Verify(err);
        foreach (var t in AsrRules) t.Busy = false;
    }

    [RelayCommand]
    private async Task DisableAllAsr()
    {
        foreach (var t in AsrRules) t.Busy = true;
        HardeningMessage = null;
        var err = await DefenderService.SetAsrManyAsync(DefenderService.AsrRules.Select(r => r.Id), false);
        await Verify(err);
        foreach (var t in AsrRules) t.Busy = false;
    }

    [RelayCommand]
    private void OpenWindowsSecurity() => SecurityChecks.Open("windowsdefender://threat/");

    [RelayCommand]
    private void OpenCheck(CheckVm? c)
    {
        if (c?.Check.Action is { } a) SecurityChecks.Open(a);
    }

    // ───────────── Scannen ─────────────

    [RelayCommand]
    private Task QuickScan() => RunScanAsync(1, null, Loc.T("Security.QuickScan"));

    [RelayCommand]
    private Task FullScan() => RunScanAsync(2, null, Loc.T("Security.FullScan"));

    [RelayCommand]
    private Task CustomScan()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Security.PickFolder") };
        return dlg.ShowDialog() == true ? RunScanAsync(3, dlg.FolderName, Loc.T("Security.CustomScanOf", dlg.FolderName)) : Task.CompletedTask;
    }

    [RelayCommand]
    private Task CheckFile(SuspectVm? s)
    {
        if (s == null) return Task.CompletedTask;
        Tab = "scan";
        return RunScanAsync(3, s.Path, Loc.T("Security.CustomScanOf", s.App.Name));
    }

    [RelayCommand]
    private async Task UpdateSignatures()
    {
        if (Scanning) return;
        Scanning = true;
        ScanTitle = Loc.T("Security.UpdatingSignatures");
        ScanResult = null;
        _scanStart = DateTime.Now;
        _clock.Start();
        _scanCts = new CancellationTokenSource();
        try
        {
            var r = await DefenderService.UpdateSignaturesAsync(_scanCts.Token);
            ScanResult = r.Ok ? Loc.T("Security.SignaturesUpdated") : Loc.T("Security.SignaturesFailed");
            ScanFoundThreats = false;
        }
        catch (OperationCanceledException) { ScanResult = Loc.T("Security.ScanCancelled"); }
        finally
        {
            _clock.Stop();
            Scanning = false;
        }
        await RefreshAsync();
    }

    private async Task RunScanAsync(int type, string? path, string title)
    {
        if (Scanning) return;
        Scanning = true;
        ScanTitle = title;
        ScanResult = null;
        ScanFoundThreats = false;
        _scanStart = DateTime.Now;
        ScanStatus = Loc.T("Security.ScanRunning", Format.Duration(TimeSpan.Zero));
        _clock.Start();
        _scanCts = new CancellationTokenSource();
        try
        {
            var r = await DefenderService.ScanAsync(type, path, _scanCts.Token);
            ScanFoundThreats = r.ThreatsFound;
            ScanResult = !r.Ok ? Loc.T("Security.ScanFailed")
                : r.ThreatsFound ? Loc.T("Security.ScanThreats")
                : Loc.T("Security.ScanClean", Format.Duration(DateTime.Now - _scanStart));
        }
        catch (OperationCanceledException) { ScanResult = Loc.T("Security.ScanCancelled"); }
        finally
        {
            _clock.Stop();
            Scanning = false;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private async Task OfflineScan()
    {
        if (!NeonDialog.Show(Loc.T("Security.OfflineTitle"), Loc.T("Security.OfflineConfirm"), Loc.T("Security.OfflineStart"), danger: true)) return;
        var err = await DefenderService.StartOfflineScanAsync();
        if (err != null) NeonDialog.Show(Loc.T("Security.OfflineTitle"), err, cancelText: null, danger: true);
    }

    // ───────────── Auffälliges ─────────────

    [RelayCommand]
    private async Task FindSuspectsAsync()
    {
        SearchingSuspects = true;
        var found = await Task.Run(SecurityChecks.FindSuspects);
        Suspects.Clear();
        foreach (var s in found) Suspects.Add(new SuspectVm(s, AppIdentity.ForPath(s.Path, System.IO.Path.GetFileNameWithoutExtension(s.Path))));
        SearchingSuspects = false;
        SuspectsChecked = true;
    }

    [RelayCommand]
    private void Reveal(SuspectVm? s)
    {
        if (s != null && System.IO.File.Exists(s.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{s.Path}\"") { UseShellExecute = true });
    }

    // ───────────── Bedrohungen ─────────────

    [RelayCommand]
    private async Task RemoveThreats()
    {
        if (!NeonDialog.Show(Loc.T("Security.RemoveTitle"), Loc.T("Security.RemoveConfirm"), Loc.T("Security.RemoveStart"), danger: true)) return;
        var err = await DefenderService.RemoveActiveThreatsAsync();
        if (err != null) NeonDialog.Show(Loc.T("Security.RemoveTitle"), err, cancelText: null, danger: true);
        await RefreshAsync();
    }
}
