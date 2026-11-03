using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentBridge.Quality;

/// <summary>quality/catalog.yml: every suite, and the paths that select all or none of them.</summary>
sealed class Catalog
{
    public List<string> GlobalTriggers { get; set; } = [];
    public List<string> NoTestPaths { get; set; } = [];
    public List<Suite> Suites { get; set; } = [];

    public static Catalog Parse(string yaml)
    {
        // Unknown fields (baseline today, others later) are ignored so the catalog can grow
        // without breaking the selector.
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var catalog = deserializer.Deserialize<Catalog?>(yaml) ?? new Catalog();
        catalog.GlobalTriggers ??= [];
        catalog.NoTestPaths ??= [];
        catalog.Suites ??= [];
        foreach (var suite in catalog.Suites)
        {
            if (string.IsNullOrWhiteSpace(suite.Id))
                throw new FormatException("A suite in the catalog has no id.");
            suite.Platforms ??= [];
            suite.Triggers ??= [];
            suite.Project = suite.Project is null ? null : RepoPath.Normalize(suite.Project);
        }
        var duplicate = catalog.Suites.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new FormatException($"The catalog lists suite '{duplicate.Key}' more than once.");
        return catalog;
    }

    public static Catalog Load(string path) => Parse(File.ReadAllText(path));
}

sealed class Suite
{
    public string Id { get; set; } = "";
    public string? Kind { get; set; }
    public int Ring { get; set; }
    public string Select { get; set; } = "affected";
    public bool SecuritySensitive { get; set; }
    public List<string> Platforms { get; set; } = [];
    public string? Runner { get; set; }
    public string? Project { get; set; }
    public List<string> Triggers { get; set; } = [];
    public string? Command { get; set; }
    public string? Where { get; set; }
    public string? Status { get; set; }

    public bool IsSentinel => string.Equals(Select, "always", StringComparison.OrdinalIgnoreCase);
}
