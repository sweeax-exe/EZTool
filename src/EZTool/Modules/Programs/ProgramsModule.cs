using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Services;

namespace EZTool.Modules.Programs;

public sealed class ProgramsModule : IModule
{
    public string Id => "programs";
    public string Title => Loc.T("Programs.Title");
    public string Subtitle => Loc.T("Programs.Subtitle");
    public string Icon => "";
    public int Order => 310;

    public FrameworkElement CreateView() => new ProgramsView { DataContext = new ProgramsViewModel() };
}

public sealed partial class ProgramsViewModel : ObservableObject, IActivatable
{
    public ObservableCollection<InstalledProgram> Programs { get; } = [];
    public ICollectionView ProgramsView { get; }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _sort = "name";
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private string _kind = "all";
    [ObservableProperty] private bool _quietMode;
    [ObservableProperty] private int _count;
    [ObservableProperty] private long _totalSize;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private long _selectedSize;

    // Deinstallations-Status
    [ObservableProperty] private bool _isUninstalling;
    [ObservableProperty] private string _currentName = "";
    [ObservableProperty] private string _queueText = "";

    private CancellationTokenSource? _skip;
    private bool _loaded;

    public ProgramsViewModel()
    {
        ProgramsView = CollectionViewSource.GetDefaultView(Programs);
        ProgramsView.Filter = o => o is InstalledProgram p && (Kind == "all" || p.Kind == Kind) && (Search.Length == 0
            || p.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || p.Publisher.Contains(Search, StringComparison.OrdinalIgnoreCase));
        ApplySort();
    }

    public void OnActivated()
    {
        if (!_loaded) _ = LoadAsync();
    }

    partial void OnSearchChanged(string value) => ProgramsView.Refresh();
    partial void OnSortChanged(string value) => ApplySort();
    partial void OnKindChanged(string value) => ProgramsView.Refresh();

    [RelayCommand]
    private void SetKind(string k) => Kind = k;

