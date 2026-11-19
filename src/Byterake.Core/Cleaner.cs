namespace Byterake.Core;

public enum CleanMode { RecycleBin, Permanent }

/// <summary>One confirmed group of files: the exact list the user saw, plus where they may live.</summary>
public sealed class CleanJob
{
    public required string Label { get; init; }
    public required IReadOnlyList<CleanFile> Files { get; init; }
    /// <summary>Folders the files must sit inside. A file outside all of them is refused.</summary>
    public required IReadOnlyList<string> AllowedRoots { get; init; }
    /// <summary>After deleting, remove folders left empty under the roots (never the roots themselves).</summary>
    public bool RemoveEmptyFolders { get; init; }
}

public sealed class CleanReport
{
    public int FilesRemoved { get; set; }
    public long BytesRemoved { get; set; }
    public int FoldersRemoved { get; set; }
    public List<(string Path, string Reason)> Failed { get; } = new();
    public List<(string Path, string Reason)> Refused { get; } = new();
    public bool Cancelled { get; set; }
}

public sealed record CleanProgress(int Done, int Total, string Current);

/// <summary>
/// Deletes only the files it is handed, after re-checking each one against the never-touch list,
/// the allowed roots, and links/junctions on the way. Locked files are skipped and reported.
/// </summary>
public static class Cleaner
{
    const int BatchSize = 128;
    const int FolderBatchSize = 32;

    /// <summary>Counts handled files and reports progress at most every 150 ms.</summary>
    sealed class Counter(int total, IProgress<CleanProgress>? progress)
    {
        readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        long _last = -1000;
        public int Done { get; private set; }

        public void Add(int n, string current)
        {
            Done += n;
            Tick(current);
        }

        public void Status(string current) => Tick(current);

        void Tick(string current)
        {
            if (progress == null || _sw.ElapsedMilliseconds - _last < 150) return;
            _last = _sw.ElapsedMilliseconds;
            progress.Report(new CleanProgress(Done, total, current));
        }
    }

    /// <summary>
    /// Recycle Bin mode only. Moving thousands of files one by one through the Windows shell is slow,
    /// so a folder is moved in one step when — checked again right before the move — it holds exactly
    /// confirmed files: nothing unconfirmed, no links or junctions, no system files. Everything else
    /// is returned for the file-by-file path. Afterwards each confirmed file is checked: any that is
    /// still there (in use) goes back to the file-by-file path as well.
    /// </summary>
    static List<CleanFile> RecycleWholeFolders(
        List<CleanFile> valid, List<string> roots, CleanReport report, ActivityLog log, Counter counter, CancellationToken ct)
    {
        var confirmed = new HashSet<string>(valid.Select(v => v.Path), StringComparer.OrdinalIgnoreCase);

        // Every folder above a confirmed file, up to (not including) its target root.
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in valid)
        {
            string? root = roots.FirstOrDefault(r => PathSafety.IsUnder(f.Path, r));
            if (root == null) continue;
            string? d = Path.GetDirectoryName(f.Path);
            while (d != null && d.Length > root.Length && PathSafety.IsUnder(d, root))
            {
                if (!candidates.Add(d)) break;
                d = Path.GetDirectoryName(d);
            }
        }

