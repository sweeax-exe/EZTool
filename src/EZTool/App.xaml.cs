using System.Windows;
using System.Windows.Threading;
using EZTool.Dialogs;
using EZTool.Services;

namespace EZTool;

public partial class App : Application
{
    /// <summary>
    /// Screenshot-Modus (für die README): "--screenshots=Ordner --pages=dashboard,hardware".
    /// EZTool fotografiert die Seiten selbst, maskiert persönliche Daten und beendet sich.
    /// </summary>
    public static string? ScreenshotDir { get; } = Arg("--screenshots=");
    public static bool ScreenshotMode => ScreenshotDir != null;

    // EZTOOL_DEV=1 → eigene Instanz für Tests, ohne eine laufende EZTool-Instanz zu stören
    private static readonly string InstanceName = "EZTool.SingleInstance.v1" +
        (ScreenshotMode ? ".shot" : string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EZTOOL_DEV")) ? "" : ".dev");

    public static string? Arg(string prefix) =>
        Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim('"');
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private TrayService? _tray;

    /// <summary>True, sobald der Nutzer wirklich beenden möchte (nicht nur Fenster schließen).</summary>
    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Nach einem Neustart (z. B. Sprachwechsel) erst warten, bis die alte Instanz beendet ist
        if (int.TryParse(Arg("--wait-pid="), out var oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(15000); } catch { /* schon beendet */ }
        }

        // Notausgang: "EZTool.exe --restore-dns" stellt die DNS-Server zurück, falls nach einem Absturz kein Internet geht
        if (e.Args.Contains("--restore-dns", StringComparer.OrdinalIgnoreCase))
        {
            Services.AdBlock.DnsSwitcher.Restore();
            SettingsService.Current.AdBlockEnabled = false;
            SettingsService.Save();
            Shutdown();
            return;
        }

        // Sprache muss vor dem ersten Fenster feststehen
        // "--lang=en" oder EZTOOL_LANG=en überschreibt die Einstellung – für Tests und Screenshots
        var forcedLang = Arg("--lang=") ?? Environment.GetEnvironmentVariable("EZTOOL_LANG");
        Core.Loc.Init(!string.IsNullOrEmpty(forcedLang) ? forcedLang : SettingsService.Current.Language);

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

        if (ScreenshotMode)
        {
            // Nur anzeigen und fotografieren – keine Hintergrund-Funktionen, nichts wird geschrieben
            SensorLogService.Instance.LoadOnly();
            if ((Arg("--pages=") ?? "").Contains("adblock", StringComparison.OrdinalIgnoreCase))
                _ = Services.AdBlock.AdBlockService.Instance.RunScreenshotDemoAsync();
            var shot = new MainWindow();
            MainWindow = shot;
            shot.Show();
            _ = shot.CaptureAsync(ScreenshotDir!, (Arg("--pages=") ?? "dashboard").Split(',', StringSplitOptions.RemoveEmptyEntries));
            return;
        }

        SensorLogService.Instance.Start();
        SensorLogService.Instance.FpsSource = () => FpsService.Instance.Current?.Fps;

        // Werbeblocker: Testinstanzen (EZTOOL_DEV) lassen die DNS-Einstellungen in Ruhe
        if (InstanceName.EndsWith(".v1"))
        {
            Services.AdBlock.AdBlockService.Instance.OnAppStart();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Services.AdBlock.AdBlockService.Instance.OnAppExit();
            AppDomain.CurrentDomain.UnhandledException += (_, _) => Services.AdBlock.AdBlockService.Instance.OnAppExit();
        }

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

    /// <summary>EZTool neu starten (z. B. nach einem Sprachwechsel). Läuft mit denselben Rechten weiter.</summary>
    public void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        var hidden = MainWindow is { IsVisible: false } ? " --tray" : "";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--wait-pid={Environment.ProcessId}{hidden}") { UseShellExecute = false });
        ExitApp();
    }

    public void ExitApp()
    {
        IsExiting = true;
        MainWindow?.Close();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Abmelden / Herunterfahren: DNS zurückstellen, solange noch Zeit ist
        Services.AdBlock.AdBlockService.Instance.OnAppExit();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.AdBlock.AdBlockService.Instance.OnAppExit();
        _tray?.Dispose();
        HardwareService.Instance.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        NeonDialog.Show(Core.Loc.T("Common.UnexpectedError"), e.Exception.Message, cancelText: null, danger: true);
    }
}
