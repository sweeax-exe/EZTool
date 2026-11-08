using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EZTool.Services;

namespace EZTool.Dialogs;

/// <summary>
/// Kleines, immer sichtbares Overlay mit den wichtigsten Werten. Klicks gehen durch das Fenster hindurch,
/// es stört also beim Spielen oder Arbeiten nicht. (In exklusivem Vollbild zeichnet Windows keine Fenster darüber.)
/// </summary>
public partial class OverlayWindow : Window
{
    private static OverlayWindow? _instance;
    private readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(5) };

    private OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => MakeClickThrough();
        Loaded += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
        HardwareService.Instance.Updated += Apply;
        if (HardwareService.Instance.Latest is { } s) Apply(s);

        // FPS des Spiels im Vordergrund (benötigt Admin-Rechte)
        if (SettingsService.Current.OverlayShowFps)
        {
            FpsService.Instance.Acquire();
            FpsService.Instance.Updated += ApplyFps;
            _fpsAcquired = true;
        }

        // Andere Topmost-Fenster können uns überdecken – regelmäßig wieder nach vorn holen
        _topmost.Tick += (_, _) =>
        {
            Topmost = false;
            Topmost = true;
        };
        _topmost.Start();
    }

    /// <summary>Zeigt/versteckt das Overlay entsprechend der Einstellungen.</summary>
    public static void Sync(AppSettings cfg)
    {
        if (cfg.OverlayEnabled)
        {
            _instance ??= new OverlayWindow();
            _instance.Opacity = Math.Clamp(cfg.OverlayOpacity, 0.3, 1);
            _instance.Show();
            _instance.Reposition();
        }
        else if (_instance != null)
        {
            _instance.Close();
        }
    }

    private bool _fpsAcquired;

    private void ApplyFps(FpsReading? r)
    {
        var vis = r != null ? Visibility.Visible : Visibility.Collapsed;
        FpsLabel.Visibility = FpsValue.Visibility = FrameTime.Visibility = vis;
        if (r == null) return;
        FpsValue.Text = $"{r.Fps:0}";
        FrameTime.Text = $"{r.FrameTimeMs:0.0} ms";
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_fpsAcquired)
        {
            FpsService.Instance.Updated -= ApplyFps;
            FpsService.Instance.Release();
        }
        HardwareService.Instance.Updated -= Apply;
        _topmost.Stop();
        _instance = null;
        base.OnClosed(e);
    }

    private void Apply(HardwareSnapshot s)
    {
        CpuLoad.Text = $"{s.CpuLoad:0}%";
        CpuTemp.Text = s.CpuTemp is { } ct ? $"{ct:0}°C" : "";
        CpuTemp.Foreground = TempBrush(s.CpuTemp);
        GpuLoad.Text = s.GpuLoad is { } gl ? $"{gl:0}%" : "—";
        GpuTemp.Text = s.GpuTemp is { } gt ? $"{gt:0}°C" : "";
        GpuTemp.Foreground = TempBrush(s.GpuTemp);
        RamLoad.Text = $"{s.RamLoad:0}%";
        RamUsed.Text = Core.Format.Bytes(s.RamUsed);
    }

    private static Brush TempBrush(double? t) => t switch
    {
        >= 85 => new SolidColorBrush(Color.FromRgb(0xF0, 0x44, 0x3A)),
        >= 70 => new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x1C)),
        _ => new SolidColorBrush(Color.FromRgb(0xA3, 0xA9, 0xAF)),
    };

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        const double m = 12;
        var corner = SettingsService.Current.OverlayCorner;
        Left = corner.EndsWith("Left") ? area.Left + m : area.Right - ActualWidth - m;
        Top = corner.StartsWith("Top") ? area.Top + m : area.Bottom - ActualHeight - m;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    private void MakeClickThrough()
    {
        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE);
    }
}
