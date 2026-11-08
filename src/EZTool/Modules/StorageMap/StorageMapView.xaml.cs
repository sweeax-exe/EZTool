using System.Windows.Controls;

namespace EZTool.Modules.StorageMap;

public partial class StorageMapView : UserControl
{
    public StorageMapView()
    {
        InitializeComponent();
        Map.NodeClicked += n => (DataContext as StorageMapViewModel)?.ZoomToCommand.Execute(n);
        Map.BackRequested += () => (DataContext as StorageMapViewModel)?.UpCommand.Execute(null);
        Map.HoverChanged += n => (DataContext as StorageMapViewModel)?.Hover(n);
    }
}
