using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Byterake.Core;

public static class SystemTools
{
    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Starts a new copy of Byterake with admin rights. Windows shows its permission prompt.
    /// Returns false if the user said no.
    /// </summary>
    public static bool RestartAsAdmin(string arguments)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find Byterake.exe.");
        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }

    /// <summary>Opens Windows' own Disk Cleanup for a drive (used for Windows.old).</summary>
    public static void OpenDiskCleanup(string driveRoot)
    {
        string letter = driveRoot.TrimEnd('\\');
        Process.Start(new ProcessStartInfo("cleanmgr.exe", $"/d {letter}") { UseShellExecute = true });
    }

    public static void OpenFolder(string folder)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
