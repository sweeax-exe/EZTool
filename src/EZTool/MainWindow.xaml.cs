using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EZTool.Core;
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

        SourceInitialized += (_, _) => EnableGlass();
        StateChanged += (_, _) => UpdateMaximizedState();
        Loaded += OnLoaded;
        HardwareService.Instance.Updated += OnHardware;
        UpdateService.Instance.Changed += info =>
        {
            UpdateBadge.Visibility = info != null ? Visibility.Visible : Visibility.Collapsed;
            if (info != null) UpdateBadgeText.Text = $"Update {info.Version} verfügbar";
        };

        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("dddd, dd. MMM · HH:mm:ss", new System.Globalization.CultureInfo("de-DE"));
        _clock.Start();
        StatusText.Text = $"{(IsAdmin ? "Administrator" : "Eingeschränkt")} · v{typeof(App).Assembly.GetName().Version?.ToString(3)}";
        StartAurora();
    }

    private static bool IsAdmin { get; } = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    // ───────────── Glas & Animation ─────────────

    private void EnableGlass()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (Native.EnableAcrylic(hwnd))
        {
            // Der WPF-Hintergrund muss durchsichtig sein, damit das Acrylic-Glas durchscheint.
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } ct }) ct.BackgroundColor = Colors.Transparent;
        }
        else
        {
            // Ältere Windows-Versionen: deckender, dunkler Hintergrund
            Tint.Fill = (Brush)FindResource("Bg0");
        }
    }

    private void StartAurora()
    {
        void Drift(TranslateTransform t, double dx, double dy, double seconds)
        {
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var ax = new DoubleAnimation(0, dx, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            var ay = new DoubleAnimation(0, dy, TimeSpan.FromSeconds(seconds * 1.3)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            // Sehr langsame Bewegung – 20 fps reichen und sparen GPU/CPU.
            Timeline.SetDesiredFrameRate(ax, 20);
            Timeline.SetDesiredFrameRate(ay, 20);
            t.BeginAnimation(TranslateTransform.XProperty, ax);
            t.BeginAnimation(TranslateTransform.YProperty, ay);
        }

        Drift(Orb1, 260, 160, 26);
        Drift(Orb2, -320, -180, 32);
        Drift(Orb3, -200, 220, 38);
    }

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
                    Text = group.ToUpperInvariant(),
                    Style = (Style)FindResource("Label"),
                    FontSize = 10,
                    Foreground = (Brush)FindResource("TextMute"),
                    Margin = new Thickness(26, NavPanel.Children.Count == 0 ? 4 : 14, 0, 4),
                });
            }

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = module.Icon,
                Style = (Style)FindResource("Icon"),
                FontSize = 16,
                Width = 22,
                Foreground = (Brush)FindResource("Cyan")
            });
            content.Children.Add(new TextBlock { Text = module.Title, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

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

        PageEyebrow.Text = $"MODUL {_modules.ToList().IndexOf(module) + 1:00} / {_modules.Count:00}";
        PageTitle.Text = module.Title;
        PageSubtitle.Text = module.Subtitle;

        PageHost.Content = view;
        var tt = new TranslateTransform(0, 14);
        view.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });

        if (IsLoaded) (view.DataContext as IActivatable)?.OnActivated();
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ((PageHost.Content as FrameworkElement)?.DataContext as IActivatable)?.OnActivated();

        // Einmalig anbieten, PawnIO für CPU-Temperaturen zu installieren
        var cfg = SettingsService.Current;
        if (IsAdmin && !cfg.PawnIoPromptShown && (!PawnIoService.IsInstalled || PawnIoService.NeedsUpdate))
        {
            cfg.PawnIoPromptShown = true;
            SettingsService.Save();
            await Task.Delay(800);
            await PawnIoService.InstallWithDialogsAsync();
        }
    }

    /// <summary>Für den Start im Infobereich (--tray): Fenster vorbereiten, aber nicht zeigen.</summary>
    public void PrepareHidden()
    {
        _trayHintShown = true; // beim Autostart keinen "läuft weiter"-Hinweis zeigen
    }

    private void OnHardware(HardwareSnapshot s)
    {
        if (!IsVisible) return;
        MiniCpu.Text = $"{s.CpuLoad:0}%" + (s.CpuTemp is { } t ? $" · {t:0}°C" : "");
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
                TrayService.Instance?.Notify("EZTool läuft im Hintergrund weiter",
                    "Überwachung und Alarme bleiben aktiv. Rechtsklick auf das Symbol zum Beenden.");
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
}
