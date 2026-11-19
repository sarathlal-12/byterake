using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Byterake.Core;

public enum Risk { Safe, Review, Never }

public enum TargetKind { Contents, RecycleBin, Downloads, LargeFiles, WindowsTool }

public sealed class CatalogTarget
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Risk Risk { get; set; } = Risk.Review;
    public bool Admin { get; set; }
    public TargetKind Kind { get; set; } = TargetKind.Contents;
    public List<string> Paths { get; set; } = new();
    public List<string> Patterns { get; set; } = new() { "*" };
    public bool Recursive { get; set; } = true;
    public List<string> Extensions { get; set; } = new();
    public long MinSizeMB { get; set; }
    public string Estimate { get; set; } = "";
    public string? Warning { get; set; }

    /// <summary>Review items and per-file lists are never pre-ticked.</summary>
    public bool TickedByDefault => Risk == Risk.Safe && Kind is TargetKind.Contents or TargetKind.RecycleBin;
}

public sealed class Catalog
{
    public int Version { get; set; } = 1;
    public List<CatalogTarget> Targets { get; set; } = new();

    /// <summary>Problems found while loading (bad entries are dropped, not fatal).</summary>
    [JsonIgnore] public List<string> Warnings { get; } = new();
    [JsonIgnore] public string Source { get; private set; } = "";

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static Catalog Parse(string json, string source = "text")
    {
        var cat = JsonSerializer.Deserialize<Catalog>(json, Options) ?? new Catalog();
        cat.Source = source;
        cat.Validate();
        return cat;
    }

    /// <summary>Loads catalog.json next to the program if present, otherwise the built-in copy.</summary>
    public static Catalog Load(string? folder = null)
    {
        folder ??= AppContext.BaseDirectory;
        string file = Path.Combine(folder, "catalog.json");
        if (File.Exists(file))
        {
            try { return Parse(File.ReadAllText(file), file); }
            catch (Exception ex)
            {
                var fallback = LoadBuiltIn();
                fallback.Warnings.Insert(0, $"Could not read {file} ({ex.Message}). Using the built-in catalog instead.");
                return fallback;
            }
        }
        return LoadBuiltIn();
    }

    public static Catalog LoadBuiltIn()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Byterake.catalog.json")
            ?? throw new InvalidOperationException("Built-in catalog is missing from the build.");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd(), "built-in");
    }

    void Validate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<CatalogTarget>();
        foreach (var t in Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name))
            {
                Warnings.Add("Skipped a target with no id or name.");
                continue;
            }
            if (!seen.Add(t.Id))
            {
                Warnings.Add($"Skipped duplicate target id '{t.Id}'.");
                continue;
            }
            if (t.Risk == Risk.Never)
            {
                Warnings.Add($"'{t.Name}' is marked Never and is not offered.");
                continue;
            }
            if (t.Kind == TargetKind.Contents && t.Paths.Count == 0)
            {
                Warnings.Add($"Skipped '{t.Name}': it lists no paths.");
                continue;
            }
            if (t.Patterns.Count == 0) t.Patterns.Add("*");
            kept.Add(t);
        }
        Targets = kept;
    }
}
