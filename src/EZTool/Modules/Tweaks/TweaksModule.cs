using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Services;

namespace EZTool.Modules.Tweaks;

public sealed class TweaksModule : IModule
{
    public string Id => "tweaks";
    public string Title => "Windows-Tweaks";
    public string Subtitle => "Werbung raus, Datenschutz rein, Gaming-Optimierungen – alles per Schalter und jederzeit rückgängig.";
    public string Icon => "";
    public int Order => 410;

    public FrameworkElement CreateView() => new TweaksView { DataContext = new TweaksViewModel() };
}

public sealed partial class TweakVm : ObservableObject
{
    public Tweak Tweak { get; }
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public string Category => Tweak.Category;
    public string Icon => Tweak.Icon;
    public string NeedsText => Tweak.Needs switch { "explorer" => "Explorer-Neustart", "reboot" => "Neustart nötig", _ => "" };
    public bool IsLocked { get; }

    [ObservableProperty] private bool _isOn;

    public TweakVm(Tweak t, bool isAdmin)
    {
        Tweak = t;
        IsLocked = t.RequiresAdmin && !isAdmin;
    }
}

public sealed partial class TweaksViewModel : ObservableObject, IActivatable
{
    public ObservableCollection<TweakVm> Items { get; } = [];
    public ICollectionView ItemsView { get; }

    [ObservableProperty] private bool _needsExplorerRestart;
    [ObservableProperty] private bool _needsReboot;
    [ObservableProperty] private int _activeCount;

    private bool _loading;
    private static readonly bool IsAdmin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public TweaksViewModel()
    {
        foreach (var t in TweakService.All)
        {
            var vm = new TweakVm(t, IsAdmin);
            vm.PropertyChanged += OnTweakChanged;
            Items.Add(vm);
        }
        var cvs = new CollectionViewSource { Source = Items };
        cvs.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TweakVm.Category)));
        ItemsView = cvs.View;
    }

    public void OnActivated()
    {
        _loading = true;
        foreach (var i in Items) i.IsOn = TweakService.IsApplied(i.Tweak);
        _loading = false;
        ActiveCount = Items.Count(i => i.IsOn);
    }

    private void OnTweakChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading || e.PropertyName != nameof(TweakVm.IsOn) || sender is not TweakVm vm) return;
        try
        {
            TweakService.Set(vm.Tweak, vm.IsOn);
            if (vm.Tweak.Needs == "explorer") NeedsExplorerRestart = true;
            if (vm.Tweak.Needs == "reboot") NeedsReboot = true;
        }
        catch (Exception ex)
        {
            _loading = true;
            vm.IsOn = !vm.IsOn;
            _loading = false;
            NeonDialog.Show("Nicht möglich", $"„{vm.Name}“ konnte nicht geändert werden.\n\n{ex.Message}", cancelText: null, danger: true);
        }
        ActiveCount = Items.Count(i => i.IsOn);
    }

    [RelayCommand]
    private void RestartExplorer()
    {
        TweakService.RestartExplorer();
        NeedsExplorerRestart = false;
    }

    [RelayCommand]
    private async Task CreateRestorePointAsync()
    {
        var (ok, msg) = await Task.Run(TweakService.CreateRestorePoint);
        NeonDialog.Show(ok ? "Gesichert" : "Nicht möglich", msg, cancelText: null, danger: !ok);
    }

    [RelayCommand]
    private void ResetAll()
    {
        if (!NeonDialog.Show("Alle Tweaks zurücksetzen?", "Alle hier aufgeführten Einstellungen werden auf den Windows-Standard zurückgesetzt.", "Zurücksetzen"))
            return;
        foreach (var i in Items.Where(i => i.IsOn && !i.IsLocked)) i.IsOn = false;
    }
}