    /// <summary>Pfeil im aktiven Spaltenkopf: ▼ absteigend, ▲ aufsteigend.</summary>
    public string SortGlyph => SortDescending ? "" : "";

    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortGlyph));
        ApplySort();
    }

    private void ApplySort()
    {
        if (!_batchSort && ProgramsView is ListCollectionView lcv) lcv.CustomSort = new ProgramComparer(Sort, SortDescending);
    }

    /// <summary>Klick auf einen Spaltenkopf: gleiche Spalte dreht die Richtung, neue Spalte startet
    /// sinnvoll (Name A→Z, Größe/Datum/Version größte bzw. neueste zuerst).</summary>
    [RelayCommand]
    private void SetSort(string s)
    {
        if (s == Sort)
        {
            SortDescending = !SortDescending;
            return;
        }
        _batchSort = true; // Spalte und Richtung zusammen setzen, dann nur einmal sortieren
        Sort = s;
        SortDescending = s != "name";
        _batchSort = false;
        ApplySort();
    }

    private bool _batchSort;

    /// <summary>Sortiert nach Spalte; fehlende Werte (Größe unbekannt, kein Datum) stehen immer unten,
    /// Gleichstände nach Name.</summary>
    private sealed class ProgramComparer(string key, bool descending) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not InstalledProgram a || y is not InstalledProgram b) return 0;
            int r = key switch
            {
                "size" => Missing(a.Size <= 0, b.Size <= 0) ?? Dir(a.Size.CompareTo(b.Size)),
                "date" => Missing(a.InstallDate is null, b.InstallDate is null) ?? Dir(Nullable.Compare(a.InstallDate, b.InstallDate)),
                "version" => Missing(a.Version.Length == 0, b.Version.Length == 0) ?? Dir(Natural(a.Version, b.Version)),
                _ => Dir(ByName(a, b))
            };
            return r != 0 ? r : ByName(a, b);
        }

        private int Dir(int r) => descending ? -r : r;

        private static int? Missing(bool a, bool b) => a == b ? (a ? 0 : null) : a ? 1 : -1;

        private static int ByName(InstalledProgram a, InstalledProgram b) =>
            string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);

        /// <summary>Vergleicht Zahlenblöcke als Zahlen: 10.2 kommt nach 9.14.</summary>
        private static int Natural(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a[si..i].TrimStart('0');
                    var nb = b[sj..j].TrimStart('0');
                    int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++;
                    j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        _loaded = true;
        var list = await Task.Run(() => ProgramService.LoadAll().Concat(StoreAppService.LoadAll())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList());

        foreach (var p in Programs) p.PropertyChanged -= OnProgramChanged;
        Programs.Clear();
        foreach (var p in list)
        {
            p.PropertyChanged += OnProgramChanged;
            Programs.Add(p);
        }
        Recalc();
        IsLoading = false;

        // Icons und fehlende Größen im Hintergrund nachladen
        _ = Task.Run(() =>
        {
            foreach (var p in list)
            {
                var icon = p.IsStore ? StoreAppService.LoadLogo(p) : ProgramService.LoadIcon(p);
                if (icon != null) Application.Current?.Dispatcher.BeginInvoke(() => p.Icon = icon);
            }
            foreach (var p in list.Where(x => x.Size == 0))
            {
                var size = p.IsStore ? StoreAppService.MeasureSize(p) : ProgramService.MeasureInstallFolder(p);
                if (size > 0) Application.Current?.Dispatcher.BeginInvoke(() => p.Size = size);
            }
            // nachgemessene Größen einsortieren (läuft nach allen Größen-Updates in der Dispatcher-Warteschlange)
            Application.Current?.Dispatcher.BeginInvoke(() => { if (Sort == "size") ProgramsView.Refresh(); });
        });
    }

    private void OnProgramChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstalledProgram.IsChecked) or nameof(InstalledProgram.Size)) Recalc();
    }

    private void Recalc()
    {
        Count = Programs.Count;
        TotalSize = Programs.Sum(p => p.Size);
        SelectedCount = Programs.Count(p => p.IsChecked);
        SelectedSize = Programs.Where(p => p.IsChecked).Sum(p => p.Size);
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var p in Programs) p.IsChecked = false;
    }

    [RelayCommand]
    private void OpenLocation(InstalledProgram? p)
    {
        if (p == null) return;
        var dir = Directory.Exists(p.InstallLocation) ? p.InstallLocation : Path.GetDirectoryName(ProgramService.SplitCommand(p.UninstallString).Exe);
        if (dir != null && Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        else NeonDialog.Show(Loc.T("Programs.NoFolderTitle"), Loc.T("Programs.NoFolderText"), cancelText: null);
    }

    [RelayCommand]
    private Task UninstallOne(InstalledProgram? p) => p == null ? Task.CompletedTask : RunUninstall([p]);

    [RelayCommand]
    private Task UninstallSelected() => RunUninstall(Programs.Where(p => p.IsChecked).ToList());

    [RelayCommand]
    private void SkipCurrent() => _skip?.Cancel();

    private async Task RunUninstall(List<InstalledProgram> list)
    {
        list = list.Where(p => p.CanUninstall).ToList();
        if (list.Count == 0 || IsUninstalling) return;

        var msg = list.Count == 1
            ? Loc.T("Programs.ConfirmOne", list[0].Name)
            : Loc.T("Programs.ConfirmMany", list.Count);
        msg += "\n\n" + Loc.T("Programs.ConfirmLeftoverHint");
        if (!NeonDialog.Show(Loc.T("Programs.ConfirmTitle"), msg, Loc.T("Programs.Uninstall"), danger: true,
                details: list.Count > 1 ? list.Select(p => $"{p.Name}  ·  {Format.Bytes(p.Size)}") : null))
            return;

        IsUninstalling = true;
        var leftovers = new List<Leftover>();
        var removed = new List<string>();
        var failed = new List<string>();

        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            CurrentName = p.Name;
            QueueText = list.Count > 1 ? Loc.T("Programs.QueueProgress", i + 1, list.Count) : Loc.T("Programs.QueueWaiting");
            p.Status = Loc.T("Programs.StatusUninstalling");
            _skip = new CancellationTokenSource();

            bool ok;
            try
            {
                if (p.IsStore)
                {
                    await StoreAppService.UninstallAsync(p);
                    ok = !StoreAppService.IsStillInstalled(p);
                }
                else ok = await ProgramService.UninstallAsync(p, QuietMode, _skip.Token);
            }
            catch (Exception ex)
            {
                ok = false;
                p.Status = ex.Message;
            }

            if (ok && p.IsStore)
            {
                removed.Add(p.Name); // Store-Apps entfernen ihre Daten selbst – keine Restesuche nötig
            }
            else if (ok)
            {
                removed.Add(p.Name);
                QueueText = Loc.T("Programs.QueueSearchingLeftovers");
                var remaining = await Task.Run(ProgramService.LoadAll);
                leftovers.AddRange(await Task.Run(() => ProgramService.FindLeftovers(p, remaining)));
            }
            else
            {
                failed.Add(p.Name);
                p.Status = Loc.T("Programs.StatusNotRemoved");
            }
        }

        IsUninstalling = false;
        _skip = null;

        var distinct = leftovers.GroupBy(l => l.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (distinct.Count > 0)
        {
            var chosen = LeftoverDialog.Show(distinct);
            if (chosen.Count > 0)
            {
                var (ok, fail) = await Task.Run(() => ProgramService.RemoveLeftovers(chosen));
                NeonDialog.Show(Loc.T("Programs.LeftoversRemovedTitle"),
                    (fail > 0 ? Loc.T("Programs.LeftoversRemovedPartial", ok, fail) : Loc.T("Programs.LeftoversRemoved", ok)) +
                    "\n\n" + Loc.T("Programs.RegistryBackups", ProgramService.BackupFolder), cancelText: null);
            }
        }
        else if (removed.Count > 0 && failed.Count == 0)
        {
            NeonDialog.Show(Loc.T("Programs.DoneTitle"), removed.Count == 1 ? Loc.T("Programs.DoneOne", removed[0]) :
                Loc.T("Programs.DoneMany", removed.Count), cancelText: null);
        }

        if (failed.Count > 0)
            NeonDialog.Show(Loc.T("Programs.NotAllRemovedTitle"), Loc.T("Programs.NotAllRemovedText"), cancelText: null, details: failed);

        await LoadAsync();
    }
}
