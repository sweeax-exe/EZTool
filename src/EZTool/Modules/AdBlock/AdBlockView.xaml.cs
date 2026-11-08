using System.Windows.Controls;

namespace EZTool.Modules.AdBlock;

public partial class AdBlockView : UserControl
{
    public AdBlockView()
    {
        InitializeComponent();
        // Das Protokoll nur aktualisieren, solange die Seite zu sehen ist
        Loaded += (_, _) => (DataContext as AdBlockViewModel)?.Start();
        Unloaded += (_, _) => (DataContext as AdBlockViewModel)?.Stop();
    }
}
