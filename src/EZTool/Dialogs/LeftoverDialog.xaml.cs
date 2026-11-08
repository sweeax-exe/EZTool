using System.Windows;
using System.Windows.Input;
using EZTool.Services;

namespace EZTool.Dialogs;

public partial class LeftoverDialog : Window
{
    private LeftoverDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    /// <summary>Zeigt die gefundenen Reste. Gibt die vom Nutzer ausgewählten zurück (leer = nichts tun).</summary>
    public static List<Leftover> Show(IReadOnlyList<Leftover> leftovers)
    {
        var d = new LeftoverDialog();
        if (Application.Current?.MainWindow is { IsLoaded: true } owner) d.Owner = owner;
        d.List.ItemsSource = leftovers;
        d.SubText.Text = Core.Loc.T("Leftover.Summary", leftovers.Count, Core.Format.Bytes(leftovers.Sum(l => l.Size)));
        return d.ShowDialog() == true ? leftovers.Where(l => l.IsChecked).ToList() : [];
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
