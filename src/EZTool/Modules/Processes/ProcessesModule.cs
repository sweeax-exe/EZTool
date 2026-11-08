using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Services;

namespace EZTool.Modules.Processes;

public sealed class ProcessesModule : IModule
{
    public string Id => "processes";
    public string Title => "Prozesse";
    public string Subtitle => "Was frisst gerade CPU und Arbeitsspeicher? Live, gruppiert nach Programm – mit einem Klick beenden.";
    public string Icon => "";
    public int Order => 20;

    public FrameworkElement CreateView() => new ProcessesView { DataContext = new ProcessesViewModel() };
}

/// <summary>Ein Programm mit all seinen Prozessen (z. B. "chrome" mit 30 Prozessen).</summary>
public sealed partial class ProcessGroupVm : ObservableObject
{
    public required string Key { get; init; }
    public required string ProcessName { get; init; }
    public bool IsCritical { get; init; }
    public bool CanKill => !IsCritical;

    public List<int> Pids { get; set; } = [];

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _company = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMany))] private int _count;
    public bool HasMany => Count > 1;
    [ObservableProperty] private double _cpu;
    [ObservableProperty] private long _memory;
    [ObservableProperty] private ImageSource? _icon;
}

public sealed partial class ProcessesViewModel : ObservableObject
{
    public ObservableCollection<ProcessGroupVm> Groups { get; } = [];
    public ICollectionView GroupsView { get; }

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _sort = "cpu";
    [ObservableProperty] private int _processCount;
    [ObservableProperty] private double _totalCpu;
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private double _ramLoad;
    [ObservableProperty] private double[] _cpuHistory = [];
    [ObservableProperty] private bool _isLoading = true;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<int, long> _lastTicks = new();
    private readonly Dictionary<string, ProcessGroupVm> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastSample;
    private bool _sampling;

    public ProcessesViewModel()
    {
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Groups);
        view.Filter = o => o is ProcessGroupVm g && (Search.Length == 0
            || g.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || g.ProcessName.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || g.Company.Contains(Search, StringComparison.OrdinalIgnoreCase));
        view.IsLiveSorting = true;
        view.LiveSortingProperties.Add(nameof(ProcessGroupVm.Cpu));
        view.LiveSortingProperties.Add(nameof(ProcessGroupVm.Memory));
        GroupsView = view;
        ApplySort();

        _timer.Tick += async (_, _) => await RefreshAsync();
        HardwareService.Instance.Updated += s =>
        {
            TotalCpu = s.CpuLoad;
            RamLoad = s.RamLoad;
            RamText = $"{Format.Bytes(s.RamUsed)} / {Format.Bytes(s.RamTotal)}";
            CpuHistory = s.CpuLoadHistory;
        };
    }

    /// <summary>Nur aktualisieren, solange die Seite sichtbar ist – spart Ressourcen.</summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            _ = RefreshAsync();
            _timer.Start();
        }
        else _timer.Stop();
    }

    partial void OnSearchChanged(string value) => GroupsView.Refresh();
    partial void OnSortChanged(string value) => ApplySort();

    [RelayCommand]
    private void SetSort(string s) => Sort = s;

    private void ApplySort()
    {
        using (GroupsView.DeferRefresh())
        {
            GroupsView.SortDescriptions.Clear();
            GroupsView.SortDescriptions.Add(Sort switch
            {
                "ram" => new SortDescription(nameof(ProcessGroupVm.Memory), ListSortDirection.Descending),
                "name" => new SortDescription(nameof(ProcessGroupVm.DisplayName), ListSortDirection.Ascending),
                _ => new SortDescription(nameof(ProcessGroupVm.Cpu), ListSortDirection.Descending),
            });
        }
    }

    private async Task RefreshAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var samples = await Task.Run(ProcessService.Sample);
            var now = DateTime.UtcNow;
            double elapsedTicks = _lastSample == default ? 0 : (now - _lastSample).Ticks * (double)Environment.ProcessorCount;
            _lastSample = now;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var grp in samples.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            {
                var key = grp.Key;
                seen.Add(key);
                double cpu = 0;
                foreach (var s in grp)
                {
                    if (elapsedTicks > 0 && _lastTicks.TryGetValue(s.Pid, out var prev) && s.CpuTicks >= prev)
                        cpu += (s.CpuTicks - prev) / elapsedTicks * 100;
                    _lastTicks[s.Pid] = s.CpuTicks;
                }

                if (!_byKey.TryGetValue(key, out var vm))
                {
                    var path = grp.Select(s => s.Path).FirstOrDefault(p => p.Length > 0) ?? "";
                    vm = new ProcessGroupVm { Key = key, ProcessName = key, IsCritical = ProcessService.IsCritical(key), DisplayName = key, Path = path };
                    _byKey[key] = vm;
                    Groups.Add(vm);
                    _ = LoadDetailsAsync(vm);
                }
                vm.Pids = grp.Select(s => s.Pid).ToList();
                vm.Count = vm.Pids.Count;
                vm.Cpu = Math.Round(Math.Clamp(cpu, 0, 100), 1);
                vm.Memory = grp.Sum(s => s.Memory);
            }

            foreach (var gone in _byKey.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                Groups.Remove(_byKey[gone]);
                _byKey.Remove(gone);
            }
            var alive = samples.Select(s => s.Pid).ToHashSet();
            foreach (var pid in _lastTicks.Keys.Where(p => !alive.Contains(p)).ToList()) _lastTicks.Remove(pid);

            ProcessCount = samples.Count;
            IsLoading = false;
        }
        finally { _sampling = false; }
    }

    private static async Task LoadDetailsAsync(ProcessGroupVm vm)
    {
        if (vm.Path.Length == 0) return;
        var (desc, company, icon) = await Task.Run(() =>
        {
            var (d, c) = ProcessService.FileInfo(vm.Path);
            return (d, c, Native.ExtractIcon(vm.Path, 0));
        });
        if (desc.Length > 0) vm.DisplayName = desc;
        vm.Company = company;
        vm.Icon = icon;
    }

    [RelayCommand]
    private void Reveal(ProcessGroupVm? g)
    {
        if (g != null && File.Exists(g.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{g.Path}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task KillAsync(ProcessGroupVm? g)
    {
        if (g == null || g.IsCritical) return;
        var what = g.Count > 1 ? $"alle {g.Count} Prozesse von „{g.DisplayName}“" : $"„{g.DisplayName}“";
        if (!NeonDialog.Show("Prozess beenden?", $"{what} werden sofort beendet. Nicht gespeicherte Daten in diesem Programm gehen verloren.",
                "Beenden", danger: true))
            return;

        var (ok, fail) = await Task.Run(() => ProcessService.Kill(g.Pids));
        if (fail > 0)
            NeonDialog.Show("Nicht vollständig beendet", $"{ok} beendet, {fail} konnten nicht beendet werden (geschützt oder bereits beendet).", cancelText: null);
        await RefreshAsync();
    }
}
