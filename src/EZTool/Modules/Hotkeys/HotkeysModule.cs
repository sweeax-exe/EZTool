using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Services;
using Microsoft.Win32;

namespace EZTool.Modules.Hotkeys;

public sealed class HotkeysModule : IModule
{
    public string Id => "hotkeys";
    public string Title => Loc.T("Hotkeys.Title");
    public string Subtitle => Loc.T("Hotkeys.Subtitle");
    public string Icon => "";
    public int Order => 415;

    public FrameworkElement CreateView() => new HotkeysView { DataContext = new HotkeysViewModel() };
}

public sealed partial class BindingVm : ObservableObject
{
    public KeyBinding Model { get; }
    public string KeyText => HotkeyService.Describe(Model.Vk, Model.Ctrl, Model.Alt, Model.Shift, Model.Win);

    public string ActionText => Model.Action switch
    {
        "key" => Loc.T("Hotkeys.SendsKey", HotkeyService.Describe(Model.TargetVk, Model.TargetCtrl, Model.TargetAlt, Model.TargetShift, false)),
        "run" => Loc.T("Hotkeys.Starts", Path.GetFileName(Model.Path)),
        _ => HotkeyService.Actions.FirstOrDefault(a => a.Id == Model.Action).Label ?? Model.Action,
    };

    public string ActionIcon => HotkeyService.Actions.FirstOrDefault(a => a.Id == Model.Action).Icon ?? "";

    [ObservableProperty] private bool _enabled;

    public BindingVm(KeyBinding model)
    {
        Model = model;
        _enabled = model.Enabled;
    }

    partial void OnEnabledChanged(bool value) => Model.Enabled = value;
}

