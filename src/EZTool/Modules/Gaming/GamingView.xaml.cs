using System.Windows.Controls;

namespace EZTool.Modules.Gaming;

public partial class GamingView : UserControl
{
    public GamingView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => (DataContext as GamingViewModel)?.SetActive((bool)e.NewValue);
    }
}
