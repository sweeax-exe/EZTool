using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EZTool.Services;

namespace EZTool.Controls;

/// <summary>
/// Squarified Treemap: Jede Kachel ist so groß wie ihr Anteil am Speicher. Zwei Ebenen werden gezeigt,
/// Linksklick zoomt in einen Ordner, Rechtsklick wieder hinaus.
/// </summary>
public sealed class TreemapView : FrameworkElement
{
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(MapNode), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public MapNode? Root { get => (MapNode?)GetValue(RootProperty); set => SetValue(RootProperty, value); }

    /// <summary>Ordner angeklickt (zum Hineinzoomen).</summary>
    public event Action<MapNode>? NodeClicked;

    /// <summary>Rechtsklick (eine Ebene zurück).</summary>
    public event Action? BackRequested;

    /// <summary>Kachel unter der Maus (null = keine).</summary>
    public event Action<MapNode?>? HoverChanged;

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xFF, 0xC2, 0x0E), Color.FromRgb(0x4D, 0xA8, 0xE8), Color.FromRgb(0x6C, 0xCB, 0x6A), Color.FromRgb(0xFF, 0x9F, 0x1C),
        Color.FromRgb(0xE4, 0x6A, 0xA8), Color.FromRgb(0x46, 0xC2, 0xB4), Color.FromRgb(0x9C, 0x8F, 0xE0), Color.FromRgb(0xC9, 0xB4, 0x8A),
    ];

    private static readonly Typeface Face = new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface FaceLight = new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private readonly List<(Rect Rect, MapNode Node, int Depth)> _hits = [];
    private MapNode? _hover;

    public TreemapView()
    {
        ClipToBounds = true;
        MouseMove += (_, e) =>
        {
            var n = HitTest(e.GetPosition(this));
            if (n == _hover) return;
            _hover = n;
            HoverChanged?.Invoke(n);
            InvalidateVisual();
        };
        MouseLeave += (_, _) =>
        {
            _hover = null;
            HoverChanged?.Invoke(null);
            InvalidateVisual();
        };
        MouseLeftButtonUp += (_, e) =>
        {
            // Zoomen in den äußeren Ordner der Kachel (Ebene 1), damit man Schritt für Schritt hineingeht
            var hit = _hits.Where(h => h.Rect.Contains(e.GetPosition(this)) && h.Node.IsDir && h.Node.Children.Count > 0).OrderBy(h => h.Depth).FirstOrDefault();
            if (hit.Node != null) NodeClicked?.Invoke(hit.Node);
        };
        MouseRightButtonUp += (_, _) => BackRequested?.Invoke();
        Cursor = Cursors.Hand;
    }

    private MapNode? HitTest(Point p) =>
        _hits.Where(h => h.Rect.Contains(p)).OrderByDescending(h => h.Depth).Select(h => h.Node).FirstOrDefault();

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (Root is not { Size: > 0 } root || bounds.Width < 20 || bounds.Height < 20) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var top = Layout(root.Children.Where(c => c.Size > 0).ToList(), bounds);
        for (int i = 0; i < top.Count; i++)
        {
            var (r, node) = top[i];
            var color = node.IsDir ? Palette[i % Palette.Length] : FileColor(node);
            DrawTile(dc, r, node, color, 1, dpi);

            // Zweite Ebene im Inneren großer Ordner-Kacheln
            if (!node.IsDir || node.Children.Count == 0 || r.Width < 90 || r.Height < 60) continue;
            var inner = new Rect(r.X + 4, r.Y + 22, Math.Max(0, r.Width - 8), Math.Max(0, r.Height - 26));
            foreach (var (cr, child) in Layout(node.Children.Where(c => c.Size > 0).ToList(), inner))
            {
                var cc = child.IsDir ? Shade(color, 0.75) : FileColor(child);
                DrawTile(dc, cr, child, cc, 2, dpi);
            }
        }
    }

    private void DrawTile(DrawingContext dc, Rect r, MapNode node, Color c, int depth, double dpi)
    {
        if (r.Width < 1.5 || r.Height < 1.5) return;
        _hits.Add((r, node, depth));
        var tile = new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2));
        bool hover = ReferenceEquals(node, _hover);

        byte a = (byte)(hover ? 150 : depth == 1 ? 60 : 85);
        var fill = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        var pen = new Pen(new SolidColorBrush(hover ? Colors.White : Color.FromArgb(150, c.R, c.G, c.B)), hover ? 1.6 : 1);
        double radius = Math.Min(2, Math.Min(tile.Width, tile.Height) / 4);
        dc.DrawRoundedRectangle(fill, pen, tile, radius, radius);

        // Beschriftung, wenn Platz ist
        if (tile.Width < 46 || tile.Height < 18) return;
        var name = new FormattedText(node.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, depth == 1 ? 12.5 : 11, Brushes.White, dpi)
        {
            MaxTextWidth = Math.Max(1, tile.Width - 12),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(name, new Point(tile.X + 6, tile.Y + 4));
        if (tile.Height >= 36 && (depth == 2 || !node.IsDir || node.Children.Count == 0 || tile.Height < 60))
        {
            var size = new FormattedText(Core.Format.Bytes(node.Size), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, FaceLight, 10.5,
                new SolidColorBrush(Color.FromArgb(210, 236, 238, 239)), dpi) { MaxTextWidth = Math.Max(1, tile.Width - 12), MaxLineCount = 1 };
            dc.DrawText(size, new Point(tile.X + 6, tile.Y + 4 + name.Height));
        }
        else if (depth == 1 && node.IsDir)
        {
            var size = new FormattedText(Core.Format.Bytes(node.Size), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, FaceLight, 10.5,
                new SolidColorBrush(Color.FromArgb(210, 236, 238, 239)), dpi);
            if (name.Width + size.Width + 24 < tile.Width) dc.DrawText(size, new Point(tile.Right - size.Width - 6, tile.Y + 5));
        }
    }

    private static Color Shade(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    private static Color FileColor(MapNode n) => n.IsAggregate ? Color.FromRgb(0x5C, 0x63, 0x6A) : n.Extension switch
    {
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" => Color.FromRgb(0xE4, 0x6A, 0xA8),
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".psd" or ".heic" => Color.FromRgb(0x6C, 0xCB, 0x6A),
        ".mp3" or ".wav" or ".flac" or ".m4a" => Color.FromRgb(0xFF, 0x9F, 0x1C),
        ".zip" or ".rar" or ".7z" or ".iso" or ".img" or ".vhdx" => Color.FromRgb(0x9C, 0x8F, 0xE0),
        ".exe" or ".dll" or ".msi" or ".sys" => Color.FromRgb(0x4D, 0xA8, 0xE8),
        _ => Color.FromRgb(0x83, 0x8A, 0x91),
    };

    // ───────────── Squarified-Algorithmus (Bruls, Huizing, van Wijk) ─────────────

    private static List<(Rect, MapNode)> Layout(List<MapNode> nodes, Rect r)
    {
        var result = new List<(Rect, MapNode)>();
        double total = nodes.Sum(n => (double)n.Size);
        if (total <= 0 || r.Width <= 0 || r.Height <= 0) return result;

        double scale = r.Width * r.Height / total;
        var items = nodes.Select(n => (Node: n, Area: n.Size * scale)).Where(i => i.Area > 0.5).ToList();
        int idx = 0;
        while (idx < items.Count)
        {
            double side = Math.Min(r.Width, r.Height);
            var row = new List<(MapNode Node, double Area)> { items[idx++] };
            while (idx < items.Count)
            {
                var next = new List<(MapNode, double)>(row) { items[idx] };
                if (Worst(next, side) > Worst(row, side)) break;
                row.Add(items[idx++]);
            }

            double sum = row.Sum(x => x.Area);
            if (r.Width >= r.Height)
            {
                double w = sum / r.Height, y = r.Y;
                foreach (var (n, a) in row)
                {
                    double h = a / w;
                    result.Add((new Rect(r.X, y, w, h), n));
                    y += h;
                }
                r = new Rect(r.X + w, r.Y, Math.Max(0, r.Width - w), r.Height);
            }
            else
            {
                double h = sum / r.Width, x = r.X;
                foreach (var (n, a) in row)
                {
                    double w = a / h;
                    result.Add((new Rect(x, r.Y, w, h), n));
                    x += w;
                }
                r = new Rect(r.X, r.Y + h, r.Width, Math.Max(0, r.Height - h));
            }
            if (r.Width < 1 || r.Height < 1) break;
        }
        return result;
    }

    private static double Worst(List<(MapNode Node, double Area)> row, double side)
    {
        double sum = 0, max = 0, min = double.MaxValue;
        foreach (var (_, a) in row)
        {
            sum += a;
            if (a > max) max = a;
            if (a < min) min = a;
        }
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }
}
