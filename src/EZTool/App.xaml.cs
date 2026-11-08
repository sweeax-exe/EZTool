using System.Windows;
using System.Windows.Threading;
using EZTool.Dialogs;
using EZTool.Services;

namespace EZTool;

public partial class App : Application
{
    // EZTOOL_DEV=1 → eigene Instanz für Tests, ohne eine laufende EZTool-Instanz zu stören
    private static readonly string InstanceName = "EZTool.SingleInstance.v1" +
        (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EZTOOL_DEV")) ? "" : ".dev");
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private TrayService? _tray;

    /// <summary>True, sobald der Nutzer wirklich beenden möchte (nicht nur Fenster schließen).</summary>
    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Nur eine Instanz: ein zweiter Start holt das laufende Fenster nach vorn.
        _mutex = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(InstanceName + ".Show").Set(); } catch { /* egal */ }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(() => ShowMain(null));
        }) { IsBackground = true }.Start();

        DispatcherUnhandledException += OnUnhandled;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        _ = SystemInfo.LoadAsync();
        HardwareService.Instance.Start();
        SensorLogService.Instance.Start();
        SensorLogService.Instance.FpsSource = () => FpsService.Instance.Current?.Fps;

        var main = new MainWindow();
        MainWindow = main;
        _tray = new TrayService(ShowMain, ExitApp);
        _ = new AlertService();

        HotkeyService.Instance.Apply(SettingsService.Current.KeyBindings);
        UpdateService.Instance.StartAutoCheck();
        SettingsService.Changed += OverlayWindow.Sync;
        OverlayWindow.Sync(SettingsService.Current);

        if (e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase)) main.PrepareHidden();
        else main.Show();
    }

    /// <summary>Hauptfenster anzeigen (optional direkt ein Modul öffnen).</summary>
    public void ShowMain(string? module)
    {
        if (MainWindow is not EZTool.MainWindow w) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
        if (module != null) w.OpenModule(module);
    }

    public void ExitApp()
    {
        IsExiting = true;
        MainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        HardwareService.Instance.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        NeonDialog.Show("Unerwarteter Fehler", e.Exception.Message, "OK", cancelText: null, danger: true);
    }
}