public sealed partial class ActionChoice : ObservableObject
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Icon { get; init; }
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class HotkeysViewModel : ObservableObject
{
    private readonly AppSettings _cfg = SettingsService.Current;

    public ObservableCollection<BindingVm> Bindings { get; } = [];
    public ObservableCollection<ActionChoice> MediaActions { get; } = [];
    public ObservableCollection<ActionChoice> OtherActions { get; } = [];

    // ── Editor ──
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isRecordingTarget;
    [ObservableProperty] private string _keyText = Loc.T("Hotkeys.NoKey");
    [ObservableProperty] private string _targetText = Loc.T("Hotkeys.NoKey");
    [ObservableProperty] private string _selectedAction = "next";
    [ObservableProperty] private string _programPath = "";
    [ObservableProperty] private string _conflict = "";

    public bool ShowTarget => SelectedAction == "key";
    public bool ShowProgram => SelectedAction == "run";

    private KeyBinding _draft = new();

    public HotkeysViewModel()
    {
        foreach (var (id, label, icon) in HotkeyService.Actions)
        {
            var choice = new ActionChoice { Id = id, Label = label, Icon = icon, IsSelected = id == SelectedAction };
            (id is "next" or "prev" or "playpause" or "stop" or "volup" or "voldown" or "mute" ? MediaActions : OtherActions).Add(choice);
        }
        foreach (var b in _cfg.KeyBindings) Add(b);
    }

    private void Add(KeyBinding b)
    {
        var vm = new BindingVm(b);
        vm.PropertyChanged += (_, _) => Save();
        Bindings.Add(vm);
    }

    private void Save()
    {
        _cfg.KeyBindings = Bindings.Select(b => b.Model).ToList();
        SettingsService.Save();
        HotkeyService.Instance.Apply(_cfg.KeyBindings);
    }

    [RelayCommand]
    private void SelectAction(string id)
    {
        SelectedAction = id;
        foreach (var a in MediaActions.Concat(OtherActions)) a.IsSelected = a.Id == id;
        OnPropertyChanged(nameof(ShowTarget));
        OnPropertyChanged(nameof(ShowProgram));
    }

    [RelayCommand]
    private void RecordKey()
    {
        IsRecording = true;
        KeyText = Loc.T("Hotkeys.PressKey");
        HotkeyService.Instance.Record((vk, ctrl, alt, shift, win) =>
        {
            IsRecording = false;
            if (vk == 0)
            {
                KeyText = _draft.Vk == 0 ? Loc.T("Hotkeys.NoKey") : HotkeyService.Describe(_draft.Vk, _draft.Ctrl, _draft.Alt, _draft.Shift, _draft.Win);
                return;
            }
            _draft.Vk = vk; _draft.Ctrl = ctrl; _draft.Alt = alt; _draft.Shift = shift; _draft.Win = win;
            KeyText = HotkeyService.Describe(vk, ctrl, alt, shift, win);
            var existing = Bindings.FirstOrDefault(b => b.Model.Vk == vk && b.Model.Ctrl == ctrl && b.Model.Alt == alt && b.Model.Shift == shift && b.Model.Win == win);
            Conflict = existing != null ? Loc.T("Hotkeys.Conflict", existing.ActionText) : "";
        });
    }

    [RelayCommand]
    private void RecordTarget()
    {
        IsRecordingTarget = true;
        TargetText = Loc.T("Hotkeys.PressTargetKey");
        HotkeyService.Instance.Record((vk, ctrl, alt, shift, _) =>
        {
            IsRecordingTarget = false;
            if (vk == 0) { TargetText = Loc.T("Hotkeys.NoKey"); return; }
            _draft.TargetVk = vk; _draft.TargetCtrl = ctrl; _draft.TargetAlt = alt; _draft.TargetShift = shift;
            TargetText = HotkeyService.Describe(vk, ctrl, alt, shift, false);
        });
    }

    [RelayCommand]
    private void BrowseProgram()
    {
        var dlg = new OpenFileDialog { Title = Loc.T("Hotkeys.BrowseTitle"), Filter = Loc.T("Hotkeys.BrowseFilterPrograms") + "|*.exe;*.lnk;*.bat;*.cmd|" + Loc.T("Hotkeys.BrowseFilterAll") + "|*.*" };
        if (dlg.ShowDialog() == true) ProgramPath = dlg.FileName;
    }

    [RelayCommand]
    private void AddBinding()
    {
        if (_draft.Vk == 0)
        {
            NeonDialog.Show(Loc.T("Hotkeys.NeedKeyTitle"), Loc.T("Hotkeys.NeedKeyText"), cancelText: null);
            return;
        }
        if (SelectedAction == "key" && _draft.TargetVk == 0)
        {
            NeonDialog.Show(Loc.T("Hotkeys.NeedTargetTitle"), Loc.T("Hotkeys.NeedTargetText"), cancelText: null);
            return;
        }
        if (SelectedAction == "run" && string.IsNullOrWhiteSpace(ProgramPath))
        {
            NeonDialog.Show(Loc.T("Hotkeys.NeedProgramTitle"), Loc.T("Hotkeys.NeedProgramText"), cancelText: null);
            return;
        }

        _draft.Action = SelectedAction;
        _draft.Path = ProgramPath;
        foreach (var dup in Bindings.Where(b => b.Model.Vk == _draft.Vk && b.Model.Ctrl == _draft.Ctrl && b.Model.Alt == _draft.Alt
                                                 && b.Model.Shift == _draft.Shift && b.Model.Win == _draft.Win).ToList())
            Bindings.Remove(dup);
        Add(_draft);
        Save();

        _draft = new KeyBinding();
        KeyText = TargetText = Loc.T("Hotkeys.NoKey");
        ProgramPath = Conflict = "";
    }

    [RelayCommand]
    private void Remove(BindingVm? b)
    {
        if (b == null) return;
        Bindings.Remove(b);
        Save();
    }

    [RelayCommand]
    private void Test(BindingVm? b)
    {
        if (b != null) HotkeyService.Execute(b.Model);
    }

    /// <summary>Ein-Klick-Vorlage: Bild ↑/↓ = Titel vor/zurück, Pause = Play/Pause.</summary>
    [RelayCommand]
    private void ApplyMediaPreset()
    {
        var preset = new[]
        {
            new KeyBinding { Vk = 0x21, Action = "next" },      // Bild ↑
            new KeyBinding { Vk = 0x22, Action = "prev" },      // Bild ↓
            new KeyBinding { Vk = 0x13, Action = "playpause" }, // Pause
        };
        foreach (var p in preset)
        {
            foreach (var dup in Bindings.Where(b => b.Model.Vk == p.Vk && !b.Model.Ctrl && !b.Model.Alt && !b.Model.Shift && !b.Model.Win).ToList())
                Bindings.Remove(dup);
            Add(p);
        }
        Save();
    }
}
