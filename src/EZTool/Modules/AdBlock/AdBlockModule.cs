using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Services;
using EZTool.Services.AdBlock;

namespace EZTool.Modules.AdBlock;

public sealed class AdBlockModule : IModule
{
    public string Id => "adblock";
    public string Title => Loc.T("AdBlock.Title");
    public string Subtitle => Loc.T("AdBlock.Subtitle");
    public string Icon => "";
    public int Order => 405;

    public FrameworkElement CreateView() => new AdBlockView { DataContext = new AdBlockViewModel() };
}

public sealed class LogVm
{
    public QueryEntry Entry { get; }
    public string Time => Entry.Time.ToString("HH:mm:ss");
    public string Domain => Entry.Domain;
    public string Type => Entry.Type switch { 1 => "A", 28 => "AAAA", 65 => "HTTPS", 5 => "CNAME", 12 => "PTR", 16 => "TXT", 33 => "SRV", 15 => "MX", _ => Entry.Type.ToString() };
    public bool IsBlocked => Entry.Status == QueryStatus.Blocked;
    public string Status => Loc.T("AdBlock.Status" + Entry.Status);
    /// <summary>Schlüssel der Farbe im Theme.</summary>
    public string StatusKind => Entry.Status.ToString();
    public string Via => Entry.Via == null ? "" : Loc.T("AdBlock.ViaCname", Entry.Via);
    public string Ms => Entry.Status is QueryStatus.Blocked or QueryStatus.Cached ? "" : $"{Entry.Ms} ms";
    public bool CanAct => Entry.Status != QueryStatus.Local;

    public LogVm(QueryEntry e) => Entry = e;
}

public sealed record TopVm(string Domain, int Count, double Percent);

public sealed record RuleVm(string Domain, bool Allow)
{
    public string Kind => Loc.T(Allow ? "AdBlock.RuleAllowed" : "AdBlock.RuleBlocked");
}

public sealed partial class ListVm : ObservableObject
{
    private readonly AdBlockViewModel _owner;
    public BlockListInfo Info { get; }
    public string Name => Info.Name;
    public string Description => Loc.T("AdBlock.List." + Info.Id);

    [ObservableProperty] private bool _isOn;

    public ListVm(BlockListInfo info, AdBlockViewModel owner)
    {
        Info = info;
        _owner = owner;
        _isOn = SettingsService.Current.AdBlockLists.Contains(info.Id);
    }

    partial void OnIsOnChanged(bool value) => _ = _owner.ToggleList(Info.Id, value);
}

public sealed partial class AdBlockViewModel : ObservableObject, IActivatable
{
    private readonly AdBlockService _svc = AdBlockService.Instance;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _seenVersion = -1;
    private QueryEntry? _newest;

    public ObservableCollection<LogVm> Log { get; } = [];
    public ObservableCollection<TopVm> Top { get; } = [];
    public ObservableCollection<RuleVm> Rules { get; } = [];
    public IReadOnlyList<ListVm> Lists { get; }
    public IReadOnlyList<UpstreamInfo> Upstreams => AdBlockService.Upstreams;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isUpdatingLists;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _blockedText = "0";
    [ObservableProperty] private string _queriesText = "0";
    [ObservableProperty] private double _blockRate;
    [ObservableProperty] private string _blockRateText = "";
    [ObservableProperty] private string _domainsText = "—";
    [ObservableProperty] private string _listsUpdatedText = "";
    [ObservableProperty] private string _upstream = SettingsService.Current.AdBlockUpstream;
    [ObservableProperty] private bool _onlyBlocked;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _ruleInput = "";
    [ObservableProperty] private string? _ruleError;
    [ObservableProperty] private bool _canRescue;
    [ObservableProperty] private bool _hasLog;
    [ObservableProperty] private bool _hasTop;
    [ObservableProperty] private bool _hasRules;

    public bool IsAdmin { get; } = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public AdBlockViewModel()
    {
        Lists = AdBlockService.Lists.Select(l => new ListVm(l, this)).ToList();
        _timer.Tick += (_, _) => Refresh();
        _svc.Changed += () => Application.Current?.Dispatcher.BeginInvoke(SyncState);
        SyncState();
        SyncRules();
    }

    public void OnActivated()
    {
        _svc.EnsureFilterLoaded();
        Refresh();
    }