        // Deepest first, so a folder can rely on the verdicts for its sub-folders.
        var whole = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in candidates.OrderByDescending(c => c.Count(ch => ch == '\\')))
        {
            if (ct.IsCancellationRequested) return valid;
            if (FolderIsExactlyConfirmed(d, confirmed, whole, candidates)) whole.Add(d);
        }

        // Keep only the top-most whole folders.
        var tops = whole.Where(d => { var p = Path.GetDirectoryName(d); return p == null || !whole.Contains(p); }).ToList();
        if (tops.Count == 0) return valid;

        // Which confirmed files sit inside which top folder.
        var topSet = new HashSet<string>(tops, StringComparer.OrdinalIgnoreCase);
        var inside = tops.ToDictionary(t => t, _ => new List<CleanFile>(), StringComparer.OrdinalIgnoreCase);
        var rest = new List<CleanFile>();
        foreach (var f in valid)
        {
            string? d = Path.GetDirectoryName(f.Path);
            while (d != null && !topSet.Contains(d)) d = Path.GetDirectoryName(d);
            if (d != null) inside[d].Add(f); else rest.Add(f);
        }

        for (int i = 0; i < tops.Count; i += FolderBatchSize)
        {
            if (ct.IsCancellationRequested) { report.Cancelled = true; break; }
            var group = tops.GetRange(i, Math.Min(FolderBatchSize, tops.Count - i));
            Native.Recycle(group);
            foreach (var folder in group)
            {
                int moved = 0;
                foreach (var f in inside[folder])
                {
                    if (File.Exists(f.Path)) rest.Add(f); // in use: retried file by file
                    else { Moved(f, report, log); moved++; }
                }
                log.Write("DELETE", $"Recycle Bin: folder {folder} ({moved} of {inside[folder].Count} files)");
                counter.Add(moved, folder);
            }
        }
        if (report.Cancelled)
        {
            // Not started: hand back everything that wasn't processed, but don't move it.
            return new List<CleanFile>();
        }
        return rest;
    }

    static bool FolderIsExactlyConfirmed(string dir, HashSet<string> confirmed, HashSet<string> whole, HashSet<string> candidates)
    {
        try
        {
            if (!PathSafety.CheckOnDisk(dir).Allowed) return false;
            var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
            foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
            {
                var a = e.Attributes;
                if ((a & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) return false;
                if ((a & FileAttributes.Directory) != 0)
                {
                    if (!candidates.Contains(e.FullName) || !whole.Contains(e.FullName)) return false;
                }
                else if (!confirmed.Contains(e.FullName)) return false;
            }
            return true;
        }
        catch
        {
            return false; // can't be sure: go file by file
        }
    }

    public static CleanReport Run(
        IReadOnlyList<CleanJob> jobs,
        CleanMode mode,
        ActivityLog log,
        IProgress<CleanProgress>? progress = null,
        CancellationToken ct = default)
    {
        var report = new CleanReport();
        int total = jobs.Sum(j => j.Files.Count);
        var counter = new Counter(total, progress);
        string how = mode == CleanMode.RecycleBin ? "Recycle Bin" : "Permanent";
        log.Info($"Clean started: {total} files in {jobs.Count} groups, mode = {how}.");

        foreach (var job in jobs)
        {
            var roots = job.AllowedRoots.Select(PathSafety.Normalize).ToList();
            var linkCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            log.Info($"Group '{job.Label}': {job.Files.Count} files.");

            // 1. Check every confirmed file again, right now.
            var valid = new List<CleanFile>(job.Files.Count);
            int checkedCount = 0;
            foreach (var f in job.Files)
            {
                if (ct.IsCancellationRequested) { report.Cancelled = true; break; }
                if (++checkedCount % 500 == 0) counter.Status("Checking " + f.Path);

                string? reason = Validate(f.Path, roots, linkCache, out string normalized);
                if (reason == "No longer exists")
                {
                    log.Skipped(f.Path, reason);
                    counter.Add(1, f.Path);
                    continue;
                }
                if (reason != null)
                {
                    report.Refused.Add((f.Path, reason));
                    log.Refused(f.Path, reason);
                    counter.Add(1, f.Path);
                    continue;
                }
                valid.Add(f with { Path = normalized });
            }
            if (report.Cancelled) break;

            // 2. Remove them.
            if (mode == CleanMode.Permanent)
            {
                foreach (var f in valid)
                {
                    if (ct.IsCancellationRequested) { report.Cancelled = true; break; }
                    DeletePermanently(f, report, log);
                    counter.Add(1, f.Path);
                }
            }
            else
            {
                var rest = job.RemoveEmptyFolders
                    ? RecycleWholeFolders(valid, roots, report, log, counter, ct)
                    : valid;
                for (int i = 0; i < rest.Count && !report.Cancelled; i += BatchSize)
                {
                    if (ct.IsCancellationRequested) { report.Cancelled = true; break; }
                    var batch = rest.GetRange(i, Math.Min(BatchSize, rest.Count - i));
                    RecycleBatch(batch, report, log);
                    counter.Add(batch.Count, batch[^1].Path);
                }
            }

            if (job.RemoveEmptyFolders && !report.Cancelled)
            {
                foreach (var root in roots) RemoveEmptyFolders(root, report, log);
            }
            if (report.Cancelled) break;
        }

        progress?.Report(new CleanProgress(counter.Done, total, ""));
        log.Info($"Clean finished: removed {report.FilesRemoved} files ({Units.Bytes(report.BytesRemoved)}), " +
                 $"{report.Failed.Count} could not be removed, {report.Refused.Count} refused" +
                 (report.Cancelled ? ", cancelled by user." : "."));
        return report;
    }

    /// <summary>Returns null if the file may be deleted, otherwise the reason it may not.</summary>
    static string? Validate(string path, List<string> roots, Dictionary<string, bool> linkCache, out string normalized)
    {
        normalized = path;
        try { normalized = PathSafety.Normalize(path); }
        catch (Exception ex) { return $"Path is ambiguous ({ex.Message})"; }

        string n = normalized;
        string? root = roots.FirstOrDefault(r => PathSafety.IsUnder(n, r) && !n.Equals(r, StringComparison.OrdinalIgnoreCase));
        if (root == null) return "Outside the selected cleanup paths";

        var v = PathSafety.CheckOnDisk(n);
        if (!v.Allowed) return v.Reason;

        // Every folder between the root and the file must be a real folder, not a link.
        string? dir = Path.GetDirectoryName(n);
        while (dir != null && dir.Length > root.Length)
        {
            if (!linkCache.TryGetValue(dir, out bool isLink))
            {
                try { isLink = (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0; }
                catch { isLink = true; } // can't tell: treat as unsafe
                linkCache[dir] = isLink;
            }
            if (isLink) return "Inside a symbolic link or junction";
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    static void DeletePermanently(CleanFile f, CleanReport report, ActivityLog log)
    {
        try
        {
            File.Delete(f.Path);
            report.FilesRemoved++;
            report.BytesRemoved += f.Size;
            log.Deleted(f.Path, f.Size, "Permanent");
        }
        catch (Exception ex)
        {
            string reason = Explain(f.Path, ex);
            report.Failed.Add((f.Path, reason));
            log.Skipped(f.Path, reason);
        }
    }

    static void RecycleBatch(List<CleanFile> batch, CleanReport report, ActivityLog log)
    {
        Native.Recycle(batch.Select(b => b.Path).ToList());
        var left = new List<CleanFile>();
        foreach (var f in batch)
        {
            if (File.Exists(f.Path)) left.Add(f);
            else Moved(f, report, log);
        }
        // Retry one by one so one locked file doesn't hold back the others.
        foreach (var f in left)
        {
            if (batch.Count > 1) Native.Recycle(new[] { f.Path });
            if (File.Exists(f.Path))
            {
                string reason = Explain(f.Path, null);
                report.Failed.Add((f.Path, reason));
                log.Skipped(f.Path, reason);
            }
            else Moved(f, report, log);
        }
    }

    static void Moved(CleanFile f, CleanReport report, ActivityLog log)
    {
        report.FilesRemoved++;
        report.BytesRemoved += f.Size;
        log.Deleted(f.Path, f.Size, "Recycle Bin");
    }

    static string Explain(string path, Exception? ex)
    {
        if (ex is UnauthorizedAccessException) return "Access denied or read-only";
        if (ex is IOException) return "In use by another program";
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return "Windows did not move it (it may be too long a path or too big for the Recycle Bin)";
        }
        catch (UnauthorizedAccessException) { return "Access denied or read-only"; }
        catch (IOException) { return "In use by another program"; }
        catch (Exception e) { return e.Message; }
    }

    static void RemoveEmptyFolders(string root, CleanReport report, ActivityLog log)
    {
        if (!Directory.Exists(root)) return;
        var dirs = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string d = stack.Pop();
            try
            {
                foreach (var sub in new DirectoryInfo(d).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    dirs.Add(sub.FullName);
                    stack.Push(sub.FullName);
                }
            }
            catch { }
        }
        foreach (var d in dirs.OrderByDescending(x => x.Length))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(d).Any()) continue;
                if (!PathSafety.CheckOnDisk(d).Allowed) continue;
                Directory.Delete(d, false);
                report.FoldersRemoved++;
                log.Write("DELETE", $"Empty folder: {d}");
            }
            catch { /* in use or no access: leave it */ }
        }
    }

    /// <summary>Permanently empties the Recycle Bin of one drive. Returns bytes freed.</summary>
    public static long EmptyRecycleBin(string driveRoot, ActivityLog log)
    {
        var (bytes, items) = Native.QueryRecycleBin(driveRoot);
        int hr = Native.EmptyRecycleBin(driveRoot);
        // S_OK, or "already empty" (0x8000FFFF on some builds)
        if (hr == 0 || items == 0)
        {
            log.Write("DELETE", $"Emptied Recycle Bin on {driveRoot}: {items} items, {Units.Bytes(bytes)}");
            return bytes;
        }
        log.Error($"Could not empty Recycle Bin on {driveRoot} (code 0x{hr:X8}).");
        return 0;
    }
}
