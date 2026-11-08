using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Modules.Folders;
using EZTool.Services;
using Microsoft.Win32;

namespace EZTool.Modules.StorageMap;

public sealed class StorageMapModule : IModule
{
    public string Id => "storagemap";
    public string Title => "Speicher-Karte";
    public string Subtitle => "Dein Laufwerk als Karte: Je größer die Kachel, desto mehr Platz. Klick zoomt hinein, Rechtsklick zurück.";
    public string Icon => "";
    public int Order => 220;

    public FrameworkElement CreateView() => new StorageMapView { DataContext = new StorageMapViewModel() };
}

public sealed partial class StorageMapViewModel : ObservableObject
{
    public ObservableCollection<RootOption> Roots { get; } = [];
    public ObservableCollection<MapNode> Breadcrumb { get; } = [];
    public ObservableCollection<MapNode> Children { get; } = [];

    [ObservableProperty] private MapNode? _current;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ScanCommand))] private bool _isBusy;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _hoverText = "Fahre über eine Kachel für Details.";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasMap;

    private CancellationTokenSource? _cts;

    public StorageMapViewModel()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && d.DriveType == DriveType.Fixed)
                    Roots.Add(new RootOption { Label = d.Name.TrimEnd('\\'), Path = d.Name, IsSelected = Roots.Count == 0 });
            }
            catch { /* nicht bereit */ }
        }
        Roots.Add(new RootOption { Label = "Benutzerordner", Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
    }

    partial void OnCurrentChanged(MapNode? value)
    {
        Breadcrumb.Clear();
        for (var n = value; n != null; n = n.Parent) Breadcrumb.Insert(0, n);
        Children.Clear();
        if (value == null) return;
        foreach (var c in value.Children.Take(40)) Children.Add(c);
        Summary = $"{Format.Bytes(value.Size)} · {value.Files:N0} Dateien";
    }

    [RelayCommand]
    private void BrowseRoot()
    {
        var dlg = new OpenFolderDialog { Title = "Ordner für die Speicher-Karte wählen" };
        if (dlg.ShowDialog() != true) return;
        foreach (var r in Roots) r.IsSelected = false;
        Roots.Add(new RootOption { Label = Path.GetFileName(dlg.FolderName.TrimEnd('\\')) is { Length: > 0 } n ? n : dlg.FolderName, Path = dlg.FolderName, IsSelected = true });
    }

    private bool CanScan() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        var root = Roots.FirstOrDefault(r => r.IsSelected)?.Path;
        if (root == null) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        var sw = Stopwatch.StartNew();
        var progress = new Progress<DiskMapService.ScanProgress>(p => ProgressText = $"{p.Files:N0} Dateien · {Format.Bytes(p.Bytes)} · {p.Current}");
        try
        {
            var tree = await Task.Run(() => DiskMapService.Scan(root, progress, _cts.Token));
            Current = tree;
            HasMap = true;
            HoverText = $"Fertig in {sw.Elapsed.TotalSeconds:0.0} s – fahre über eine Kachel für Details.";
        }
        catch (OperationCanceledException)
        {
            HoverText = "Analyse abgebrochen.";
        }
        finally
        {
            IsBusy = false;
            ProgressText = "";
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void ZoomTo(MapNode? node)
    {
        if (node is { IsDir: true }) Current = node;
    }

    [RelayCommand]
    private void Up()
    {
        if (Current?.Parent != null) Current = Current.Parent;
    }

    public void Hover(MapNode? node)
    {
        if (node == null) return;
        HoverText = $"{node.Path}  ·  {Format.Bytes(node.Size)}" + (node.IsDir ? $"  ·  {node.Files:N0} Dateien" : "") +
                    (node.Parent is { Size: > 0 } p ? $"  ·  {node.Size * 100.0 / p.Size:0.0} % von {p.Name}" : "");
    }

    [RelayCommand]
    private static void Reveal(MapNode? node)
    {
        if (node == null) return;
        if (node.IsDir || node.IsAggregate)
        {
            if (Directory.Exists(node.Path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{node.Path}\"") { UseShellExecute = true });
        }
        else if (File.Exists(node.Path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.Path}\"") { UseShellExecute = true });
        }
    }
}
