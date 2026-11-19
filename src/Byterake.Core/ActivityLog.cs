namespace Byterake.Core;

/// <summary>
/// Plain-text log of everything Byterake scans, deletes and skips. One file per day in
/// %LOCALAPPDATA%\Byterake\Logs. Nothing is ever sent anywhere.
/// </summary>
public sealed class ActivityLog : IDisposable
{
    readonly object _gate = new();
    readonly List<string> _recent = new();
    const int RecentMax = 2000;
    StreamWriter? _writer;

    public ActivityLog(string? folder = null)
    {
        Folder = folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Byterake", "Logs");
        Directory.CreateDirectory(Folder);
        FilePath = Path.Combine(Folder, $"byterake-{DateTime.Now:yyyy-MM-dd}.log");
    }

    public string Folder { get; }
    public string FilePath { get; }

    /// <summary>Raised on the writing thread after each line.</summary>
    public event Action<string>? LineWritten;

    public IReadOnlyList<string> Recent { get { lock (_gate) return _recent.ToList(); } }

    public void Info(string message) => Write("INFO", message);
    public void Scan(string message) => Write("SCAN", message);
    public void Deleted(string path, long size, string how) => Write("DELETE", $"{how}: {path} ({Units.Bytes(size)})");
    public void Skipped(string path, string reason) => Write("SKIP", $"{path} - {reason}");
    public void Refused(string path, string reason) => Write("REFUSE", $"{path} - {reason}");
    public void Error(string message) => Write("ERROR", message);

    public void Write(string kind, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {kind,-7} {message}";
        lock (_gate)
        {
            _recent.Add(line);
            if (_recent.Count > RecentMax) _recent.RemoveRange(0, _recent.Count - RecentMax);
            try
            {
                // Kept open (others can still read it); every line is flushed straight to disk.
                _writer ??= new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true,
                };
                _writer.WriteLine(line);
            }
            catch
            {
                // The log must never stop a clean. Drop the writer so the next line tries again.
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
        LineWritten?.Invoke(line);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
