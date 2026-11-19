using Byterake.Core;

namespace Byterake.Tests;

public class ResolverTests
{
    [Fact]
    public void Wildcard_folder_matches_every_profile()
    {
        using var t = new TempTree();
        t.File(@"User Data\Default\Cache\a", 100);
        t.File(@"User Data\Profile 1\Cache\b", 200);
        t.File(@"User Data\Default\Bookmarks", 999);

        var target = new CatalogTarget { Id = "x", Name = "x", Paths = { t.Root + @"\User Data\*\Cache" } };
        var m = TargetResolver.MeasureContents(target);

        Assert.Equal(2, m.Roots.Count);
        Assert.Equal(300, m.Bytes);
        Assert.DoesNotContain(m.Files, f => f.Path.EndsWith("Bookmarks"));
    }

    [Fact]
    public void Patterns_and_non_recursive_are_respected()
    {
        using var t = new TempTree();
        t.File("thumbcache_1.db", 10);
        t.File("thumbcache_2.db", 20);
        t.File("other.db", 40);
        t.File(@"sub\thumbcache_3.db", 80);

        var target = new CatalogTarget { Id = "x", Name = "x", Paths = { t.Root }, Patterns = { "thumbcache_*.db" }, Recursive = false };
        target.Patterns.Remove("*");
        var m = TargetResolver.MeasureContents(target);

        Assert.Equal(30, m.Bytes);
    }

    [Fact]
    public void Junctions_inside_a_target_are_not_followed()
    {
        using var t = new TempTree();
        using var outside = new TempTree();
        outside.File("precious.doc", 5000);
        t.File("junk.tmp", 10);
        Assert.True(t.Junction("link", outside.Root));

        var m = TargetResolver.MeasureContents(new CatalogTarget { Id = "x", Name = "x", Paths = { t.Root } });

        Assert.Equal(10, m.Bytes);
        Assert.Contains(m.Refused, r => r.Path.EndsWith("link"));
    }

    [Fact]
    public void Missing_paths_and_unset_variables_are_skipped_quietly()
    {
        var target = new CatalogTarget { Id = "x", Name = "x", Paths = { @"C:\definitely\not\here", @"%BYTERAKE_UNSET%\x" } };
        var m = TargetResolver.MeasureContents(target);

        Assert.Empty(m.Roots);
        Assert.Single(m.Notes);
    }

    [Fact]
    public void A_catalog_entry_pointing_at_a_protected_folder_is_refused()
    {
        var target = new CatalogTarget { Id = "bad", Name = "bad", Paths = { @"%WINDIR%\System32" } };
        var m = TargetResolver.MeasureContents(target);

        Assert.Empty(m.Roots);
        Assert.Empty(m.Files);
        Assert.Single(m.Refused);
    }

    [Fact]
    public void Downloads_lists_only_big_installers()
    {
        using var t = new TempTree();
        t.File("setup.exe", 3 * 1024 * 1024);
        t.File("small.exe", 1024);
        t.File("movie.mp4", 3 * 1024 * 1024);
        t.File(@"nested\big.ZIP", 4 * 1024 * 1024);

        var target = new CatalogTarget { Id = "d", Name = "d", Extensions = { ".exe", ".msi", ".zip" }, MinSizeMB = 2 };
        var m = TargetResolver.MeasureDownloads(target, t.Root);

        Assert.Equal(new[] { "big.ZIP", "setup.exe" }, m.Files.Select(f => Path.GetFileName(f.Path)).ToArray());
    }
}
