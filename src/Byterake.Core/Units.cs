namespace Byterake.Core;

public static class Units
{
    public const long MB = 1024L * 1024;
    public const long GB = 1024L * 1024 * 1024;

    /// <summary>Formats a byte count the way Windows Explorer does (binary units, one decimal).</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.0} {units[i]}";
    }
}
