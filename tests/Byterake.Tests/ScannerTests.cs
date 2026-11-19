using System.Security.AccessControl;
using System.Security.Principal;
using Byterake.Core;

namespace Byterake.Tests;

public class ScannerTests
{
    [Fact]
    public void Sizes_add_up_through_nested_folders()
    {
        using var t = new TempTree();
        t.File("a.bin", 1000);
        t.File(@"sub\b.bin", 2000);
        t.File(@"sub\deeper\c.bin", 3000);
        t.File(@"other\d.bin", 500);
        t.Dir("empty");

        var r = DiskScanner.Scan(t.Root);

        Assert.Equal(6500, r.Root.Size);
        Assert.Equal(4, r.Files);
        Assert.Equal(4, r.Root.FileCount);
        var sub = r.Root.Children.Single(c => c.Name == "sub");
        Assert.Equal(5000, sub.Size);
        Assert.Equal(3000, sub.Children.Single().Size);
        Assert.Equal(0, r.Root.Children.Single(c => c.Name == "empty").Size);
        Assert.False(r.Cancelled);
    }

    [Fact]
    public void Children_are_sorted_largest_first()
    {
        using var t = new TempTree();
        t.File(@"small\x.bin", 10);
        t.File(@"big\x.bin", 5000);
        t.File(@"mid\x.bin", 300);

        var r = DiskScanner.Scan(t.Root);

        Assert.Equal(new[] { "big", "mid", "small" }, r.Root.Children.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Largest_and_large_files_are_reported()
    {
        using var t = new TempTree();
        t.File("tiny.bin", 10);
        t.File(@"x\huge.bin", 4096);
        t.File("medium.bin", 2048);

        var r = DiskScanner.Scan(t.Root, largeFileThreshold: 2048, topFiles: 2);

        Assert.Equal(new[] { "huge.bin", "medium.bin" }, r.LargestFiles.Select(f => Path.GetFileName(f.Path)).ToArray());
        Assert.Equal(2, r.LargeFiles.Count);
        Assert.Equal(4096, r.LargeFiles[0].Size);
    }

    [Fact]
    public void Junctions_are_not_followed()
    {
        using var t = new TempTree();
        using var outside = new TempTree();
        outside.File("outside.bin", 9999);
        t.File("inside.bin", 100);
        Assert.True(t.Junction("link", outside.Root), "could not create a test junction");

        var r = DiskScanner.Scan(t.Root);

        Assert.Equal(100, r.Root.Size);
        Assert.Equal(1, r.LinksSkipped);
        Assert.DoesNotContain(r.Root.Children, c => c.Name == "link");
    }

    [Fact]
    public void Junction_loop_does_not_hang()
    {
        using var t = new TempTree();
        t.File(@"a\f.bin", 10);
        Assert.True(t.Junction(@"a\loop", t.Root));

        var r = DiskScanner.Scan(t.Root);

        Assert.Equal(10, r.Root.Size);
    }

    [Fact]
    public void Unreadable_folder_is_logged_and_skipped()
    {
        using var t = new TempTree();
        t.File("ok.bin", 50);
        string locked = t.Dir("locked");
        t.File(@"locked\secret.bin", 777);

        var me = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var di = new DirectoryInfo(locked);
        var sec = di.GetAccessControl();
        sec.AddAccessRule(rule);
        di.SetAccessControl(sec);
        try
        {
            var r = DiskScanner.Scan(t.Root);

            Assert.Equal(50, r.Root.Size);
            Assert.Contains(r.Skipped, s => s.Path.Equals(locked, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(r.Root.Children.Single(c => c.Name == "locked").SkipReason);
        }
        finally
        {
            sec.RemoveAccessRule(rule);
            di.SetAccessControl(sec);
        }
    }

    [Fact]
    public void Cancel_stops_the_scan()
    {
        using var t = new TempTree();
        for (int i = 0; i < 20; i++) t.File($@"d{i}\f.bin", 10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = DiskScanner.Scan(t.Root, ct: cts.Token);

        Assert.True(r.Cancelled);
    }

    [Fact]
    public void Units_format_like_explorer()
    {
        Assert.Equal("512 B", Units.Bytes(512));
        Assert.Equal("1.5 KB", Units.Bytes(1536));
        Assert.Equal("2.0 GB", Units.Bytes(2 * Units.GB));
    }
}
