using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Byterake.App;

public partial class App : Application
{
    public static bool IsDark { get; private set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General) Dispatcher.Invoke(ApplyTheme);
        };

        var win = new MainWindow(StartArgs.Parse(e.Args));
        MainWindow = win;
        win.Show();
    }

    /// <summary>Follows Windows' "app mode" (Settings > Personalization > Colors).</summary>
    public static void ApplyTheme()
    {
        bool dark = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light) dark = light == 0;
        }
        catch { }
        // For testing a theme without changing Windows settings: set BYTERAKE_THEME=light or dark.
        var forced = Environment.GetEnvironmentVariable("BYTERAKE_THEME");
        if (string.Equals(forced, "light", StringComparison.OrdinalIgnoreCase)) dark = false;
        if (string.Equals(forced, "dark", StringComparison.OrdinalIgnoreCase)) dark = true;
        IsDark = dark;
        var dicts = Current.Resources.MergedDictionaries;
        dicts[0] = new ResourceDictionary { Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative) };
        foreach (Window w in Current.Windows) TitleBar(w);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Matches the window's title bar to the theme.</summary>
    public static void TitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}

/// <summary>Arguments used when Byterake restarts itself as admin: --drive C --select id1,id2</summary>
public sealed class StartArgs
{
    public string? Drive { get; private set; }
    public HashSet<string> Select { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static StartArgs Parse(string[] args)
    {
        var s = new StartArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--drive" && args[i + 1].Length >= 1 && char.IsLetter(args[i + 1][0]))
                s.Drive = char.ToUpperInvariant(args[i + 1][0]) + @":\";
            if (args[i] == "--select")
                foreach (var id in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries)) s.Select.Add(id.Trim());
        }
        return s;
    }
}
