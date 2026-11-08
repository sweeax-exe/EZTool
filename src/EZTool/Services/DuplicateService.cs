using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using EZTool.Core;

namespace EZTool.Services;

public sealed partial class DuplicateFile : ObservableObject
{
    public required string Path { get; init; }
    public DateTime Modified { get; init; }
    public DateTime Created { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isRemoved;
}

public sealed partial class DuplicateGroup : ObservableObject
{
    public required long Size { get; init; }
    public required List<DuplicateFile> Files { get; init; }
    public string Extension => System.IO.Path.GetExtension(Files[0].Path).TrimStart('.').ToUpperInvariant();
    public long Wasted => Size * (Files.Count - 1);
}

public static class DuplicateService
{
    public static readonly Dictionary<string, string[]> Types = new()
    {
        ["images"] = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".raw", ".cr2", ".nef", ".arw", ".dng", ".psd"],
        ["videos"] = [".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".flv"],
        ["music"] = [".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma", ".opus"],
        ["docs"] = [".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".odt", ".epub"],
        ["archives"] = [".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".exe", ".msi"],
    };

    private const int PartialBytes = 64 * 1024;

    /// <summary>Build- und Entwicklerordner: Kopien dort sind gewollt (Build-Ausgaben, Pakete).</summary>
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "build", "dist", "out", "target", "node_modules", "packages", "venv", "__pycache__",
        "build-cache", "CMakeFiles", "Debug", "Release", "x64", "x86", "Intermediate", "Binaries", "DerivedDataCache",
    };

    public sealed record Progress(string Phase, string Detail);

    public static List<DuplicateGroup> Find(string root, long minSize, string type, IProgress<Progress>? progress, CancellationToken token)
    {
        HashSet<string>? exts = Types.TryGetValue(type, out var e) ? new HashSet<string>(e, StringComparer.OrdinalIgnoreCase) : null;

        // 1) Alle Dateien nach Größe gruppieren
        var bySize = new Dictionary<long, List<FileInfo>>();
        int scanned = 0;
        var last = DateTime.MinValue;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        var shallow = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System };

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var f in dir.EnumerateFiles("*", shallow))
                {
                    scanned++;
                    try
                    {
                        // Cloud-Platzhalter nie lesen – das würde den Download auslösen
                        if ((f.Attributes & FolderService.CloudOnly) != 0) continue;
                        if (f.Length < minSize) continue;
                        if (exts != null && !exts.Contains(f.Extension)) continue;
                        if (!bySize.TryGetValue(f.Length, out var list)) bySize[f.Length] = list = [];
                        list.Add(f);
                    }
                    catch { /* egal */ }
                }
                foreach (var sub in dir.EnumerateDirectories("*", shallow))
                    if (!FolderService.IsSystemSkip(sub) && !SkipFolders.Contains(sub.Name)) stack.Push(sub);
            }
            catch { /* Ordner nicht lesbar */ }

            if ((DateTime.UtcNow - last).TotalMilliseconds > 150)
            {
                last = DateTime.UtcNow;
                progress?.Report(new Progress(Loc.T("Duplicates.Phase.Scanning", scanned), dir.FullName));
            }
        }

        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        int total = candidates.Sum(c => c.Value.Count), done = 0;
        var groups = new List<DuplicateGroup>();

        // 2) Gleiche Größe → Anfang+Ende vergleichen → vollständigen Inhalt vergleichen
        foreach (var (size, files) in candidates.OrderByDescending(c => c.Key))
        {
            token.ThrowIfCancellationRequested();
            foreach (var partialGroup in files.GroupBy(f => SafeHash(f, partial: true)).Where(g => g.Key != null && g.Count() > 1))
            {
                var sameContent = size <= PartialBytes * 2
                    ? partialGroup.GroupBy(_ => partialGroup.Key) // ganze Datei schon im Teil-Hash enthalten
                    : partialGroup.GroupBy(f => SafeHash(f, partial: false));

                foreach (var full in sameContent.Where(g => g.Key != null && g.Count() > 1))
                {
                    var dupes = full.Select(f => new DuplicateFile { Path = f.FullName, Modified = f.LastWriteTime, Created = f.CreationTime })
                        .OrderBy(f => f.Created).ToList();
                    groups.Add(new DuplicateGroup { Size = size, Files = dupes });
                }
            }
            done += files.Count;
            if ((DateTime.UtcNow - last).TotalMilliseconds > 150)
            {
                last = DateTime.UtcNow;
                progress?.Report(new Progress(Loc.T("Duplicates.Phase.Comparing", done, total), files[0].FullName));
            }
        }

        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    private static string? SafeHash(FileInfo f, bool partial)
    {
        try
        {
            using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
            if (!partial) return Convert.ToHexString(SHA256.HashData(stream));

            var buffer = new byte[PartialBytes * 2];
            int read = stream.Read(buffer, 0, PartialBytes);
            if (stream.Length > PartialBytes * 2)
            {
                stream.Seek(-PartialBytes, SeekOrigin.End);
                read += stream.Read(buffer, read, PartialBytes);
            }
            else
            {
                read += stream.Read(buffer, read, buffer.Length - read);
            }
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch { return null; }
    }
}
