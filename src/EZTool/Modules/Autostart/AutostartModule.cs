using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Services;

namespace EZTool.Modules.Autostart;

public sealed class AutostartModule : IModule
{
    public string Id => "autostart";
    public string Title => "Autostart";
    public string Subtitle => "Bestimme, was beim Hochfahren mitstartet – ein Klick schaltet aus, ohne etwas zu löschen.";
    public string Icon => "";
    public int Order => 330;

    public FrameworkElement CreateView() => new AutostartView { DataContext = new AutostartViewModel() };
}

public sealed partial class AutostartViewModel : ObservableObject, IActivatable
{
    public ObservableCollection<AutostartEntry> Entries { get; } = [];
    public ICollectionView EntriesView { get; }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _filter = "all";
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private int _disabledCount;
    [ObservableProperty] private int _brokenCount;

    private bool _loaded;
    private bool _suppress;

    public AutostartViewModel()
    {
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = o => o is AutostartEntry e
                                  && (Filter == "all" || (Filter == "broken" ? e.FileMissing : e.Category == Filter))
                                  && (Search.Length == 0 || e.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                                                         || e.Publisher.Contains(Search, StringComparison.OrdinalIgnoreCase)
                                                         || e.Command.Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    public void OnActivated()
    {
        if (!_loaded) _ = LoadAsync();
    }

    partial void OnSearchChanged(string value) => EntriesView.Refresh();
    partial void OnFilterChanged(string value) => EntriesView.Refresh();

    [RelayCommand]
    private void SetFilter(string f) => Filter = f;

    [RelayCommand]
    private async Task LoadAsync()
    {
        _loaded = true;
        IsLoading = true;
        var list = await Task.Run(AutostartService.LoadAll);

        foreach (var e in Entries) e.PropertyChanged -= OnEntryChanged;
        Entries.Clear();
        foreach (var e in list)
        {
            e.PropertyChanged += OnEntryChanged;
            Entries.Add(e);
        }
        Recalc();
        IsLoading = false;

        _ = Task.Run(() =>
        {
            foreach (var e in list)
            {
                var icon = AutostartService.LoadIcon(e);
                if (icon != null) Application.Current?.Dispatcher.BeginInvoke(() => e.Icon = icon);
            }
        });
    }

    /// <summary>Der Schalter in der Liste ändert IsEnabled → sofort im System übernehmen.</summary>
    private async void OnEntryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_suppress || args.PropertyName != nameof(AutostartEntry.IsEnabled) || sender is not AutostartEntry e) return;
        var wanted = e.IsEnabled;
        try
        {
            await Task.Run(() => AutostartService.SetEnabled(e, wanted));
        }
        catch (Exception ex)
        {
            _suppress = true;
            e.IsEnabled = !wanted;
            _suppress = false;
            NeonDialog.Show("Nicht möglich", $"„{e.Name}“ konnte nicht {(wanted ? "aktiviert" : "deaktiviert")} werden.\n\n{ex.Message}", cancelText: null, danger: true);
        }
        Recalc();
    }

    private void Recalc()
    {
        EnabledCount = Entries.Count(e => e.IsEnabled);
        DisabledCount = Entries.Count(e => !e.IsEnabled);
        BrokenCount = Entries.Count(e => e.FileMissing);
    }

    [RelayCommand]
    private void Reveal(AutostartEntry? e)
    {
        if (e != null) AutostartService.Reveal(e);
    }

    [RelayCommand]
    private async Task DeleteAsync(AutostartEntry? e)
    {
        if (e == null) return;
        var msg = $"„{e.Name}“ wird dauerhaft aus dem Autostart entfernt. Das Programm selbst bleibt installiert.\n\n" +
                  "Tipp: Ausschalten genügt meistens – das lässt sich jederzeit rückgängig machen. " +
                  $"Eine Sicherung des Eintrags wird abgelegt in:\n{ProgramService.BackupFolder}";
        if (e.IsWindows) msg = "Achtung: Dieser Eintrag gehört zu Windows/Microsoft.\n\n" + msg;
        if (!NeonDialog.Show("Eintrag entfernen?", msg, "Entfernen", danger: true, details: [e.Command])) return;

        try
        {
            await Task.Run(() => AutostartService.Delete(e));
            e.PropertyChanged -= OnEntryChanged;
            Entries.Remove(e);
            Recalc();
        }
        catch (Exception ex)
        {
            NeonDialog.Show("Nicht möglich", $"„{e.Name}“ konnte nicht entfernt werden.\n\n{ex.Message}", cancelText: null, danger: true);
        }
    }
}
