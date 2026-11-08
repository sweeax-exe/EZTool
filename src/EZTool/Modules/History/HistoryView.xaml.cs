using System.Windows.Controls;

namespace EZTool.Modules.History;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => (DataContext as HistoryViewModel)?.SetActive((bool)e.NewValue);
    }
}
