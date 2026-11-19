namespace Byterake.Core;

public sealed record SafetyVerdict(bool Allowed, string Reason)
{
    public static readonly SafetyVerdict Ok = new(true, "");
    public static SafetyVerdict Block(string reason) => new(false, reason);
}

/// <summary>
/// The hard-coded never-touch list. Every delete goes through <see cref="CheckOnDisk"/> first.
/// There is no setting, file or flag that switches any of this off.
/// </summary>
public static class PathSafety
{
    static readonly string[] ProtectedFileNames = { "pagefile.sys", "hiberfil.sys", "swapfile.sys" };

    static readonly string[] UserFolderNames = { "Documents", "Desktop", "Pictures", "Videos", "Music" };

    static List<(string Root, string Label)>? _trees;
    static List<string>? _exact;
    static readonly object Gate = new();

    /// <summary>Folders whose whole contents are off limits.</summary>
    public static IReadOnlyList<(string Root, string Label)> ProtectedTrees { get { Build(); return _trees!; } }

    static void Build()
    {
        if (_trees != null) return;
        lock (Gate)
        {
            if (_trees != null) return;
            var trees = new List<(string, string)>();
            void Add(string raw, string label)
            {
                if (string.IsNullOrWhiteSpace(raw)) return;
                string expanded = Environment.ExpandEnvironmentVariables(raw);
                if (expanded.Contains('%') || !Path.IsPathFullyQualified(expanded)) return;
                string n = NormalizeUnchecked(expanded);
                if (!trees.Any(t => t.Item1.Equals(n, StringComparison.OrdinalIgnoreCase))) trees.Add((n, label));
            }

            Add(@"%SystemRoot%\System32", "Windows System32");
            Add(@"%SystemRoot%\SysWOW64", "Windows SysWOW64");
            Add(@"%SystemRoot%\WinSxS", "Windows component store (WinSxS)");
            Add("%ProgramFiles%", "Program Files");
            Add("%ProgramFiles(x86)%", "Program Files (x86)");
            Add("%ProgramW6432%", "Program Files");
            Add(@"%SystemDrive%\Program Files", "Program Files");
            Add(@"%SystemDrive%\Program Files (x86)", "Program Files (x86)");

            Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "your Documents folder");
            Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "your Desktop");
            Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "your Pictures folder");
            Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "your Videos folder");
            Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "your Music folder");
            foreach (var name in UserFolderNames)
            {
                Add($@"%USERPROFILE%\{name}", $"your {name} folder");
                Add($@"%USERPROFILE%\OneDrive\{name}", $"your {name} folder (OneDrive)");
                Add($@"%OneDrive%\{name}", $"your {name} folder (OneDrive)");
            }

            var exact = new List<string>();
            void Exact(string raw)
            {
                string e = Environment.ExpandEnvironmentVariables(raw);
                if (e.Contains('%') || !Path.IsPathFullyQualified(e)) return;
                exact.Add(NormalizeUnchecked(e));
            }
            Exact("%SystemRoot%");
            Exact("%USERPROFILE%");
            Exact("%LOCALAPPDATA%");
            Exact("%APPDATA%");
            Exact("%ProgramData%");
            Exact("%TEMP%");
            Exact(@"%SystemDrive%\Users");
            Exact("%OneDrive%");

            _exact = exact;
            _trees = trees;
        }
    }

    /// <summary>
    /// Expands environment variables, resolves "..", forward slashes, \\?\ prefixes, 8.3 short names
    /// and trailing separators into one canonical full path. Throws if the path is not absolute.
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Empty path.");
        string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (p.Contains('%')) throw new ArgumentException("Path has an environment variable that is not set.");
        p = p.Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\")) p = p[4..];
        if (!Path.IsPathFullyQualified(p)) throw new ArgumentException("Not a full path.");
        return NormalizeUnchecked(p);
    }

    static string NormalizeUnchecked(string p)
    {
        p = Path.GetFullPath(p.Replace('/', '\\'));
        if (p.Contains('~')) p = ExpandShortNames(p);
        while (p.Length > 3 && p.EndsWith('\\')) p = p[..^1];
        if (p.Length == 2 && p[1] == ':') p += "\\";
        return p;
    }

    /// <summary>Turns C:\PROGRA~1\x into C:\Program Files\x using the longest part of the path that exists.</summary>
    static string ExpandShortNames(string p)
    {
        string existing = p;
        string rest = "";
        while (!string.IsNullOrEmpty(existing) && !File.Exists(existing) && !Directory.Exists(existing))
        {
            string? parent = Path.GetDirectoryName(existing);
            if (parent == null) return p;
            rest = Path.Combine(Path.GetFileName(existing), rest);
            existing = parent;
        }
        string? longer = Native.LongPath(existing);
        if (longer == null) return p;
        return rest.Length == 0 ? longer : Path.Combine(longer, rest.TrimEnd('\\'));
    }

    public static bool IsUnder(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        string r = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when everything inside this folder is off limits (used for the lock icons).</summary>
    public static bool ContentsProtected(string folder) =>
        !Check(Path.Combine(folder, "byterake-probe.tmp")).Allowed;

    /// <summary>Text-only check: is this path on the never-touch list?</summary>
    public static SafetyVerdict Check(string path)
    {
        string n;
        try { n = Normalize(path); }
        catch (Exception ex) { return SafetyVerdict.Block($"Path is ambiguous ({ex.Message})"); }

        Build();

        if (n.Length <= 3) return SafetyVerdict.Block("A whole drive cannot be deleted");

        var segments = n.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in segments)
        {
            if (s.StartsWith(".git", StringComparison.OrdinalIgnoreCase))
                return SafetyVerdict.Block("Source-control folder (.git)");
        }

        string file = segments[^1];
        foreach (var f in ProtectedFileNames)
        {
            if (file.Equals(f, StringComparison.OrdinalIgnoreCase))
                return SafetyVerdict.Block($"Windows system file ({f})");
        }

        foreach (var (root, label) in _trees!)
        {
            if (IsUnder(n, root)) return SafetyVerdict.Block($"Protected: {label}");
        }

        foreach (var e in _exact!)
        {
            if (n.Equals(e, StringComparison.OrdinalIgnoreCase))
                return SafetyVerdict.Block("Top-level system or user folder");
        }

        return SafetyVerdict.Ok;
    }

    /// <summary>
    /// Full check used right before a delete: the never-touch list plus what is on disk now
    /// (links, junctions and system-protected files are refused).
    /// </summary>
    public static SafetyVerdict CheckOnDisk(string path)
    {
        var v = Check(path);
        if (!v.Allowed) return v;
        try
        {
            string n = Normalize(path);
            if (!File.Exists(n) && !Directory.Exists(n)) return SafetyVerdict.Block("No longer exists");
            var attrs = File.GetAttributes(n);
            if ((attrs & FileAttributes.ReparsePoint) != 0) return SafetyVerdict.Block("Symbolic link or junction");
            if ((attrs & FileAttributes.System) != 0) return SafetyVerdict.Block("System-protected file");
        }
        catch (Exception ex)
        {
            return SafetyVerdict.Block($"Could not read file details ({ex.Message})");
        }
        return SafetyVerdict.Ok;
    }
}
