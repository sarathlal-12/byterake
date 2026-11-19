using Byterake.Core;

namespace Byterake.Tests;

public class PathSafetyTests
{
    static void Blocked(string path) =>
        Assert.False(PathSafety.Check(path).Allowed, $"should be blocked: {path}");

    static void Allowed(string path) =>
        Assert.True(PathSafety.Check(path).Allowed, $"should be allowed: {path} ({PathSafety.Check(path).Reason})");

    static string Win => Environment.GetEnvironmentVariable("SystemRoot")!;
    static string Profile => Environment.GetEnvironmentVariable("USERPROFILE")!;

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\System32\")]
    [InlineData(@"C:\Windows\System32\\\")]
    [InlineData(@"c:\windows\system32\drivers\etc\hosts")]
    [InlineData(@"C:\WINDOWS\SYSTEM32\kernel32.dll")]
    [InlineData(@"C:/Windows/System32/notepad.exe")]
    [InlineData(@"\\?\C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\Temp\..\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32.")]
    [InlineData(@"C:\Windows\System32 ")]
    [InlineData(@"%WINDIR%\System32")]
    [InlineData(@"%SystemRoot%\system32\config\SAM")]
    [InlineData(@"%windir%\SYSTEM32\")]
    [InlineData(@"C:\Windows\WinSxS")]
    [InlineData(@"c:\windows\winsxs\amd64_foo\bar.dll")]
    [InlineData(@"%WINDIR%\WinSxS\")]
    [InlineData(@"C:\Windows\SysWOW64\x.dll")]
    public void Windows_system_folders_are_blocked(string path) => Blocked(path);

    [Theory]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\")]
    [InlineData(@"c:\program files\Some App\app.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData(@"C:\PROGRAM FILES (X86)\")]
    [InlineData(@"%ProgramFiles%\x")]
    [InlineData(@"%ProgramFiles(x86)%\x")]
    [InlineData(@"%ProgramW6432%")]
    [InlineData(@"C:/Program Files/x/y.dll")]
    public void Program_files_are_blocked(string path) => Blocked(path);

    [Theory]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"C:\swapfile.sys")]
    [InlineData(@"c:\PAGEFILE.SYS")]
    [InlineData(@"D:\pagefile.sys")]
    [InlineData(@"%SystemDrive%\hiberfil.sys")]
    public void Windows_memory_files_are_blocked(string path) => Blocked(path);

    [Theory]
    [InlineData(@"%USERPROFILE%\Documents")]
    [InlineData(@"%USERPROFILE%\Documents\report.docx")]
    [InlineData(@"%userprofile%\DESKTOP\a.txt")]
    [InlineData(@"%USERPROFILE%\Pictures\")]
    [InlineData(@"%USERPROFILE%\Videos\movie.mp4")]
    [InlineData(@"%USERPROFILE%\Music\song.mp3")]
    [InlineData(@"%USERPROFILE%\OneDrive\Documents\x.txt")]
    public void User_folders_are_blocked(string path) => Blocked(path);

    [Fact]
    public void User_folders_from_windows_are_blocked_wherever_they_live()
    {
        foreach (var f in new[] {
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic })
        {
            string p = Environment.GetFolderPath(f);
            if (string.IsNullOrEmpty(p)) continue;
            Blocked(p);
            Blocked(p + "\\");
            Blocked(p.ToUpperInvariant() + @"\file.txt");
            Blocked(p.Replace('\\', '/') + "/file.txt");
        }
    }

    [Theory]
    [InlineData(@"C:\code\project\.git")]
    [InlineData(@"C:\code\project\.git\")]
    [InlineData(@"C:\code\project\.git\objects\ab\cdef")]
    [InlineData(@"C:\code\project\.GIT\config")]
    [InlineData(@"%TEMP%\repo\.git\HEAD")]
    [InlineData(@"C:\code\project\.github\workflows\ci.yml")]
    [InlineData(@"C:\code\project\.gitignore")]
    public void Git_paths_are_blocked(string path) => Blocked(path);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"D:\")]
    [InlineData(@"%SystemRoot%")]
    [InlineData(@"C:\Windows\")]
    [InlineData(@"%USERPROFILE%")]
    [InlineData(@"%LOCALAPPDATA%\")]
    [InlineData(@"%APPDATA%")]
    [InlineData(@"%TEMP%")]
    [InlineData(@"C:\Users")]
    public void Drive_roots_and_top_level_folders_are_blocked(string path) => Blocked(path);

    [Theory]
    [InlineData(@"relative\path.txt")]
    [InlineData(@"..\..\Windows\System32")]
    [InlineData(@"%BYTERAKE_NOT_SET_VAR%\x")]
    [InlineData(@"")]
    [InlineData(@"   ")]
    [InlineData(@"\Windows\System32")]
    public void Ambiguous_paths_are_blocked(string path) => Blocked(path);

    [Fact]
    public void Environment_variable_pointing_into_a_protected_folder_is_blocked()
    {
        Environment.SetEnvironmentVariable("BYTERAKE_TEST_SNEAKY", Win + @"\System32");
        try
        {
            Blocked(@"%BYTERAKE_TEST_SNEAKY%\cmd.exe");
            Blocked(@"%BYTERAKE_TEST_SNEAKY%");
        }
        finally { Environment.SetEnvironmentVariable("BYTERAKE_TEST_SNEAKY", null); }
    }

    [Fact]
    public void Short_8dot3_names_are_expanded_before_checking()
    {
        string? shortPf = null;
        // Only testable when 8.3 names are on for this drive.
        var dir = new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        foreach (var candidate in new[] { "PROGRA~1", "PROGRA~2" })
        {
            string p = Path.Combine(Path.GetPathRoot(dir.FullName)!, candidate);
            if (Directory.Exists(p)) { shortPf = p; break; }
        }
        if (shortPf == null) return;
        Blocked(shortPf + @"\anything.dll");
    }

    [Theory]
    [InlineData(@"%TEMP%\some-app\cache.tmp")]
    [InlineData(@"%LOCALAPPDATA%\Google\Chrome\User Data\Default\Cache\Cache_Data\f_000001")]
    [InlineData(@"C:\Windows\Temp\setup.log")]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download\abc\update.cab")]
    [InlineData(@"%USERPROFILE%\Downloads\installer.exe")]
    [InlineData(@"C:\Windows\System32Backup\x")]
    [InlineData(@"C:\Program FilesX\x")]
    [InlineData(@"C:\data\gitstuff\file.txt")]
    [InlineData(@"C:\data\my.git.notes\file.txt")]
    public void Ordinary_cleanup_paths_are_allowed(string path) => Allowed(path);

    [Fact]
    public void Junctions_are_refused_on_disk()
    {
        using var t = new TempTree();
        using var target = new TempTree();
        Assert.True(t.Junction("link", target.Root));
        var v = PathSafety.CheckOnDisk(Path.Combine(t.Root, "link"));
        Assert.False(v.Allowed);
        Assert.Contains("junction", v.Reason);
    }

    [Fact]
    public void System_attribute_files_are_refused_on_disk()
    {
        using var t = new TempTree();
        string f = t.File("sys.dat", 10);
        File.SetAttributes(f, FileAttributes.System);
        try
        {
            Assert.False(PathSafety.CheckOnDisk(f).Allowed);
        }
        finally { File.SetAttributes(f, FileAttributes.Normal); }
    }

    [Fact]
    public void Plain_temp_file_passes_on_disk()
    {
        using var t = new TempTree();
        string f = t.File("plain.tmp", 10);
        Assert.True(PathSafety.CheckOnDisk(f).Allowed);
    }

    [Fact]
    public void Lock_icon_rule_marks_only_fully_protected_folders()
    {
        Assert.True(PathSafety.ContentsProtected(@"C:\Program Files"));
        Assert.True(PathSafety.ContentsProtected(Win + @"\System32"));
        Assert.True(PathSafety.ContentsProtected(Profile + @"\Documents"));
        Assert.False(PathSafety.ContentsProtected(@"C:\Users"));
        Assert.False(PathSafety.ContentsProtected(Win));
        Assert.False(PathSafety.ContentsProtected(Profile + @"\Downloads"));
    }

    [Fact]
    public void Normalize_gives_one_canonical_form()
    {
        string a = PathSafety.Normalize(@"%WINDIR%\Temp\");
        string b = PathSafety.Normalize(@"C:/Windows/Temp");
        string c = PathSafety.Normalize(@"\\?\C:\Windows\Temp\\");
        Assert.Equal(a, b, ignoreCase: true);
        Assert.Equal(a, c, ignoreCase: true);
        Assert.False(a.EndsWith('\\'));
    }
}
