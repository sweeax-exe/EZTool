using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Services;

namespace EZTool.Modules.Disks;

public sealed class DisksModule : IModule
{
    public string Id => "disks";
    public string Title => Loc.T("Disks.Title");
    public string Subtitle => Loc.T("Disks.Subtitle");
    public string Icon => "";
    public int Order => 50;

    public FrameworkElement CreateView() => new DisksView { DataContext = new DisksViewModel() };
}

public sealed partial class DisksViewModel : ObservableObject, IActivatable
{
    public ObservableCollection<DiskHealth> Disks { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _limited;

    public void OnActivated()
    {
        if (Disks.Count == 0 && !IsLoading) _ = LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        var list = await Task.Run(DiskHealthService.Load);
        Disks.Clear();
        foreach (var d in list) Disks.Add(d);
        Limited = list.All(d => d.PowerOnHours == null);
        IsLoading = false;
    }
}
