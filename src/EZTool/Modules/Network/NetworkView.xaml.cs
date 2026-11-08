using System.Windows.Controls;

namespace EZTool.Modules.Network;

public partial class NetworkView : UserControl
{
    public NetworkView()
    {
        InitializeComponent();
        // Live-Messung nur, solange die Seite zu sehen ist
        Loaded += (_, _) => (DataContext as NetworkViewModel)?.Start();
        Unloaded += (_, _) => (DataContext as NetworkViewModel)?.Stop();
    }
}
