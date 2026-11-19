using System.Diagnostics;
using Byterake.Core;
using Xunit.Abstractions;

namespace Byterake.Tests;

public class CleanerTests
{
    readonly ITestOutputHelper _out;
    public CleanerTests(ITestOutputHelper output) => _out = output;

    // Logs live inside the temp tree; each test disposes its log so the tree can be deleted.
    static ActivityLog Log(TempTree t) => new(Path.Combine(t.Root, "_logs"));

    static CleanJob Job(string root, bool removeEmpty, params string[] files) => new()
    {
        Label = "test",
        AllowedRoots = new[] { root },
        Files = files.Select(f => new CleanFile(f, new FileInfo(f).Exists ? new FileInfo(f).Length : 0, DateTime.Now)).ToList(),
        RemoveEmptyFolders = removeEmpty,
    };

    static CleanJob Job(string root, params string[] files) => Job(root, true, files);

    [Fact]
    public void Permanent_delete_removes_listed_files_and_empty_folders()
    {
        using var t = new TempTree();
        using var log = Log(t);
        string area = t.Dir("area");
        string a = t.File(@"area\a.tmp", 100);
        string b = t.File(@"area\sub\b.tmp", 200);
        string keep = t.File(@"area\keep.tmp", 50);

        var r = Cleaner.Run(new[] { Job(area, a, b) }, CleanMode.Permanent, log);

        Assert.Equal(2, r.FilesRemoved);
        Assert.Equal(300, r.BytesRemoved);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(keep), "files not in the confirmed list must stay");
        Assert.False(Directory.Exists(Path.Combine(area, "sub")));
        Assert.True(Directory.Exists(area), "the target folder itself must stay");
    }

    [Fact]
    public void Files_outside_the_allowed_roots_are_refused()
    {
        using var t = new TempTree();
        using var log = Log(t);
        string area = t.Dir("area");
        string outside = t.File(@"elsewhere\x.tmp", 10);

        var r = Cleaner.Run(new[] { Job(area, outside) }, CleanMode.Permanent, log);

        Assert.True(File.Exists(outside));
        Assert.Single(r.Refused);
    }

    [Fact]
    public void Protected_paths_are_refused_even_if_listed()
    {
        using var t = new TempTree();
        using var log = Log(t);
        string sys = Environment.ExpandEnvironmentVariables(@"%WINDIR%\System32\notepad.exe");
        var job = new CleanJob
        {
            Label = "evil",
            AllowedRoots = new[] { Environment.ExpandEnvironmentVariables("%WINDIR%") },
            Files = new[] { new CleanFile(sys, 1, DateTime.Now) },
        };

        var r = Cleaner.Run(new[] { job }, CleanMode.RecycleBin, log);

        Assert.Equal(0, r.FilesRemoved);
        Assert.Single(r.Refused);
        Assert.True(File.Exists(sys));
    }

    [Fact]
    public void Files_reached_through_a_junction_are_refused()
    {
        using var t = new TempTree();
        using var log = Log(t);
        using var outside = new TempTree();
        string precious = outside.File("precious.doc", 10);
        string area = t.Dir("area");
        Assert.True(t.Junction(@"area\link", outside.Root));

        var r = Cleaner.Run(new[] { Job(area, Path.Combine(area, "link", "precious.doc")) }, CleanMode.RecycleBin, log);

        Assert.True(File.Exists(precious));
        Assert.Single(r.Refused);
    }

    [Fact]
    public void Locked_files_are_skipped_and_reported_and_the_rest_continue()
    {
        using var t = new TempTree();
        using var log = Log(t);
        string area = t.Dir("area");
        string locked = t.File(@"area\locked.tmp", 10);
        string free = t.File(@"area\free.tmp", 20);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = Cleaner.Run(new[] { Job(area, locked, free) }, CleanMode.Permanent, log);

            Assert.Equal(1, r.FilesRemoved);
            Assert.Single(r.Failed);
            Assert.Equal(locked, r.Failed[0].Path, ignoreCase: true);
        }
        Assert.False(File.Exists(free));
    }

    [Fact]
    public void Recycle_mode_moves_files_out_and_logs_them()
    {
        using var t = new TempTree();
        using var log = Log(t);
        string area = t.Dir("area");
        string a = t.File(@"area\recycle-me.tmp", 10);

        var r = Cleaner.Run(new[] { Job(area, a) }, CleanMode.RecycleBin, log);

        Assert.Equal(1, r.FilesRemoved);
        Assert.False(File.Exists(a));
        Assert.Contains(log.Recent, l => l.Contains("Recycle Bin: ") && l.Contains("recycle-me.tmp"));
        Assert.True(File.Exists(log.FilePath));
    }

    [Fact]
    public void Recycle_mode_moves_a_fully_confirmed_folder_in_one_step()
    {
        using var t = new TempTree();
        using var log = Log(t);
        string area = t.Dir("area");
        var files = Enumerable.Range(0, 20).Select(i => t.File($@"area\cache\{i}.bin", 100)).ToArray();

        var r = Cleaner.Run(new[] { Job(area, files) }, CleanMode.RecycleBin, log);

        Assert.Equal(20, r.FilesRemoved);
        Assert.Equal(2000, r.BytesRemoved);
        Assert.False(Directory.Exists(Path.Combine(area, "cache")));
        Assert.True(Directory.Exists(area), "the target folder itself must stay");
        Assert.Contains(log.Recent, l => l.Contains("Recycle Bin: folder") && l.Contains("(20 of 20 files)"));
    }

    [Fact]
    public void A_folder_with_an_unconfirmed_file_is_not_moved_whole()
    {
        using var t = new TempTree();
        using var log = Log(t);
        string area = t.Dir("area");
        string a = t.File(@"area\mixed\a.tmp", 10);
        string b = t.File(@"area\mixed\b.tmp", 10);
        string newcomer = t.File(@"area\mixed\arrived-after-confirm.tmp", 10);

        var r = Cleaner.Run(new[] { Job(area, a, b) }, CleanMode.RecycleBin, log);

        Assert.Equal(2, r.FilesRemoved);
        Assert.True(File.Exists(newcomer), "a file the user never confirmed must stay");
        Assert.DoesNotContain(log.Recent, l => l.Contains("Recycle Bin: folder"));
    }

    [Fact]
    public void A_folder_containing_a_junction_is_not_moved_whole()
    {
        using var t = new TempTree();
        using var log = Log(t);
        using var outside = new TempTree();
        string precious = outside.File("precious.doc", 10);
        string area = t.Dir("area");
        string a = t.File(@"area\cache\a.tmp", 10);
        Assert.True(t.Junction(@"area\cache\link", outside.Root));

        var r = Cleaner.Run(new[] { Job(area, a) }, CleanMode.RecycleBin, log);

        Assert.Equal(1, r.FilesRemoved);
        Assert.True(File.Exists(precious));
        Assert.True(Directory.Exists(Path.Combine(area, "cache", "link")), "the junction must stay where it is");
    }

    [Fact]
    public void A_locked_file_inside_a_whole_folder_is_skipped_and_the_rest_still_go()
    {
        using var t = new TempTree();
        using var log = Log(t);
        string area = t.Dir("area");
        var files = Enumerable.Range(0, 10).Select(i => t.File($@"area\cache\{i}.bin", 10)).ToArray();

        using (new FileStream(files[3], FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = Cleaner.Run(new[] { Job(area, files) }, CleanMode.RecycleBin, log);

            Assert.Equal(9, r.FilesRemoved);
            Assert.Single(r.Failed);
            Assert.True(File.Exists(files[3]));
        }
    }

    [Fact]
    public void Folder_mode_is_much_faster_than_file_by_file()
    {
        using var t = new TempTree();
        using var log = Log(t);
        string area = t.Dir("area");
        var files = new List<string>();
        for (int d = 0; d < 20; d++)
            for (int i = 0; i < 100; i++)
                files.Add(t.File($@"area\cache{d}\f{i}.bin", 64));

        var sw = Stopwatch.StartNew();
        var r = Cleaner.Run(new[] { Job(area, files.ToArray()) }, CleanMode.RecycleBin, log);
        sw.Stop();

        _out.WriteLine($"2,000 files in 20 folders recycled in {sw.Elapsed.TotalSeconds:0.00} s");
        Assert.Equal(2000, r.FilesRemoved);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Files_that_vanished_are_skipped_quietly()
    {
        using var t = new TempTree();
        using var log = Log(t);
        string area = t.Dir("area");
        var r = Cleaner.Run(new[] { Job(area, Path.Combine(area, "gone.tmp")) }, CleanMode.Permanent, log);
        Assert.Equal(0, r.FilesRemoved);
        Assert.Empty(r.Failed);
        Assert.Empty(r.Refused);
    }
}
