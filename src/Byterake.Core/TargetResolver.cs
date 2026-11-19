using System.IO.Enumeration;

namespace Byterake.Core;

public sealed record CleanFile(string Path, long Size, DateTime Modified);

public sealed class TargetMeasure
{
    /// <summary>Normalized root folders or files the target covers (only ones that exist).</summary>
    public List<string> Roots { get; } = new();
    public List<CleanFile> Files { get; } = new();
    public long Bytes { get; set; }
    /// <summary>Folders that could not be read (often fixed by running as admin).</summary>
    public int Unreadable { get; set; }
    /// <summary>Files refused by the never-touch list or because they are links.</summary>
    public List<(string Path, string Reason)> Refused { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>Turns catalog entries into real folders and the files inside them.</summary>
public static class TargetResolver
{
    static readonly EnumerationOptions OneLevel = new()
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Expands %VARS% and * folder wildcards. Returns existing, normalized paths.</summary>
    public static List<string> ExpandPaths(IEnumerable<string> rawPaths, List<string>? notes = null)
    {
        var result = new List<string>();
        foreach (var raw in rawPaths)
        {
            string expanded = Environment.ExpandEnvironmentVariables(raw);
            if (expanded.Contains('%'))
            {
                notes?.Add($"Skipped '{raw}': an environment variable in it is not set on this PC.");
                continue;
            }
            if (!Path.IsPathFullyQualified(expanded))
            {
                notes?.Add($"Skipped '{raw}': not a full path.");
                continue;
            }

            var parts = expanded.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var current = new List<string> { parts[0] + "\\" };
            for (int i = 1; i < parts.Length; i++)
            {
                string seg = parts[i];
                var next = new List<string>();
                bool wild = seg.Contains('*') || seg.Contains('?');
                foreach (var dir in current)
                {
                    if (!wild)
                    {
                        next.Add(Path.Combine(dir, seg));
                        continue;
                    }
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        foreach (var d in new DirectoryInfo(dir).EnumerateDirectories(seg, OneLevel))
                        {
                            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            next.Add(d.FullName);
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
                }
                current = next;
            }

            foreach (var c in current)
            {
                if (!File.Exists(c) && !Directory.Exists(c)) continue;
                string n;
                try { n = PathSafety.Normalize(c); } catch { continue; }
                if (!result.Contains(n, StringComparer.OrdinalIgnoreCase)) result.Add(n);
            }
        }
        return result;
    }

    /// <summary>Lists every file a "contents" or "windowstool" target would remove, and its size.</summary>
    public static TargetMeasure MeasureContents(CatalogTarget target, CancellationToken ct = default)
    {
        var m = new TargetMeasure();
        foreach (var root in ExpandPaths(target.Paths, m.Notes))
        {
            if (ct.IsCancellationRequested) break;
            var v = PathSafety.Check(root);
            if (!v.Allowed && !IsAllowedRootException(root, v))
            {
                m.Refused.Add((root, v.Reason));
                continue;
            }
            m.Roots.Add(root);

            if (File.Exists(root))
            {
                AddFile(m, new FileInfo(root), target);
                continue;
            }
            Walk(m, root, target, ct);
        }
        return m;
    }

    /// <summary>A catalog root may itself be on the exact-match list (e.g. %TEMP%): its contents are fine.</summary>
    static bool IsAllowedRootException(string root, SafetyVerdict v) =>
        v.Reason == "Top-level system or user folder" &&
        root.Equals(PathSafety.Normalize("%TEMP%"), StringComparison.OrdinalIgnoreCase);

    static void Walk(TargetMeasure m, string root, CatalogTarget target, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) return;
            string dir = stack.Pop();
            try
            {
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", OneLevel))
                {
                    var attrs = info.Attributes;
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        m.Refused.Add((info.FullName, "Symbolic link or junction (not followed)"));
                        continue;
                    }
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        if (target.Recursive) stack.Push(info.FullName);
                        continue;
                    }
                    AddFile(m, (FileInfo)info, target);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                m.Unreadable++;
            }
        }
    }

    static void AddFile(TargetMeasure m, FileInfo f, CatalogTarget target)
    {
        if (!target.Patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, f.Name, ignoreCase: true))) return;
        var v = PathSafety.Check(f.FullName);
        if (!v.Allowed) { m.Refused.Add((f.FullName, v.Reason)); return; }
        if ((f.Attributes & FileAttributes.System) != 0) { m.Refused.Add((f.FullName, "System-protected file")); return; }
        long len;
        try { len = f.Length; } catch { return; }
        m.Files.Add(new CleanFile(f.FullName, len, f.LastWriteTime));
        m.Bytes += len;
    }

    /// <summary>Installers and archives in Downloads over the size limit.</summary>
    public static TargetMeasure MeasureDownloads(CatalogTarget target, string? downloadsFolder, CancellationToken ct = default)
    {
        var m = new TargetMeasure();
        if (downloadsFolder == null || !Directory.Exists(downloadsFolder))
        {
            m.Notes.Add("Downloads folder not found.");
            return m;
        }
        var t = new CatalogTarget
        {
            Id = target.Id,
            Patterns = target.Extensions.Select(e => "*" + e).ToList(),
            Recursive = true,
        };
        m.Roots.Add(PathSafety.Normalize(downloadsFolder));
        Walk(m, m.Roots[0], t, ct);
        long min = target.MinSizeMB * Units.MB;
        m.Files.RemoveAll(f => f.Size < min);
        m.Files.Sort((a, b) => b.Size.CompareTo(a.Size));
        m.Bytes = m.Files.Sum(f => f.Size);
        return m;
    }
}
