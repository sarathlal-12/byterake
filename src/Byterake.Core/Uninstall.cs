using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Byterake.Core;

public sealed record InstalledProgram(
    string Name,
    string Publisher,
    string Version,
    long SizeBytes,
    DateTime? InstallDate,
    string UninstallCommand,
    string Source);

/// <summary>
/// Reads installed programs from the Uninstall registry keys (read-only) and starts a program's
/// own uninstaller. Byterake never deletes program folders itself.
/// </summary>
public static class Uninstall
{
    const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledProgram> ListPrograms()
    {
        var list = new List<InstalledProgram>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, "All users", list);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, "All users (32-bit)", list);
        Read(RegistryHive.CurrentUser, RegistryView.Registry64, "Current user", list);
        return list
            .GroupBy(p => (p.Name.ToLowerInvariant(), p.Version))
            .Select(g => g.First())
            .OrderByDescending(p => p.SizeBytes)
            .ToList();
    }

    static void Read(RegistryHive hive, RegistryView view, string source, List<InstalledProgram> list)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(KeyPath);
            if (root == null) return;
            foreach (var sub in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(sub);
                    if (k == null) continue;
                    string? name = k.GetValue("DisplayName") as string;
                    string? cmd = k.GetValue("UninstallString") as string;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cmd)) continue;
                    if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (k.GetValue("ParentKeyName") != null) continue; // updates/patches of another product
                    string release = k.GetValue("ReleaseType") as string ?? "";
                    if (release is "Update" or "Hotfix" or "Security Update") continue;

                    long size = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;
                    DateTime? date = null;
                    if (k.GetValue("InstallDate") is string d &&
                        DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        date = parsed;

                    list.Add(new InstalledProgram(
                        name.Trim(),
                        (k.GetValue("Publisher") as string ?? "").Trim(),
                        (k.GetValue("DisplayVersion") as string ?? "").Trim(),
                        size, date, cmd.Trim(), source));
                }
                catch { /* one bad key shouldn't hide the rest */ }
            }
        }
        catch { /* hive not readable */ }
    }

    /// <summary>
    /// Splits an UninstallString into program and arguments. Returns null when it can't be
    /// understood with confidence (Byterake then refuses to run it).
    /// </summary>
    public static (string Exe, string Args)? ParseCommand(string command)
    {
        string c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.Length == 0) return null;
        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            if (end < 0) return null;
            return (c[1..end], c[(end + 1)..].Trim());
        }
        int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe < 0) return null;
        return (c[..(exe + 4)], c[(exe + 4)..].Trim());
    }

    /// <summary>Starts the program's own uninstaller. Returns an error message, or null on success.</summary>
    public static string? Launch(InstalledProgram p, ActivityLog log)
    {
        var parsed = ParseCommand(p.UninstallCommand);
        if (parsed == null)
        {
            log.Skipped(p.Name, $"Uninstall command not understood: {p.UninstallCommand}");
            return "Byterake could not understand this program's uninstall command, so it did not run it. Use Settings > Apps instead.";
        }
        var (exe, args) = parsed.Value;
        bool isBareName = !exe.Contains('\\');
        if (!isBareName && !File.Exists(exe))
        {
            log.Skipped(p.Name, $"Uninstaller not found: {exe}");
            return $"The uninstaller was not found:\n{exe}";
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            log.Info($"Started uninstaller for {p.Name}: {exe} {args}");
            return null;
        }
        catch (Exception ex)
        {
            log.Error($"Uninstaller for {p.Name} failed to start: {ex.Message}");
            return ex.Message;
        }
    }
}
