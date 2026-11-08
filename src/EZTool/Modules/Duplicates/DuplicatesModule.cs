using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Modules.Folders;
using EZTool.Services;
using Microsoft.Win32;

namespace EZTool.Modules.Duplicates;

public sealed class DuplicatesModule : IModule
{
    public string Id => "duplicates";
    public string Title => "Duplikate";
    public string Subtitle => "Findet doppelte Fotos, Videos, Musik und Dokumente – Byte für Byte verglichen, nicht nur nach Namen.";
    public string Icon => "";
    public int Order => 240;

    public FrameworkElement CreateView() => new DuplicatesView { DataContext = new DuplicatesViewModel() };
}

public sealed partial class DuplicatesViewModel : ObservableObject
{
    private static readonly long[] SizeSteps = [100L << 10, 1L << 20, 10L << 20, 100L << 20];
    private const int MaxGroupsShown = 300;

    public ObservableCollection<RootOption> Roots { get; } = [];
    public ObservableCollection<DuplicateGroup> Groups { get; } = [];

    [ObservableProperty] private string _type = "all";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SizeLabel))] private int _sizeIndex = 1;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(RecycleCommand))] private bool _isBusy;
    [ObservableProperty] private string _phase = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _resultText = "Wähle einen Ort und starte die Suche.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private long _wastedTotal;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private long _selectedSize;

    public string SizeLabel => "ab " + Format.Bytes(SizeSteps[SizeIndex]);

    private CancellationTokenSource? _cts;

    public DuplicatesViewModel()
    {
        Roots.Add(new RootOption { Label = "Benutzerordner", Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), IsSelected = true });
        Roots.Add(new RootOption { Label = "Bilder", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) });
        Roots.Add(new RootOption { Label = "Downloads", Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") });
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
    private void SetType(string t) => Type = t;

    [RelayCommand]
    private void BrowseRoot()
    {
        var dlg = new OpenFolderDialog { Title = "Ordner nach Duplikaten durchsuchen" };
        if (dlg.ShowDialog() != true) return;
        foreach (var r in Roots) r.IsSelected = false;
        Roots.Add(new RootOption { Label = Path.GetFileName(dlg.FolderName.TrimEnd('\\')) is { Length: > 0 } n ? n : dlg.FolderName, Path = dlg.FolderName, IsSelected = true });
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ScanAsync()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        IsBusy = true;
        Groups.Clear();
        HasResults = false;
        var root = Roots.FirstOrDefault(r => r.IsSelected)?.Path ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var progress = new Progress<DuplicateService.Progress>(p =>
        {
            Phase = p.Phase;
            Detail = p.Detail;
        });

        try
        {
            var found = await Task.Run(() => DuplicateService.Find(root, SizeSteps[SizeIndex], Type, progress, token), token);
            foreach (var g in found.Take(MaxGroupsShown))
            {
                foreach (var f in g.Files)
                {
                    f.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(DuplicateFile.IsChecked)) Recalc();
                    };
                }
                Groups.Add(g);
            }
            // Nur bei Medien automatisch vorauswählen – bei "Alle" entscheidet der Nutzer selbst.
            if (Type is "images" or "videos" or "music") KeepOldest();
            WastedTotal = found.Sum(g => g.Wasted);
            HasResults = Groups.Count > 0;
            ResultText = found.Count == 0
                ? "Keine Duplikate gefunden – alles einzigartig. ✨"
                : $"{found.Count:N0} Gruppen · {found.Sum(g => g.Files.Count - 1):N0} überflüssige Kopien" +
                  (found.Count > MaxGroupsShown ? $" (die {MaxGroupsShown} größten werden angezeigt)" : "");
        }
        catch (OperationCanceledException)
        {
            ResultText = "Suche abgebrochen.";
        }
        finally
        {
            IsBusy = false;
            Phase = Detail = "";
            Recalc();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>In jeder Gruppe die älteste Datei (das "Original") behalten, alle Kopien auswählen.</summary>
    [RelayCommand]
    private void KeepOldest() => AutoSelect(g => g.Files.OrderBy(f => f.Created).ThenBy(f => f.Modified).First());

    [RelayCommand]
    private void KeepNewest() => AutoSelect(g => g.Files.OrderByDescending(f => f.Modified).First());

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var f in Groups.SelectMany(g => g.Files)) f.IsChecked = false;
    }

    private void AutoSelect(Func<DuplicateGroup, DuplicateFile> keep)
    {
        foreach (var g in Groups)
        {
            var k = keep(g);
            foreach (var f in g.Files) f.IsChecked = !f.IsRemoved && !ReferenceEquals(f, k);
        }
        Recalc();
    }

    private void Recalc()
    {
        int count = 0;
        long size = 0;
        foreach (var g in Groups)
        foreach (var f in g.Files)
        {
            if (!f.IsChecked || f.IsRemoved) continue;
            count++;
            size += g.Size;
        }
        SelectedCount = count;
        SelectedSize = size;
        RecycleCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Open(DuplicateFile? f)
    {
        if (f != null && File.Exists(f.Path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{f.Path}\"") { UseShellExecute = true });
    }

    private bool CanRecycle() => !IsBusy && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanRecycle))]
    private async Task RecycleAsync()
    {
        // Sicherheitsnetz: niemals ALLE Exemplare einer Gruppe löschen
        var allInGroup = Groups.Where(g => g.Files.Where(f => !f.IsRemoved).All(f => f.IsChecked)).ToList();
        if (allInGroup.Count > 0)
        {
            NeonDialog.Show("Mindestens eine Kopie behalten",
                $"In {allInGroup.Count} Gruppe(n) sind alle Exemplare ausgewählt. Bitte lass in jeder Gruppe mindestens eine Datei stehen.",
                cancelText: null, danger: true, details: allInGroup.Take(10).Select(g => g.Files[0].Name));
            return;
        }

        var chosen = Groups.SelectMany(g => g.Files.Where(f => f.IsChecked && !f.IsRemoved)).ToList();
        if (!NeonDialog.Show("Duplikate in den Papierkorb?",
                $"{chosen.Count:N0} Dateien ({Format.Bytes(SelectedSize)}) werden in den Papierkorb verschoben. In jeder Gruppe bleibt mindestens ein Exemplar erhalten.",
                "Verschieben", danger: true))
            return;

        IsBusy = true;
        int ok = 0, fail = 0;
        foreach (var f in chosen)
        {
            Detail = f.Path;
            if (await Task.Run(() => Native.MoveToRecycleBin(f.Path)))
            {
                ok++;
                f.IsChecked = false;
                f.IsRemoved = true;
            }
            else fail++;
        }
        IsBusy = false;
        Detail = "";
        ResultText = $"{ok:N0} Duplikate in den Papierkorb verschoben" + (fail > 0 ? $" · {fail} fehlgeschlagen" : "");
        Recalc();
    }
}
