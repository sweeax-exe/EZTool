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
    public string Title => "Programme";
    public string Subtitle => "Installierte Software verwalten – auswählen, deinstallieren und Reste gleich mit entfernen.";
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

    private void ApplySort()
    {
        using (ProgramsView.DeferRefresh())
        {
            ProgramsView.SortDescriptions.Clear();
            ProgramsView.SortDescriptions.Add(Sort switch
            {
                "size" => new SortDescription(nameof(InstalledProgram.Size), ListSortDirection.Descending),
                "date" => new SortDescription(nameof(InstalledProgram.InstallDate), ListSortDirection.Descending),
                _ => new SortDescription(nameof(InstalledProgram.Name), ListSortDirection.Ascending)
            });
        }
    }

    [RelayCommand]
    private void SetSort(string s) => Sort = s;

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
        else NeonDialog.Show("Kein Ordner", "Für dieses Programm ist kein Installationsordner hinterlegt.", cancelText: null);
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
            ? $"„{list[0].Name}“ wird deinstalliert. Das Deinstallationsprogramm des Herstellers wird gestartet."
            : $"{list.Count} Programme werden nacheinander deinstalliert. Für jedes Programm startet das Deinstallationsprogramm des Herstellers.";
        msg += "\n\nDanach sucht EZTool nach übrig gebliebenen Ordnern und Registry-Einträgen.";
        if (!NeonDialog.Show("Deinstallieren?", msg, "Deinstallieren", danger: true,
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
            QueueText = list.Count > 1 ? $"Programm {i + 1} von {list.Count}" : "Warte auf das Deinstallationsprogramm…";
            p.Status = "Wird deinstalliert…";
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
                QueueText = "Suche nach Resten…";
                var remaining = await Task.Run(ProgramService.LoadAll);
                leftovers.AddRange(await Task.Run(() => ProgramService.FindLeftovers(p, remaining)));
            }
            else
            {
                failed.Add(p.Name);
                p.Status = "Nicht entfernt";
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
                NeonDialog.Show("Reste entfernt",
                    $"{ok} Einträge entfernt" + (fail > 0 ? $", {fail} konnten nicht entfernt werden (evtl. in Benutzung)." : ".") +
                    $"\n\nRegistry-Sicherungen: {ProgramService.BackupFolder}", cancelText: null);
            }
        }
        else if (removed.Count > 0 && failed.Count == 0)
        {
            NeonDialog.Show("Fertig", removed.Count == 1 ? $"„{removed[0]}“ wurde vollständig entfernt. Keine Reste gefunden." :
                $"{removed.Count} Programme wurden vollständig entfernt. Keine Reste gefunden.", cancelText: null);
        }

        if (failed.Count > 0)
            NeonDialog.Show("Nicht alles entfernt", "Diese Programme sind noch installiert (abgebrochen oder übersprungen):", cancelText: null, details: failed);

        await LoadAsync();
    }
}
