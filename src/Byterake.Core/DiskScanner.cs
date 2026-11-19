using System.Diagnostics;

namespace Byterake.Core;

public sealed class DirNode
{
    public DirNode(string path, DirNode? parent)
    {
        Path = path;
        Parent = parent;
        Name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        if (Name.Length == 0) Name = path;
    }

    public string Path { get; }
    public string Name { get; }
    public DirNode? Parent { get; }
    /// <summary>Total bytes of every file at or below this folder (after the scan finishes).</summary>
    public long Size { get; internal set; }
    public long FileCount { get; internal set; }
    public List<DirNode> Children { get; } = new();
    /// <summary>Set when the folder could not be read; its size is then unknown (counted as 0).</summary>
    public string? SkipReason { get; internal set; }
}

public sealed record FileEntry(string Path, long Size, DateTime Modified);

public sealed record ScanProgress(long Files, long Folders, long Bytes, string CurrentFolder);

public sealed class ScanResult
{
    public required DirNode Root { get; init; }
    public List<FileEntry> LargestFiles { get; init; } = new();
    /// <summary>Files at or above the large-file threshold, largest first.</summary>
    public List<FileEntry> LargeFiles { get; init; } = new();
    public List<(string Path, string Reason)> Skipped { get; init; } = new();
    public long Files { get; set; }
    public long Folders { get; set; }
    public long LinksSkipped { get; set; }
    public bool Cancelled { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Walks a folder tree and adds up sizes. Never follows symbolic links or junctions,
/// and records (instead of throwing on) folders it cannot read.
/// </summary>
public static class DiskScanner
{
    public static ScanResult Scan(
        string root,
        long largeFileThreshold = 500 * Units.MB,
        int topFiles = 200,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string full = System.IO.Path.GetFullPath(root);
        var rootNode = new DirNode(full, null);
        var result = new ScanResult { Root = rootNode };

        var all = new List<DirNode>();
        var stack = new Stack<DirNode>();
        stack.Push(rootNode);
        var top = new PriorityQueue<FileEntry, long>();
        long bytes = 0;
        long lastReport = 0;

        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
            var node = stack.Pop();
            all.Add(node);
            result.Folders++;

            try
            {
                foreach (var info in new DirectoryInfo(node.Path).EnumerateFileSystemInfos("*", opts))
                {
                    FileAttributes attrs;
                    try { attrs = info.Attributes; }
                    catch (Exception ex) { result.Skipped.Add((info.FullName, ex.Message)); continue; }

                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        // Symlink, junction or cloud placeholder: never followed, never counted.
                        result.LinksSkipped++;
                        continue;
                    }

                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        var child = new DirNode(info.FullName, node);
                        node.Children.Add(child);
                        stack.Push(child);
                    }
                    else
                    {
                        long len = ((FileInfo)info).Length;
                        node.Size += len;
                        node.FileCount++;
                        bytes += len;
                        result.Files++;
                        var entry = new FileEntry(info.FullName, len, info.LastWriteTime);
                        if (len >= largeFileThreshold) result.LargeFiles.Add(entry);
                        if (top.Count < topFiles) top.Enqueue(entry, len);
                        else if (len > top.Peek().Size) { top.Dequeue(); top.Enqueue(entry, len); }
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                node.SkipReason = ex is UnauthorizedAccessException ? "Access denied" : ex.Message;
                result.Skipped.Add((node.Path, node.SkipReason));
            }

            if (progress != null && sw.ElapsedMilliseconds - lastReport > 200)
            {
                lastReport = sw.ElapsedMilliseconds;
                progress.Report(new ScanProgress(result.Files, result.Folders, bytes, node.Path));
            }
        }

        // Add each folder's total into its parent, deepest folders first.
        for (int i = all.Count - 1; i >= 0; i--)
        {
            var n = all[i];
            if (n.Parent != null)
            {
                n.Parent.Size += n.Size;
                n.Parent.FileCount += n.FileCount;
            }
        }
        foreach (var n in all) n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));

        var list = new List<FileEntry>();
        while (top.Count > 0) list.Add(top.Dequeue());
        list.Reverse();
        result.LargestFiles.AddRange(list);
        result.LargeFiles.Sort((a, b) => b.Size.CompareTo(a.Size));

        result.Elapsed = sw.Elapsed;
        progress?.Report(new ScanProgress(result.Files, result.Folders, bytes, full));
        return result;
    }
}
