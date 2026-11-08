using System.Windows.Controls;
using EZTool.Services;

namespace EZTool.Modules.Hotkeys;

public partial class HotkeysView : UserControl
{
    public HotkeysView()
    {
        InitializeComponent();
        // Eine laufende Tasten-Aufnahme beim Verlassen der Seite abbrechen
        IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) HotkeyService.Instance.CancelRecord();
        };
    }
}
