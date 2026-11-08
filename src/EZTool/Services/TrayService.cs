using System.Windows;
using EZTool.Core;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace EZTool.Services;

/// <summary>Symbol im Infobereich: Live-Werte im Tooltip, Schnellmenü und Windows-Benachrichtigungen.</summary>
public sealed class TrayService : IDisposable
{
    public static TrayService? Instance { get; private set; }

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _overlayItem;
    private readonly Forms.ToolStripMenuItem _gamingItem;
    private readonly Action<string?> _show;
    private readonly Action _exit;

    /// <param name="show">Öffnet das Hauptfenster (optional direkt ein Modul).</param>
    /// <param name="exit">Beendet EZTool wirklich.</param>
    public TrayService(Action<string?> show, Action exit)
    {
        _show = show;
        _exit = exit;

        var menu = new Forms.ContextMenuStrip { Renderer = new Forms.ToolStripProfessionalRenderer(new DarkColors()), ShowImageMargin = false };
        menu.Font = new Drawing.Font("Segoe UI", 9.5f);
        menu.Items.Add(Item(Loc.T("Tray.Open"), () => _show(null), bold: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item(Loc.T("Dashboard.Title"), () => _show("dashboard")));
        menu.Items.Add(Item(Loc.T("Cleaner.Title"), () => _show("cleaner")));
        menu.Items.Add(Item(Loc.T("Processes.Title"), () => _show("processes")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _gamingItem = Item(Loc.T("Tray.GamingStart"), async () =>
        {
            var g = GamingService.Instance;
            if (g.IsActive)
            {
                var report = await g.DeactivateAsync();
                if (report != null) Notify(Loc.T("Tray.SessionReady"), Loc.T("Tray.SessionText", report.Game, report.FpsText, report.LowText), openModule: "gaming");
            }
            else await g.ActivateAsync();
        });
        menu.Items.Add(_gamingItem);
        _overlayItem = Item(Loc.T("Tray.Overlay"), () =>
        {
            SettingsService.Current.OverlayEnabled = !SettingsService.Current.OverlayEnabled;
            SettingsService.Save();
        });
        menu.Items.Add(_overlayItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item(Loc.T("Tray.Exit"), () => _exit()));
        menu.Opening += (_, _) =>
        {
            _overlayItem.Checked = SettingsService.Current.OverlayEnabled;
            _gamingItem.Text = GamingService.Instance.IsActive ? Loc.T("Tray.GamingStop") : Loc.T("Tray.GamingStart");
            _gamingItem.Checked = GamingService.Instance.IsActive;
        };

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "EZTool",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _show(null);
        };
        _icon.BalloonTipClicked += (_, _) => _show(_balloonTarget);

        HardwareService.Instance.Updated += OnHardware;
        Instance = this;
    }

    private static Forms.ToolStripMenuItem Item(string text, Action click, bool bold = false)
    {
        var item = new Forms.ToolStripMenuItem(text) { ForeColor = Drawing.Color.FromArgb(232, 242, 255), Padding = new Forms.Padding(4, 3, 4, 3) };
        if (bold) item.Font = new Drawing.Font("Segoe UI", 9.5f, Drawing.FontStyle.Bold);
        item.Click += (_, _) => click();
        return item;
    }

    private static Drawing.Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = info!.Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private void OnHardware(HardwareSnapshot s)
    {
        string T(double? t) => t is { } v ? $" · {v:0}°C" : "";
        var text = $"EZTool\nCPU {s.CpuLoad:0}%{T(s.CpuTemp)}\n" +
                   (s.GpuLoad is { } g ? $"GPU {g:0}%{T(s.GpuTemp)}\n" : "") +
                   $"RAM {s.RamLoad:0}%";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private string? _balloonTarget;

    /// <summary>Zeigt eine Windows-Benachrichtigung. Ein Klick darauf öffnet das angegebene Modul.</summary>
    public void Notify(string title, string text, bool warning = false, string? openModule = null)
    {
        _balloonTarget = openModule;
        _icon.ShowBalloonTip(8000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        HardwareService.Instance.Updated -= OnHardware;
        _icon.Visible = false;
        _icon.Dispose();
        Instance = null;
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Bg = Drawing.Color.FromArgb(13, 20, 36);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(18, 60, 80);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(26, 37, 64);

        public override Drawing.Color ToolStripDropDownBackground => Bg;
        public override Drawing.Color MenuBorder => Border;
        public override Drawing.Color MenuItemBorder => Drawing.Color.FromArgb(0, 229, 255);
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => Border;
        public override Drawing.Color SeparatorLight => Border;
        public override Drawing.Color ImageMarginGradientBegin => Bg;
        public override Drawing.Color ImageMarginGradientMiddle => Bg;
        public override Drawing.Color ImageMarginGradientEnd => Bg;
        public override Drawing.Color CheckBackground => Hover;
        public override Drawing.Color CheckSelectedBackground => Hover;
    }
}
