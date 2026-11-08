using System.Windows;
using System.Windows.Controls;

namespace EZTool.Modules.Processes;

public partial class ProcessesView : UserControl
{
    public ProcessesView()
    {
        InitializeComponent();
        // Prozessliste nur aktualisieren, solange die Seite sichtbar ist
        IsVisibleChanged += (_, e) => (DataContext as ProcessesViewModel)?.SetActive((bool)e.NewValue);
    }
}
