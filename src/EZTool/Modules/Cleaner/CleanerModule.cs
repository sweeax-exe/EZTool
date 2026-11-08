using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Services;

namespace EZTool.Modules.Cleaner;

public sealed class CleanerModule : IModule
{
    public string Id => "cleaner";
    public string Title => "Speicher-Cleaner";
    public string Subtitle => "Findet überflüssige Dateien und entfernt sie sicher – persönliche Daten bleiben unberührt.";
    public string Icon => "";
    public int Order => 210;

    public FrameworkElement CreateView() => new CleanerView { DataContext = new CleanerViewModel() };
}

public sealed partial class CleanItemVm : ObservableObject
{
    public CleanRule Rule { get; }
    public string Name => Rule.Name;
    public string Description => Rule.Description;
    public string Category => Rule.Category;
    public string Icon => Rule.Icon;

    public ScanResult? Scan { get; set; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private long _size;
    [ObservableProperty] private int _files;
    [ObservableProperty] private string _state = "Bereit";
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _isDone;

    public CleanItemVm(CleanRule rule)
    {
        Rule = rule;
        _isChecked = rule.DefaultOn;
    }
}

public sealed partial class CleanerViewModel : ObservableObject, IActivatable
{
    public ObservableCollection<CleanItemVm> Items { get; } = [];
    public ICollectionView ItemsView { get; }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CleanCommand))]
    private bool _isBusy;

    [ObservableProperty] private long _selectedBytes;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private int _selectedFiles;
    [ObservableProperty] private long _freedBytes;
    [ObservableProperty] private bool _hasCleaned;
    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private string _status = "Bereit zum Scannen.";
    [ObservableProperty] private double _progress;

    public CleanerViewModel()
    {
        foreach (var r in CleanerService.GetRules())
        {
            var vm = new CleanItemVm(r);
            vm.PropertyChanged += (_, e) =>
            {
                    if (e.PropertyName is nameof(CleanItemVm.IsChecked) or nameof(CleanItemVm.Size)) Recalc();
            };
            Items.Add(vm);
        }

        var cvs = new CollectionViewSource { Source = Items };
        cvs.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CleanItemVm.Category)));
        ItemsView = cvs.View;
    }

    public void OnActivated()
    {
        if (!HasScanned && !IsBusy) ScanCommand.Execute(null);
    }

    private void Recalc()
    {
        SelectedBytes = Items.Where(i => i.IsChecked).Sum(i => i.Size);
        SelectedFiles = Items.Where(i => i.IsChecked).Sum(i => i.Files);
        TotalBytes = Items.Sum(i => i.Size);
        CleanCommand.NotifyCanExecuteChanged();
    }

    private bool CanRun() => !IsBusy;
    private bool CanClean() => !IsBusy && HasScanned && (SelectedBytes > 0 || Items.Any(i => i.IsChecked && i.Rule.SizeUnknown));

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        HasCleaned = false;
        Progress = 0;
        int n = 0;
        foreach (var item in Items)
        {
            item.IsWorking = true;
            item.IsDone = false;
            item.State = "Analysiere…";
            Status = $"Analysiere: {item.Name}";
            var scan = await Task.Run(() => CleanerService.Scan(item.Rule));
            item.Scan = scan;
            item.Size = scan.Bytes;
            item.Files = scan.Files;
            item.State = item.Rule.SizeUnknown ? "Größe wird beim Bereinigen gemessen"
                : scan.Files == 0 ? "Sauber" : $"{Format.Number(scan.Files)} Dateien";
            item.IsWorking = false;
            Progress = ++n * 100.0 / Items.Count;
        }
        HasScanned = true;
        Status = $"Analyse abgeschlossen · {Format.Bytes(Items.Sum(i => i.Size))} gefunden";
        IsBusy = false;
        Recalc();
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var selected = Items.Where(i => i.IsChecked && (i.Size > 0 || i.Rule.SizeUnknown) && i.Scan != null).ToList();
        if (selected.Count == 0) return;

        var running = selected.Where(i => CleanerService.IsAnyRunning(i.Rule)).Select(i => i.Name).ToList();
        var msg = $"{Format.Bytes(selected.Sum(i => i.Size))} in {Format.Number(selected.Sum(i => i.Files))} Dateien werden endgültig gelöscht. " +
                  "Dateien, die gerade verwendet werden, werden automatisch übersprungen.";
        if (selected.Any(i => i.Rule.Category == "Tiefenreinigung"))
            msg += "\n\nDie Tiefenreinigung nutzt die Windows-eigenen Werkzeuge (DISM / Datenträgerbereinigung) und kann mehrere Minuten dauern. Bitte EZTool währenddessen nicht schließen.";
        if (running.Count > 0)
            msg += "\n\nHinweis: Einige Programme laufen noch (z. B. Browser). Schließe sie für ein besseres Ergebnis.";

        if (!NeonDialog.Show("Bereinigung starten?", msg, "Jetzt bereinigen",
                details: selected.Select(i => $"{i.Name}  ·  {Format.Bytes(i.Size)}")))
            return;

        IsBusy = true;
        Progress = 0;
        long freed = 0;
        int skipped = 0, n = 0;
        foreach (var item in selected)
        {
            item.IsWorking = true;
            item.State = "Bereinige…";
            Status = $"Bereinige: {item.Name}";
            var res = await Task.Run(() => CleanerService.Clean(item.Rule, item.Scan!));
            freed += res.FreedBytes;
            skipped += res.Skipped;
            item.IsWorking = false;
            item.IsDone = true;
            item.State = res.Skipped > 0 ? $"{Format.Bytes(res.FreedBytes)} frei · {res.Skipped} gesperrt" : $"{Format.Bytes(res.FreedBytes)} frei";
            item.Size = Math.Max(0, item.Size - res.FreedBytes);
            item.Files = res.Skipped;
            item.Scan = null;
            Progress = ++n * 100.0 / selected.Count;
        }

        FreedBytes = freed;
        HasCleaned = true;
        Status = skipped > 0
            ? $"Fertig! {Format.Bytes(freed)} freigegeben · {Format.Number(skipped)} Dateien waren in Benutzung"
            : $"Fertig! {Format.Bytes(freed)} freigegeben";
        IsBusy = false;
        HasScanned = false; // erneuter Scan nötig, bevor wieder gelöscht werden kann
        Recalc();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in Items) i.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in Items) i.IsChecked = false;
    }
}
