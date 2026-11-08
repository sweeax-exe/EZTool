using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EZTool.Core;
using EZTool.Dialogs;
using EZTool.Services;

namespace EZTool;

public partial class MainWindow : Window
{
    private readonly IReadOnlyList<IModule> _modules;
    private readonly Dictionary<string, FrameworkElement> _views = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        _modules = ModuleRegistry.Discover();
        BuildNavigation();

        SourceInitialized += (_, _) => Native.StyleWindow(this);
        StateChanged += (_, _) => UpdateMaximizedState();
        Loaded += OnLoaded;
        HardwareService.Instance.Updated += OnHardware;
        UpdateService.Instance.Changed += info =>
        {
            UpdateBadge.Visibility = info != null ? Visibility.Visible : Visibility.Collapsed;
            if (info != null) UpdateBadgeText.Text = Loc.T("Shell.UpdateAvailable", info.Version);
            ShowUpdateBar(info);
        };

        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString(Loc.T("Shell.ClockFormat"), Loc.Culture);
        _clock.Start();
        StatusText.Text = IsAdmin ? Loc.T("Shell.Admin") : Loc.T("Shell.Limited");
        VersionText.Text = $"v{typeof(App).Assembly.GetName().Version?.ToString(3)}";
    }

    private static bool IsAdmin { get; } = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    // ───────────── Navigation ─────────────

    private void BuildNavigation()
    {
        string? group = null;
        foreach (var module in _modules)
        {
            // Gruppen-Überschrift, sobald eine neue Gruppe beginnt
            if (module.Group != group)
            {
                group = module.Group;
                NavPanel.Children.Add(new TextBlock
                {
                    Text = group,
                    Style = (Style)FindResource("Label"),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextMute"),
                    Margin = new Thickness(24, NavPanel.Children.Count == 0 ? 0 : 18, 0, 6),
                });
            }

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new TextBlock { Text = module.Icon, Style = (Style)FindResource("Icon"), FontSize = 15, Width = 20 };
            // Symbol übernimmt die Textfarbe des Eintrags – auf dem gelben Reiter also dunkel
            icon.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(RadioButton), 1)
            });
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = module.Title, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

            var button = new RadioButton { Style = (Style)FindResource("NavButton"), Content = content, GroupName = "nav", Tag = module };
            button.Checked += (_, _) => Navigate(module);
            NavPanel.Children.Add(button);
        }

        // Startseite: "EZTool.exe --page=cleaner" öffnet direkt ein bestimmtes Modul.
        var page = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))?[7..];
        if (!OpenModule(page ?? "")) OpenModule(_modules.FirstOrDefault()?.Id ?? "");
    }

    /// <summary>Wechselt zu einem Modul (per ID). Gibt false zurück, wenn es das Modul nicht gibt.</summary>
    public bool OpenModule(string id)
    {
        var button = NavPanel.Children.OfType<RadioButton>().FirstOrDefault(b => ((IModule)b.Tag).Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (button == null) return false;
        button.IsChecked = true;
        return true;
    }

    private void Navigate(IModule module)
    {
        if (!_views.TryGetValue(module.Id, out var view))
        {
            view = module.CreateView();
            _views[module.Id] = view;
        }

        PageTitle.Text = module.Title;
        PageSubtitle.Text = module.Subtitle;

        PageHost.Content = view;
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

        if (IsLoaded) (view.DataContext as IActivatable)?.OnActivated();
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ((PageHost.Content as FrameworkElement)?.DataContext as IActivatable)?.OnActivated();

        // Einmalig anbieten, PawnIO für CPU-Temperaturen zu installieren
        var cfg = SettingsService.Current;
        if (!App.ScreenshotMode && IsAdmin && !cfg.PawnIoPromptShown && (!PawnIoService.IsInstalled || PawnIoService.NeedsUpdate))
        {
            cfg.PawnIoPromptShown = true;
            SettingsService.Save();
            await Task.Delay(800);
            await PawnIoService.InstallWithDialogsAsync();
        }
    }

    /// <summary>
    /// Screenshot-Modus: öffnet die Seiten nacheinander, rendert das Fenster selbst als PNG (mit dunklem
    /// Hintergrund statt Desktop-Glas) und beendet EZTool danach.
    /// </summary>
    public async Task CaptureAsync(string dir, string[] pages)
    {
        WindowState = WindowState.Normal;
        Width = 1440;
        Height = 900;
        Directory.CreateDirectory(dir);

        // Während der Aufnahme keine Maus-/Tastatureingaben annehmen – sonst landet womöglich eine andere
        // (persönliche) Seite auf dem Bild.
        Root.IsHitTestVisible = false;
        PreviewKeyDown += (_, e) => e.Handled = true;
        PreviewMouseWheel += (_, e) => e.Handled = true;

        await Task.Delay(6000); // Sensoren, Systeminfos und Verlauf laden lassen

        foreach (var entry in pages)
        {
            // "network#apps" = Seite "network", Reiter "apps" (für Module mit einer Tab-Eigenschaft)
            var parts = entry.Split('#', 2);
            var page = parts[0];
            var module = _modules.FirstOrDefault(m => m.Id.Equals(page, StringComparison.OrdinalIgnoreCase));
            if (module == null || !OpenModule(page)) continue;
            if (parts.Length == 2 && _views[module.Id].DataContext?.GetType().GetProperty("Tab") is { CanWrite: true } tab)
                tab.SetValue(_views[module.Id].DataContext, parts[1]);
            await Task.Delay(page == "cleaner" ? 12000 : parts.Length == 2 && parts[1] is "devices" or "suspects" ? 20000 : page == "security" ? 9000 : 5000);

            // Sicherheitsnetz: nur fotografieren, wenn wirklich die gewünschte Seite angezeigt wird
            if (PageTitle.Text != module.Title || PageHost.Content != _views[module.Id])
            {
                OpenModule(page);
                await Task.Delay(2000);
                if (PageTitle.Text != module.Title) continue;
            }

            const double scale = 1.25;
            var size = new Size(Root.ActualWidth, Root.ActualHeight);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var bg = (Brush)FindResource("Chassis");
                dc.DrawRectangle(bg, null, new Rect(size));
                dc.DrawRectangle(new VisualBrush(Root), null, new Rect(size));
            }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            await using var fs = File.Create(Path.Combine(dir, entry.Replace('#', '-') + ".png"));
            png.Save(fs);
        }
        ((App)Application.Current).ExitApp();
    }

    /// <summary>Für den Start im Infobereich (--tray): Fenster vorbereiten, aber nicht zeigen.</summary>
    public void PrepareHidden()
    {
        _trayHintShown = true; // beim Autostart keinen "läuft weiter"-Hinweis zeigen
    }

    private void OnHardware(HardwareSnapshot s)
    {
        if (!IsVisible) return;
        MiniCpu.Text = (s.CpuTemp is { } t ? $"{t:0} °C   " : "") + $"{s.CpuLoad:0} %";
        MiniCpuBar.Value = s.CpuLoad;
        MiniRam.Text = $"{Format.Bytes(s.RamUsed)} / {Format.Bytes(s.RamTotal)}";
        MiniRamBar.Value = s.RamLoad;
    }

    // ───────────── Fenster ─────────────

    protected override void OnClosing(CancelEventArgs e)
    {
        // "Schließen" minimiert in den Infobereich, solange der Nutzer nicht wirklich beendet.
        if (!App.IsExiting && SettingsService.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                TrayService.Instance?.Notify(Loc.T("Shell.TrayHintTitle"), Loc.T("Shell.TrayHintText"));
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!App.IsExiting) ((App)Application.Current).ExitApp();
    }

    private void UpdateMaximizedState()
    {
        // Rahmenlose Fenster ragen maximiert über den Bildschirm hinaus – ausgleichen.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateBadge_Click(object sender, RoutedEventArgs e) => OpenModule("settings");

    // ───────────── Update-Leiste ─────────────

    private Version? _dismissedUpdate;
    private bool _installing;

    /// <summary>Gelbe Leiste über jeder Seite, solange eine neuere Version bereitsteht ("Später" blendet sie für diese Version aus).</summary>
    private void ShowUpdateBar(UpdateInfo? info)
    {
        if (_installing) return;
        bool show = info != null && info.Version != _dismissedUpdate && !App.ScreenshotMode;
        UpdateBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (info == null) return;
        UpdateBarTitle.Text = Loc.T("AppUpdate.NotifyTitle", info.Version);
        UpdateBarText.Text = Loc.T("AppUpdate.BannerText", UpdateService.Current);
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        _dismissedUpdate = UpdateService.Instance.Available?.Version;
        UpdateBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Instance.Available is not { } info) return;
        // Release-Notes (Markdown) als Stichpunkte zeigen
        var lines = info.Notes.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string Clean(string s) => s.Replace("**", "").Replace("`", "").TrimStart('#', '-', '*', ' ');
        var intro = lines.FirstOrDefault(l => !l.StartsWith('-') && !l.StartsWith("**Download", StringComparison.Ordinal)) is { } first ? Clean(first) : "";
        var bullets = lines.Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(Clean).ToList();
        if (NeonDialog.Show(Loc.T("AppUpdate.NotesTitle", info.Version), intro, Loc.T("Settings.UpdateNow"), Loc.T("AppUpdate.Later"),
                details: bullets.Count > 0 ? bullets : null))
            UpdateInstall_Click(sender, e);
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_installing || UpdateService.Instance.Available is not { } info) return;
        _installing = true;
        UpdateBarInstall.IsEnabled = UpdateBarNotes.IsEnabled = false;
        var progress = new Progress<string>(m => UpdateBarText.Text = m);
        var (ok, msg) = await UpdateService.Instance.InstallAsync(info, progress);
        if (ok) return; // EZTool startet gleich neu
        _installing = false;
        UpdateBarInstall.IsEnabled = UpdateBarNotes.IsEnabled = true;
        ShowUpdateBar(info);
        NeonDialog.Show(Loc.T("Settings.UpdateFailedTitle"), msg, cancelText: null, danger: true);
    }
}
