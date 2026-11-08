using System.Windows;
using System.Windows.Input;

namespace EZTool.Dialogs;

public partial class NeonDialog : Window
{
    private NeonDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    /// <summary>Zeigt einen gestylten Dialog. Gibt true zurück, wenn bestätigt wurde.</summary>
    public static bool Show(string title, string message, string confirmText = "OK", string? cancelText = "Abbrechen",
        bool danger = false, IEnumerable<string>? details = null)
    {
        var d = new NeonDialog();
        var owner = Application.Current?.MainWindow;
        if (owner is { IsLoaded: true } && owner != d) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        d.TitleText.Text = title;
        d.MessageText.Text = message;
        d.ConfirmButton.Content = confirmText;

        if (cancelText == null) d.CancelButton.Visibility = Visibility.Collapsed;
        else d.CancelButton.Content = cancelText;

        if (danger)
        {
            d.ConfirmButton.Style = (Style)d.FindResource("DangerButton");
            d.IconBorder.Background = (System.Windows.Media.Brush)d.FindResource("RedSoft");
            d.IconText.Foreground = (System.Windows.Media.Brush)d.FindResource("Red");
            d.IconText.Text = "";
        }

        var list = details?.ToList();
        if (list is { Count: > 0 })
        {
            d.DetailsList.ItemsSource = list;
            d.DetailsBorder.Visibility = Visibility.Visible;
        }

        return d.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
