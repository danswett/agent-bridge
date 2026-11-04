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
            suite.Builds = suite.Builds is null ? null : RepoPath.Normalize(suite.Builds);
        }
        var duplicate = catalog.Suites.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new FormatException($"The catalog lists suite '{duplicate.Key}' more than once.");
        return catalog;
    }

    public static Catalog Load(string path) => Parse(File.ReadAllText(path));

    static readonly string[] Kinds = ["unit", "component", "process", "integration", "e2e", "live", "ui", "perf", "security", "a11y", "static", "packaging"];
    static readonly string[] Selects = ["affected", "always"];
    internal static readonly string[] PlatformNames = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "macos-arm64", "ios-sim", "ios-device", "browser"];
    static readonly string[] Runners = ["local", "hosted", "lab", "staging", "device"];
    static readonly string[] Statuses = ["exists", "partial", "planned"];
    // A person runs these from the command's instructions, so no workflow job runs them.
    static readonly string[] ManualRunners = ["lab", "staging", "device"];

    /// <summary>
    /// The schema in catalog.yml's header. <paramref name="csprojCount"/> counts the .csproj files
    /// directly in a project folder, which must be exactly one so "project" names one project.
    /// </summary>
    public IReadOnlyList<Problem> Validate(Func<string, int> csprojCount)
    {
        var problems = new List<Problem>();
        // A missing or misspelled suites key would otherwise pass, and select nothing, even by the fail-safe.
        if (Suites.Count == 0)
            problems.Add(new("schema", "the catalog lists no suites"));
        void Add(Suite suite, string message) => problems.Add(new("schema", $"suite {suite.Id} {message}"));
        void OneOf(Suite suite, string field, string? value, string[] allowed)
        {
            if (string.IsNullOrWhiteSpace(value))
                Add(suite, $"has no {field}");
            else if (!allowed.Contains(value, StringComparer.Ordinal))
                Add(suite, $"has {field} '{value}', not one of {string.Join(", ", allowed)}");
        }

        foreach (var suite in Suites)
        {
            OneOf(suite, "kind", suite.Kind, Kinds);
            if (suite.Ring is null)
                Add(suite, "has no ring");
            else if (suite.Ring is < 0 or > 4)
                Add(suite, $"has ring {suite.Ring}, not 0 to 4");
            OneOf(suite, "select", suite.Select, Selects);
            if (suite.Platforms.Count == 0)
                Add(suite, "has no platforms");
            foreach (var platform in suite.Platforms.Where(p => !PlatformNames.Contains(p, StringComparer.Ordinal)))
                Add(suite, $"has platform '{platform}', not one of {string.Join(", ", PlatformNames)}");
            OneOf(suite, "runner", suite.Runner, Runners);
            OneOf(suite, "status", suite.Status, Statuses);
            if (suite.Status == "exists")
            {
                if (string.IsNullOrWhiteSpace(suite.Command))
                    Add(suite, "exists but has no command");
                if (string.IsNullOrWhiteSpace(suite.Where) && !ManualRunners.Contains(suite.Runner, StringComparer.Ordinal))
                    Add(suite, $"exists on a {suite.Runner} runner but has no where");
            }
            if (suite.Select == "affected" && suite.Project is null && suite.Builds is null && suite.Triggers.Count == 0)
                Add(suite, "is select: affected with neither project nor triggers, so nothing can select it");
            foreach (var (field, folder) in new[] { ("project", suite.Project), ("builds", suite.Builds) })
            {
                if (folder is null)
                    continue;
                var count = csprojCount(folder);
                if (count != 1)
                    Add(suite, $"names {field} {folder}, which holds {(count == 0 ? "no" : count.ToString(System.Globalization.CultureInfo.InvariantCulture))} .csproj files, not one");
            }
        }
        return problems;
    }
}

sealed class Suite
{
    public string Id { get; set; } = "";
    public string? Kind { get; set; }
    public int? Ring { get; set; }
    public string? Select { get; set; }
    public bool SecuritySensitive { get; set; }
    public List<string> Platforms { get; set; } = [];
    public string? Runner { get; set; }
    public string? Project { get; set; }
    /// <summary>
    /// A project the suite builds and ships, not a test project (app-package's src/AgentBridge.App):
    /// whatever that project's build takes in, through ProjectReferences and linked files, selects it.
    /// </summary>
    public string? Builds { get; set; }
    public List<string> Triggers { get; set; } = [];
    public string? Command { get; set; }
    public string? Where { get; set; }
    public string? Status { get; set; }

    public bool IsSentinel => string.Equals(Select, "always", StringComparison.OrdinalIgnoreCase);
}
