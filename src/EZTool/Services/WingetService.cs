using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EZTool.Services;

public sealed partial class UpgradeItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string Version { get; init; } = "";
    public string Available { get; init; } = "";
    public string Source { get; init; } = "";

    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _status = "Update verfügbar";
    [ObservableProperty] private int _state; // 0 = offen, 1 = erledigt, 2 = Fehler
    [ObservableProperty] private System.Windows.Media.ImageSource? _icon;
}

/// <summary>Software-Updates über den Windows-Paketmanager winget (bei Windows 11 vorinstalliert).</summary>
public static class WingetService
{
    public static bool IsAvailable()
    {
        try
        {
            var (code, output) = Run("--version", TimeSpan.FromSeconds(20));
            return code == 0 && output.Trim().StartsWith('v');
        }
        catch { return false; }
    }

    /// <summary>Liste verfügbarer Updates. NeedsAgreement = winget möchte, dass erst die Quell-Bedingungen akzeptiert werden.</summary>
    public static (List<UpgradeItem> Items, bool NeedsAgreement) ListUpgrades(bool acceptSourceAgreements)
    {
        var args = "upgrade --include-unknown --disable-interactivity" + (acceptSourceAgreements ? " --accept-source-agreements" : "");
        var output = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            (_, output) = Run(args, TimeSpan.FromMinutes(3));
            var items = Parse(output);
            if (items.Count > 0 || output.Contains("----")) return (items, false);
        }
        return ([], !acceptSourceAgreements && NeedsSourceAgreement(output));
    }

    /// <summary>Liest die Tabelle(n) aus der winget-Ausgabe. Spaltenpositionen ergeben sich aus der Kopfzeile (sprachunabhängig).</summary>
    public static List<UpgradeItem> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l.Split('\r')[^1]).ToList();
        var result = new List<UpgradeItem>();
        for (int i = 1; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("---")) continue;
            var header = lines[i - 1];
            var starts = new List<int> { 0 };
            for (int c = 1; c < header.Length; c++)
                if (header[c] != ' ' && header[c - 1] == ' ') starts.Add(c);
            if (starts.Count < 4) continue;

            string Col(string row, int idx)
            {
                if (starts[idx] >= row.Length) return "";
                int end = idx + 1 < starts.Count ? Math.Min(starts[idx + 1], row.Length) : row.Length;
                return row[starts[idx]..end].Trim();
            }

            for (int r = i + 1; r < lines.Count; r++)
            {
                var row = lines[r];
                if (string.IsNullOrWhiteSpace(row) || row.Length < starts[2]) break;
                var id = Col(row, 1);
                if (id.Length == 0 || id.Contains(' ')) break;
                result.Add(new UpgradeItem
                {
                    Name = Col(row, 0).TrimEnd('…'),
                    Id = id,
                    Version = Col(row, 2),
                    Available = Col(row, 3),
                    Source = starts.Count > 4 ? Col(row, 4) : "",
                });
            }
        }
        return result.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    public static bool NeedsSourceAgreement(string output) =>
        output.Contains("agreement", StringComparison.OrdinalIgnoreCase) || output.Contains("Vereinbarung", StringComparison.OrdinalIgnoreCase);

    /// <summary>Aktualisiert ein Paket still im Hintergrund. Gibt (Erfolg, Meldung) zurück.</summary>
    public static Task<(bool Ok, string Message)> UpgradeAsync(UpgradeItem item) => Task.Run(() =>
    {
        var (code, output) = Run($"upgrade --id \"{item.Id}\" --exact --silent --include-unknown --accept-package-agreements --accept-source-agreements --disable-interactivity",
            TimeSpan.FromMinutes(30));
        if (code == 0) return (true, "Aktualisiert");
        var last = output.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
        return (false, string.IsNullOrEmpty(last) ? $"Fehler (Code 0x{code:X8})" : last);
    });

    private static (int Code, string Output) Run(string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("winget", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("winget konnte nicht gestartet werden.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(true); } catch { /* egal */ }
            return (-1, "Zeitüberschreitung");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
