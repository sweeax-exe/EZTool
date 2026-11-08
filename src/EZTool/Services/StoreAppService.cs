using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace EZTool.Services;

/// <summary>Microsoft-Store-/MSIX-Apps über die offizielle Windows-API (PackageManager) lesen und entfernen.</summary>
public static class StoreAppService
{
    /// <summary>Apps, ohne die Windows bzw. die App-Verwaltung nicht mehr richtig funktioniert.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl", "Microsoft.Windows.CloudExperienceHost", "Microsoft.Windows.ContentDeliveryManager",
        "MicrosoftWindows.Client.CBS", "MicrosoftWindows.Client.Core", "Microsoft.WindowsAppRuntime.CBS", "Microsoft.Winget.Source",
        "Microsoft.UI.Xaml.CBS", "Microsoft.Windows.SecureAssessmentBrowser", "windows.immersivecontrolpanel",
        "Microsoft.Windows.Search", "MicrosoftWindows.CrossDevice", "Microsoft.ApplicationCompatibilityEnhancements",
        // Von anderen Apps/Spielen benötigt
        "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI", "Microsoft.GamingServices", "Microsoft.XboxGameCallableUI",
        "Microsoft.WidgetsPlatformRuntime", "Microsoft.Services.Store.Engagement", "Microsoft.MicrosoftEdge.Stable",
        "Microsoft.WebExperience", "Microsoft.Edge.GameAssist",
    };

    public static List<InstalledProgram> LoadAll()
    {
        var list = new List<InstalledProgram>();
        try
        {
            var pm = new PackageManager();
            foreach (var p in pm.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (p.IsFramework || p.IsResourcePackage || p.SignatureKind == PackageSignatureKind.System) continue;
                    var name = p.DisplayName;
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) continue;

                    var v = p.Id.Version;
                    string location = "";
                    try { location = p.InstalledLocation?.Path ?? ""; } catch { /* nicht verfügbar */ }

                    list.Add(new InstalledProgram
                    {
                        Name = name.Trim(),
                        Version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                        Publisher = SafeString(() => p.PublisherDisplayName),
                        InstallDate = SafeDate(() => p.InstalledDate.LocalDateTime),
                        InstallLocation = location,
                        IconPath = SafeString(() => p.Logo?.LocalPath ?? ""),
                        Hive = RegistryHive.CurrentUser,
                        View = RegistryView.Registry64,
                        KeyName = p.Id.FullName,
                        IsStore = true,
                        IsProtected = Protected.Contains(p.Id.Name) || p.Id.Name.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase)
                                                                   || p.Id.Name.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase),
                    });
                }
                catch { /* einzelnes Paket nicht lesbar */ }
            }
        }
        catch { /* API nicht verfügbar */ }

        // Gleiche App kann mehrfach (x64/neutral) auftauchen
        return list.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private static string SafeString(Func<string> f)
    {
        try { return f()?.Trim() ?? ""; } catch { return ""; }
    }

    private static DateTime? SafeDate(Func<DateTime> f)
    {
        try
        {
            var d = f();
            return d.Year > 2000 ? d : null;
        }
        catch { return null; }
    }

    /// <summary>Logo der App laden. Store-Apps liefern oft nur den Basisnamen (z. B. StoreLogo.png), die echte Datei heißt StoreLogo.scale-100.png.</summary>
    public static ImageSource? LoadLogo(InstalledProgram p)
    {
        try
        {
            var path = p.IconPath;
            if (string.IsNullOrEmpty(path)) return null;
            if (!File.Exists(path))
            {
                var dir = Path.GetDirectoryName(path);
                if (dir == null || !Directory.Exists(dir)) return null;
                var stem = Path.GetFileNameWithoutExtension(path);
                path = Directory.EnumerateFiles(dir, stem + ".*" + Path.GetExtension(path))
                    .OrderByDescending(f => f.Contains("scale-200") ? 3 : f.Contains("scale-150") ? 2 : f.Contains("scale-100") ? 1 : 0)
                    .FirstOrDefault() ?? "";
                if (path.Length == 0) return null;
            }

            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = 64;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public static long MeasureSize(InstalledProgram p)
    {
        if (string.IsNullOrEmpty(p.InstallLocation) || !Directory.Exists(p.InstallLocation)) return 0;
        long total = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(p.InstallLocation).EnumerateFiles("*", opts)) total += f.Length;
        }
        catch { /* egal */ }
        return total;
    }

    public static bool IsStillInstalled(InstalledProgram p)
    {
        try { return new PackageManager().FindPackageForUser(string.Empty, p.KeyName) != null; }
        catch { return false; }
    }

    /// <summary>Entfernt die App für den aktuellen Benutzer (inklusive ihrer App-Daten).</summary>
    public static async Task UninstallAsync(InstalledProgram p)
    {
        if (p.IsProtected) throw new InvalidOperationException("Diese App ist systemrelevant und wird nicht entfernt.");
        var result = await new PackageManager().RemovePackageAsync(p.KeyName).AsTask();
        if (result.ExtendedErrorCode != null && result.ExtendedErrorCode.HResult != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.ErrorText) ? result.ExtendedErrorCode.Message : result.ErrorText);
    }
}
