using Byterake.Core;

namespace Byterake.Tests;

public class CatalogTests
{
    [Fact]
    public void Built_in_catalog_loads_with_every_required_target()
    {
        var c = Catalog.LoadBuiltIn();

        Assert.Empty(c.Warnings);
        string[] required =
        {
            "user-temp", "windows-temp", "recycle-bin", "browser-chrome", "browser-edge", "browser-firefox",
            "thumbnails", "crash-user", "crash-system", "windows-update", "delivery-optimization",
            "dev-caches", "windows-old", "old-installers", "large-files",
        };
        foreach (var id in required) Assert.Contains(c.Targets, t => t.Id == id);
    }

    [Fact]
    public void Risk_and_admin_flags_match_the_spec()
    {
        var c = Catalog.LoadBuiltIn();
        CatalogTarget T(string id) => c.Targets.Single(t => t.Id == id);

        Assert.Equal(Risk.Safe, T("user-temp").Risk);
        Assert.True(T("windows-update").Admin);
        Assert.True(T("delivery-optimization").Admin);
        Assert.Equal(Risk.Review, T("windows-old").Risk);
        Assert.True(T("windows-old").Admin);
        Assert.NotNull(T("windows-old").Warning);
        Assert.Equal(50, T("old-installers").MinSizeMB);
        Assert.Equal(new[] { ".exe", ".msi", ".zip" }, T("old-installers").Extensions);
    }

    [Fact]
    public void Only_safe_targets_are_ticked_by_default()
    {
        var c = Catalog.LoadBuiltIn();
        foreach (var t in c.Targets)
        {
            if (t.Risk == Risk.Review) Assert.False(t.TickedByDefault, t.Id);
        }
        Assert.True(c.Targets.Single(t => t.Id == "user-temp").TickedByDefault);
        Assert.False(c.Targets.Single(t => t.Id == "large-files").TickedByDefault);
    }

    [Fact]
    public void Browser_targets_only_point_at_cache_folders()
    {
        var c = Catalog.LoadBuiltIn();
        foreach (var t in c.Targets.Where(t => t.Id.StartsWith("browser-")))
        foreach (var p in t.Paths)
        {
            string last = p.Split('\\').Last();
            Assert.Contains("cache", last.ToLowerInvariant().Replace("thumbnails", "cache"));
        }
    }

    [Fact]
    public void Never_targets_duplicates_and_empty_entries_are_dropped()
    {
        string json = """
        { "targets": [
          { "id": "a", "name": "A", "risk": "Safe", "paths": ["%TEMP%"] },
          { "id": "a", "name": "A again", "risk": "Safe", "paths": ["%TEMP%"] },
          { "id": "n", "name": "Nope", "risk": "Never", "paths": ["%TEMP%"] },
          { "id": "", "name": "No id" },
          { "id": "p", "name": "No paths", "risk": "Safe", "kind": "contents" },
        ] }
        """;
        var c = Catalog.Parse(json);

        Assert.Single(c.Targets);
        Assert.Equal(4, c.Warnings.Count);
    }

    [Fact]
    public void Bad_file_next_to_exe_falls_back_to_built_in()
    {
        using var t = new TempTree();
        File.WriteAllText(Path.Combine(t.Root, "catalog.json"), "{ not json");

        var c = Catalog.Load(t.Root);

        Assert.Equal("built-in", c.Source);
        Assert.Contains(c.Warnings, w => w.Contains("Could not read"));
    }
}
