using System.Diagnostics;

namespace Byterake.Tests;

/// <summary>A throwaway folder under %TEMP% that deletes itself after the test.</summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; }

    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "byterake-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string File(string relative, int bytes)
    {
        string p = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllBytes(p, new byte[bytes]);
        return p;
    }

    public string Dir(string relative)
    {
        string p = Path.Combine(Root, relative);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Creates a directory junction (no admin rights needed). Returns false if the OS refused.</summary>
    public bool Junction(string relative, string target)
    {
        string p = Path.Combine(Root, relative);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{p}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode == 0 && Directory.Exists(p);
    }

    public void Dispose()
    {
        try
        {
            // Remove junctions first without touching their targets.
            foreach (var d in Directory.EnumerateDirectories(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true }))
            {
                if ((System.IO.File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d, false);
            }
            Directory.Delete(Root, true);
        }
        catch { /* best effort */ }
    }
}
