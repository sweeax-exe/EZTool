using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EZTool.Modules.Programs;

public partial class ProgramsView : UserControl
{
    public ProgramsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged o) o.PropertyChanged -= OnVmChanged;
            if (e.NewValue is INotifyPropertyChanged n) n.PropertyChanged += OnVmChanged;
        };
    }

    // Nach dem Umsortieren oben anfangen – sonst bleibt die alte Scrollposition stehen
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProgramsViewModel.Sort) or nameof(ProgramsViewModel.SortDescending))
            Dispatcher.BeginInvoke(() => FindScroller(List)?.ScrollToTop());
    }

    private static ScrollViewer? FindScroller(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScroller(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }
}
