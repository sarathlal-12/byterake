using System.Runtime.InteropServices;
using System.Text;

namespace Byterake.Core;

/// <summary>Thin wrappers over the few Windows shell calls Byterake needs. All local, no network.</summary>
public static class Native
{
    // ---- Known folders ----
    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    public static string? DownloadsFolder()
    {
        if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    // ---- 8.3 short names -> long names ----
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint size);

    public static string? LongPath(string path)
    {
        var sb = new StringBuilder(1024);
        uint n = GetLongPathNameW(path, sb, (uint)sb.Capacity);
        if (n == 0) return null;
        if (n > sb.Capacity)
        {
            sb = new StringBuilder((int)n);
            n = GetLongPathNameW(path, sb, (uint)sb.Capacity);
            if (n == 0) return null;
        }
        return sb.ToString();
    }

    // ---- Recycle Bin ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x0004;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_ALLOWUNDO = 0x0040;
    const ushort FOF_NOERRORUI = 0x0400;
    const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>
    /// Sends files to the Recycle Bin. If a file is too big for the bin, Windows shows its own
    /// "delete permanently?" warning instead of silently destroying it. Callers check
    /// File.Exists afterwards to learn which files actually moved.
    /// </summary>
    public static int Recycle(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return 0;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING),
        };
        return SHFileOperationW(ref op);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBinW(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? rootPath, uint flags);

    public static (long Bytes, long Items) QueryRecycleBin(string driveRoot)
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        int hr = SHQueryRecycleBinW(driveRoot, ref info);
        return hr == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Permanently empties the Recycle Bin for one drive. Returns the HRESULT (0 = ok).</summary>
    public static int EmptyRecycleBin(string driveRoot)
    {
        const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;
        return SHEmptyRecycleBinW(IntPtr.Zero, driveRoot, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
    }
}
