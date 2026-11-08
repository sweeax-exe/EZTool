namespace EZTool.Services;

public sealed class MapNode
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsDir { get; init; }
    public bool IsAggregate { get; init; }
    public long Size { get; set; }
    public long Files { get; set; }
    public MapNode? Parent { get; set; }
    public List<MapNode> Children { get; } = [];

    public string Extension => IsDir ? "" : System.IO.Path.GetExtension(Name).ToLowerInvariant();
    public double Share => Parent is { Size: > 0 } p ? Size * 100.0 / p.Size : 100;
}

/// <summary>Liest einen Ordner/ein Laufwerk komplett ein und baut einen Größenbaum für die Speicher-Karte.</summary>
public static class DiskMapService
{
    private const int FilesPerFolder = 12;

    public sealed record ScanProgress(long Files, long Bytes, string Current);

    public static MapNode Scan(string root, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        long files = 0, bytes = 0;
        var last = DateTime.MinValue;
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };

        MapNode Walk(DirectoryInfo dir, MapNode? parent, bool parallel)
        {
            token.ThrowIfCancellationRequested();
            var node = new MapNode { Name = dir.Name.Length > 0 ? dir.Name : dir.FullName, Path = dir.FullName, IsDir = true, Parent = parent };
            var fileList = new List<(string Name, string Path, long Size)>();
            try
            {
                foreach (var f in dir.EnumerateFiles("*", opts))
                {
                    try
                    {
                        // Cloud-Platzhalter belegen keinen lokalen Speicher
                        long len = (f.Attributes & FolderService.CloudOnly) != 0 ? 0 : f.Length;
                        fileList.Add((f.Name, f.FullName, len));
                    }
                    catch { /* egal */ }
                }
            }
            catch { /* nicht lesbar */ }

            var subs = new List<DirectoryInfo>();
            try { subs.AddRange(dir.EnumerateDirectories("*", opts)); } catch { /* nicht lesbar */ }

            var children = new MapNode[subs.Count];
            if (parallel && subs.Count > 1)
                Parallel.For(0, subs.Count, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Environment.ProcessorCount },
                    i => children[i] = Walk(subs[i], node, false));
            else
                for (int i = 0; i < subs.Count; i++) children[i] = Walk(subs[i], node, false);

            foreach (var c in children)
            {
                node.Children.Add(c);
                node.Size += c.Size;
                node.Files += c.Files;
            }

            // Die größten Dateien einzeln, der Rest zusammengefasst
            long fileBytes = 0;
            foreach (var f in fileList.OrderByDescending(f => f.Size).Take(FilesPerFolder))
            {
                node.Children.Add(new MapNode { Name = f.Name, Path = f.Path, Size = f.Size, Files = 1, Parent = node });
                fileBytes += f.Size;
            }
            if (fileList.Count > FilesPerFolder)
            {
                var restSize = fileList.OrderByDescending(f => f.Size).Skip(FilesPerFolder).Sum(f => f.Size);
                node.Children.Add(new MapNode
                {
                    Name = $"{fileList.Count - FilesPerFolder:N0} weitere Dateien", Path = dir.FullName, Size = restSize,
                    Files = fileList.Count - FilesPerFolder, IsAggregate = true, Parent = node
                });
                fileBytes += restSize;
            }
            node.Size += fileBytes;
            node.Files += fileList.Count;
            node.Children.Sort((a, b) => b.Size.CompareTo(a.Size));

            Interlocked.Add(ref files, fileList.Count);
            Interlocked.Add(ref bytes, fileBytes);
            if (progress != null && (DateTime.UtcNow - last).TotalMilliseconds > 150)
            {
                last = DateTime.UtcNow;
                progress.Report(new ScanProgress(Interlocked.Read(ref files), Interlocked.Read(ref bytes), dir.FullName));
            }
            return node;
        }

        return Walk(new DirectoryInfo(root), null, parallel: true);
    }
}
