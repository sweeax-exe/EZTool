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
    public string Title => Loc.T("StorageMap.Title");
    public string Subtitle => Loc.T("StorageMap.Subtitle");
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
    [ObservableProperty] private string _hoverText = Loc.T("StorageMap.HoverHint");
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
        Roots.Add(new RootOption { Label = Loc.T("StorageMap.UserFolder"), Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
    }

    partial void OnCurrentChanged(MapNode? value)
    {
        Breadcrumb.Clear();
        for (var n = value; n != null; n = n.Parent) Breadcrumb.Insert(0, n);
        Children.Clear();
        if (value == null) return;
        foreach (var c in value.Children.Take(40)) Children.Add(c);
        Summary = Loc.T("StorageMap.Summary", Format.Bytes(value.Size), value.Files);
    }

    [RelayCommand]
    private void BrowseRoot()
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("StorageMap.BrowseTitle") };
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
        var progress = new Progress<DiskMapService.ScanProgress>(p => ProgressText = Loc.T("StorageMap.Progress", p.Files, Format.Bytes(p.Bytes), p.Current));
        try
        {
            var tree = await Task.Run(() => DiskMapService.Scan(root, progress, _cts.Token));
            Current = tree;
            HasMap = true;
            HoverText = Loc.T("StorageMap.Done", sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            HoverText = Loc.T("StorageMap.Cancelled");
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
        HoverText = $"{node.Path}  ·  {Format.Bytes(node.Size)}" + (node.IsDir ? "  ·  " + Loc.T("StorageMap.Hover.Files", node.Files) : "") +
                    (node.Parent is { Size: > 0 } p ? "  ·  " + Loc.T("StorageMap.Hover.Share", node.Size * 100.0 / p.Size, p.Name) : "");
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