    /// <summary>Nur aktualisieren, solange die Seite sichtbar ist.</summary>
    public void Start()
    {
        Refresh();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void SyncState()
    {
        IsRunning = _svc.IsRunning;
        IsBusy = _svc.IsBusy;
        IsUpdatingLists = _svc.IsUpdatingLists;
        Error = _svc.LastError;
        DomainsText = _svc.Filter.Count > 0 ? _svc.Filter.Count.ToString("N0", Loc.Culture) : "—";
        ListsUpdatedText = _svc.ListsUpdated is { } t ? Loc.T("AdBlock.ListsUpdated", t.ToString("g", Loc.Culture)) : Loc.T("AdBlock.ListsNever");
        CanRescue = !_svc.IsRunning && !_svc.IsBusy && DnsSwitcher.HasBackup;
    }

    private void Refresh()
    {
        long q = _svc.QueriesToday, b = _svc.BlockedToday;
        QueriesText = q.ToString("N0", Loc.Culture);
        BlockedText = b.ToString("N0", Loc.Culture);
        BlockRate = q == 0 ? 0 : 100.0 * b / q;
        BlockRateText = Loc.T("AdBlock.BlockRate", BlockRate);

        if (_svc.LogVersion != _seenVersion)
        {
            _seenVersion = _svc.LogVersion;
            AppendLog();
            var top = _svc.TopBlocked(8);
            int max = top.Count > 0 ? top[0].Count : 1;
            Top.Clear();
            foreach (var (d, c) in top) Top.Add(new TopVm(d, c, 100.0 * c / max));
            HasTop = Top.Count > 0;
        }
    }

    /// <summary>Neue Einträge oben einfügen, statt die Liste neu aufzubauen – so bleibt die Scrollposition stabil.</summary>
    private void AppendLog(bool rebuild = false)
    {
        var all = _svc.RecentQueries(); // neueste zuerst
        if (rebuild)
        {
            Log.Clear();
            _newest = null;
        }
        var fresh = new List<QueryEntry>();
        foreach (var e in all)
        {
            if (ReferenceEquals(e, _newest)) break;
            fresh.Add(e);
        }
        if (all.Count > 0) _newest = all[0];
        for (int i = fresh.Count - 1; i >= 0; i--)
            if (Matches(fresh[i])) Log.Insert(0, new LogVm(fresh[i]));
        while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
        HasLog = Log.Count > 0;
    }

    private bool Matches(QueryEntry e) =>
        (!OnlyBlocked || e.Status == QueryStatus.Blocked)
        && (Search.Length == 0 || e.Domain.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));

    partial void OnOnlyBlockedChanged(bool value) => AppendLog(rebuild: true);
    partial void OnSearchChanged(string value) => AppendLog(rebuild: true);

    private void SyncRules()
    {
        Rules.Clear();
        foreach (var d in SettingsService.Current.AdBlockAllow) Rules.Add(new RuleVm(d, true));
        foreach (var d in SettingsService.Current.AdBlockDeny) Rules.Add(new RuleVm(d, false));
        HasRules = Rules.Count > 0;
    }

    // ───────────── Befehle ─────────────

    [RelayCommand]
    private async Task Toggle()
    {
        if (_svc.IsBusy) return;
        if (_svc.IsRunning) await _svc.DisableAsync();
        else await _svc.EnableAsync();
        SyncState();
    }

    [RelayCommand]
    private void SetOnlyBlocked(string value) => OnlyBlocked = value == "True";

    [RelayCommand]
    private Task UpdateLists() => _svc.UpdateListsAsync(force: true);

    [RelayCommand]
    private void SetUpstream(string id)
    {
        Upstream = id;
        _svc.SetUpstream(id);
    }

    [RelayCommand]
    private void Allow(LogVm? item)
    {
        if (item == null) return;
        _svc.AddRule(item.Entry.Via ?? item.Domain, allow: true);
        SyncRules();
    }

    [RelayCommand]
    private void Block(LogVm? item)
    {
        if (item == null) return;
        _svc.AddRule(item.Domain, allow: false);
        SyncRules();
    }

    [RelayCommand]
    private void AddAllow() => AddRule(true);

    [RelayCommand]
    private void AddBlock() => AddRule(false);

    private void AddRule(bool allow)
    {
        var d = BlockFilter.Normalize(RuleInput);
        if (d == null)
        {
            RuleError = Loc.T("AdBlock.RuleInvalid");
            return;
        }
        RuleError = null;
        RuleInput = "";
        _svc.AddRule(d, allow);
        SyncRules();
    }

    [RelayCommand]
    private void RemoveRule(RuleVm? rule)
    {
        if (rule == null) return;
        _svc.RemoveRule(rule.Domain);
        SyncRules();
    }

    [RelayCommand]
    private async Task Rescue()
    {
        await Task.Run(DnsSwitcher.Restore);
        SyncState();
    }

    internal async Task ToggleList(string id, bool on) => await _svc.SetListEnabledAsync(id, on);
}
