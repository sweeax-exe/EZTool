using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Services;
using Microsoft.Win32;

namespace EZTool.Modules.Folders;

public sealed class FoldersModule : IModule
{
    public string Id => "folders";
    public string Title => "Ordner-Analyse";
    public string Subtitle => "Große, lange nicht genutzte, leere oder verwaiste Ordner finden – und sicher in den Papierkorb verschieben.";
    public string Icon => "";
    public int Order => 230;

    public FrameworkElement CreateView() => new FoldersView { DataContext = new FoldersViewModel() };
}

public sealed partial class RootOption : ObservableObject
{
    public required string Label { get; init; }
    public required string Path { get; init; }
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class FoldersViewModel : ObservableObject
{
    private static readonly int[] AgeSteps = [0, 30, 90, 180, 365, 730];
    private static readonly long[] SizeSteps = [10L << 20, 100L << 20, 500L << 20, 1L << 30, 5L << 30];

    public ObservableCollection<RootOption> Roots { get; } = [];
    public ObservableCollection<FolderEntry> Results { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ModeHint), nameof(ShowRoots), nameof(ShowSize))]
    private string _mode = "stale";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(AgeLabel))]
    private int _ageIndex = 3;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SizeLabel))]
    private int _sizeIndex = 1;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(RecycleCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _resultText = "Wähle einen Modus und starte die Analyse.";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private long _selectedSize;
    [ObservableProperty] private bool _hasResults;

    private CancellationTokenSource? _cts;

    public string AgeLabel => AgeSteps[AgeIndex] switch
    {
        0 => "egal",
        < 365 and var d => $"{d / 30} Monate",
        var d => d / 365 == 1 ? "1 Jahr" : $"{d / 365} Jahre"
    };

    public string SizeLabel => Format.Bytes(SizeSteps[SizeIndex]);
    public bool ShowRoots => Mode != "orphans";
    public bool ShowSize => Mode == "stale";

    public string ModeHint => Mode switch
    {
        "stale" => "Findet Ordner, deren Inhalt seit langer Zeit nicht verändert wurde. Programm- und Systemordner werden ausgelassen – Programme bitte über „Programme“ entfernen.",
        "orphans" => "Durchsucht AppData und ProgramData nach Ordnern, zu denen kein installiertes Programm mehr passt. Bitte vor dem Löschen prüfen – nichts ist vorausgewählt.",
        _ => "Findet Ordner ohne Dateien (auch mit nur leeren Unterordnern)."
    };

    public FoldersViewModel()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Roots.Add(new RootOption { Label = "Benutzerordner", Path = profile, IsSelected = true });
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.DriveType == DriveType.Fixed) Roots.Add(new RootOption { Label = d.Name.TrimEnd('\\'), Path = d.Name });
            }
            catch { /* nicht bereit */ }
        }
    }

    [RelayCommand]
    private void SetMode(string m)
    {
        Mode = m;
        Results.Clear();
        HasResults = false;
        ResultText = "Wähle einen Modus und starte die Analyse.";
        Recalc();
    }

    [RelayCommand]
    private void BrowseRoot()
    {
        var dlg = new OpenFolderDialog { Title = "Ordner für die Analyse wählen" };
        if (dlg.ShowDialog() != true) return;
        foreach (var r in Roots) r.IsSelected = false;
        var existing = Roots.FirstOrDefault(r => r.Path.Equals(dlg.FolderName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.IsSelected = true;
            return;
        }
        Roots.Add(new RootOption { Label = Path.GetFileName(dlg.FolderName.TrimEnd('\\')) is { Length: > 0 } n ? n : dlg.FolderName, Path = dlg.FolderName, IsSelected = true });
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ScanAsync()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        Results.Clear();
        HasResults = false;
        var progress = new Progress<string>(p => ProgressText = p);
        var root = Roots.FirstOrDefault(r => r.IsSelected)?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        int age = AgeSteps[AgeIndex];
        long size = SizeSteps[SizeIndex];

        try
        {
            List<FolderEntry> found = Mode switch
            {
                "stale" => await Task.Run(() => FolderService.FindStale(root, age, size, progress, token), token),
                "orphans" => await Task.Run(() => FolderService.FindOrphans(ProgramService.LoadAll().Concat(StoreAppService.LoadAll()).ToList(), Math.Max(age, 30), progress, token), token),
                _ => await Task.Run(() => FolderService.FindEmpty(root, progress, token), token),
            };

            foreach (var f in found.Take(1000))
            {
                f.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(FolderEntry.IsChecked)) Recalc();
                };
                Results.Add(f);
            }
            HasResults = Results.Count > 0;
            ResultText = Results.Count == 0
                ? "Nichts gefunden – hier ist alles aufgeräumt. ✨"
                : $"{Results.Count} Ordner gefunden · zusammen {Format.Bytes(Results.Sum(r => r.Size))}";
        }
        catch (OperationCanceledException)
        {
            ResultText = "Analyse abgebrochen.";
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
            Recalc();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void Recalc()
    {
        SelectedCount = Results.Count(r => r.IsChecked);
        SelectedSize = Results.Where(r => r.IsChecked).Sum(r => r.Size);
        RecycleCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var r in Results) r.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in Results) r.IsChecked = false;
    }

    [RelayCommand]
    private void Open(FolderEntry? e)
    {
        if (e != null && Directory.Exists(e.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{e.Path}\"") { UseShellExecute = true });
    }

    private bool CanRecycle() => !IsBusy && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanRecycle))]
    private async Task RecycleAsync()
    {
        var chosen = Results.Where(r => r.IsChecked).ToList();
        if (!NeonDialog.Show("In den Papierkorb verschieben?",
                $"{chosen.Count} Ordner ({Format.Bytes(chosen.Sum(c => c.Size))}) werden in den Papierkorb verschoben. Du kannst sie dort jederzeit wiederherstellen.",
                "Verschieben", danger: true, details: chosen.Select(c => c.Path)))
            return;

        IsBusy = true;
        int ok = 0, fail = 0;
        foreach (var c in chosen)
        {
            ProgressText = c.Path;
            if (await Task.Run(() => FolderService.Recycle(c)))
            {
                ok++;
                Results.Remove(c);
            }
            else
            {
                fail++;
                c.Status = "Konnte nicht verschoben werden (in Benutzung?)";
            }
        }
        IsBusy = false;
        ProgressText = "";
        HasResults = Results.Count > 0;
        ResultText = $"{ok} Ordner in den Papierkorb verschoben" + (fail > 0 ? $" · {fail} fehlgeschlagen" : "");
        Recalc();
    }
}
