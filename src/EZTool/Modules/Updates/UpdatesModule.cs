using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Services;

namespace EZTool.Modules.Updates;

public sealed class UpdatesModule : IModule
{
    public string Id => "updates";
    public string Title => Loc.T("Updates.Title");
    public string Subtitle => Loc.T("Updates.Subtitle");
    public string Icon => "";
    public int Order => 320;

    public FrameworkElement CreateView() => new UpdatesView { DataContext = new UpdatesViewModel() };
}

public sealed partial class UpdatesViewModel : ObservableObject, IActivatable
{
    public ObservableCollection<UpgradeItem> Items { get; } = [];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(UpdateSelectedCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _wingetMissing;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private DateTime? _lastCheck;

    private bool _checked;

    public void OnActivated()
    {
        if (!_checked && !IsBusy) _ = CheckAsync();
    }

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckAsync()
    {
        _checked = true;
        IsBusy = IsChecking = true;
        Status = Loc.T("Updates.Searching");
        try
        {
            if (!await Task.Run(WingetService.IsAvailable))
            {
                WingetMissing = true;
                Status = "";
                return;
            }

            var (items, needsAgreement) = await Task.Run(() => WingetService.ListUpgrades(SettingsService.Current.WingetAgreed));
            if (needsAgreement)
            {
                if (!NeonDialog.Show(Loc.T("Updates.AgreementTitle"), Loc.T("Updates.AgreementText"), Loc.T("Updates.AgreementAccept")))
                {
                    Status = Loc.T("Updates.AgreementDeclined");
                    return;
                }
                SettingsService.Current.WingetAgreed = true;
                SettingsService.Save();
                (items, _) = await Task.Run(() => WingetService.ListUpgrades(true));
            }

            Items.Clear();
            foreach (var i in items)
            {
                i.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(UpgradeItem.IsChecked)) Recalc();
                };
                Items.Add(i);
            }
            LastCheck = DateTime.Now;
            Status = items.Count == 0 ? Loc.T("Updates.AllUpToDate") : Loc.T("Updates.Available", items.Count);
            Recalc();
            _ = LoadIconsAsync(items);
        }
        finally
        {
            IsBusy = IsChecking = false;
        }
    }

    /// <summary>Icons aus der Liste der installierten Programme übernehmen (gleicher Name).</summary>
    private static async Task LoadIconsAsync(List<UpgradeItem> items)
    {
        var icons = await Task.Run(() =>
        {
            var installed = ProgramService.LoadAll();
            var map = new Dictionary<UpgradeItem, System.Windows.Media.ImageSource>();
            foreach (var u in items)
            {
                var p = installed.FirstOrDefault(x => x.Name.Equals(u.Name, StringComparison.OrdinalIgnoreCase))
                        ?? installed.FirstOrDefault(x => x.Name.StartsWith(u.Name, StringComparison.OrdinalIgnoreCase));
                if (p != null && ProgramService.LoadIcon(p) is { } icon) map[u] = icon;
            }
            return map;
        });
        foreach (var (u, icon) in icons) u.Icon = icon;
    }

    private void Recalc()
    {
        SelectedCount = Items.Count(i => i.IsChecked && i.State == 0);
        HasItems = Items.Count > 0;
        UpdateSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in Items.Where(i => i.State == 0)) i.IsChecked = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var i in Items) i.IsChecked = false;
    }

    private bool CanUpdate() => !IsBusy && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateSelected() => RunUpdates(Items.Where(i => i.IsChecked && i.State == 0).ToList());

    [RelayCommand]
    private Task UpdateOne(UpgradeItem? item) => item == null || IsBusy ? Task.CompletedTask : RunUpdates([item]);

    private async Task RunUpdates(List<UpgradeItem> list)
    {
        if (list.Count == 0) return;
        if (!NeonDialog.Show(list.Count == 1 ? Loc.T("Updates.ConfirmOneTitle", list[0].Name) : Loc.T("Updates.ConfirmManyTitle", list.Count),
                Loc.T("Updates.ConfirmText"),
                Loc.T("Updates.Update"), details: list.Count > 1 ? list.Select(i => $"{i.Name}  ·  {i.Version} → {i.Available}") : null))
            return;

        IsBusy = true;
        int ok = 0, fail = 0, n = 0;
        foreach (var item in list)
        {
            n++;
            Status = Loc.T("Updates.Progress", n, list.Count, item.Name);
            item.IsWorking = true;
            item.Status = Loc.T("Updates.Installing");
            var (success, msg) = await WingetService.UpgradeAsync(item);
            item.IsWorking = false;
            item.State = success ? 1 : 2;
            item.Status = success ? Loc.T("Updates.UpdatedTo", item.Available) : msg;
            item.IsChecked = false;
            if (success) ok++; else fail++;
        }
        IsBusy = false;
        Status = fail == 0 ? Loc.T("Updates.Done", ok) : Loc.T("Updates.DonePartial", ok, fail);
        Recalc();
    }
}
