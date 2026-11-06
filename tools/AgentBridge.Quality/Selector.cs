using System.Runtime.InteropServices;

namespace AgentBridge.Quality;

sealed record Reason(string Rule, IReadOnlyList<string> Files);

sealed record SelectedSuite(Suite Suite, IReadOnlyList<Reason> Reasons);

sealed record SkippedSuite(Suite Suite, string Why);

sealed record Selection(
    int Ring,
    string? Platform,
    string Mode,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Unclaimed,
    IReadOnlyList<SelectedSuite> Suites,
    IReadOnlyList<SkippedSuite> Skipped)
{
    /// <summary>
    /// Each platform, with the selected suites that run on it: the OS matrix. With a platform
    /// filter it's this machine's run, and what was left out is in <see cref="Skipped"/>; CI
    /// plans with no filter, so its matrix is complete.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Matrix =>
        Suites.SelectMany(s => s.Suite.Platforms.Select(p => (Platform: p, s.Suite.Id)))
            .GroupBy(x => x.Platform)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Id).ToList(), StringComparer.Ordinal);
}

/// <summary>
/// Chooses suites from what changed (quality/README.md §4). The rule that matters most is the
/// fail-safe: a path no rule claims runs the whole ring, because "nothing to test" must never be
/// decided by mistake.
/// </summary>
sealed class Selector(Catalog catalog, ProjectGraph graph)
{
    public const string Sentinel = "always (sentinel)";
    public const string FailSafe = "unclaimed path (fail-safe: the full ring)";
    public const string AskedFor = "asked for by name";

    // Paths match as the Windows build reads them, whatever their case (ProjectGraph.PathComparer):
    // only the rules that add suites fold case. no_test_paths take suites away, so they stay exact
    // and an alias of one is selected for by the other rules instead.
    readonly List<Glob> globalTriggers = catalog.GlobalTriggers.Select(g => new Glob(g, ignoreCase: true)).ToList();
    readonly List<Glob> noTestPaths = catalog.NoTestPaths.Select(g => new Glob(g)).ToList();
    readonly List<(Suite Suite, List<Glob> Triggers)> triggers =
        catalog.Suites.Select(s => (s, s.Triggers.Select(t => new Glob(t, ignoreCase: true)).ToList())).ToList();
    // In catalog order, so reasons come out in the same order on every run.
    readonly List<Suite> projectSuites = catalog.Suites.Where(s => s.Project is not null).ToList();
    readonly List<Suite> buildSuites = catalog.Suites.Where(s => s.Builds is not null).ToList();

    public Selection Select(IEnumerable<string> changedFiles, int ring, string? platform = null, IReadOnlyCollection<string>? named = null)
    {
        var files = changedFiles.Select(RepoPath.Normalize).Where(f => f.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var reasons = new Dictionary<string, List<(string Rule, string? File)>>(StringComparer.Ordinal);
        var inRing = catalog.Suites.Where(s => s.Ring <= ring).ToList();
        var unclaimed = new List<string>();
        string mode;

        void Add(Suite suite, string rule, string? file)
        {
            if (!reasons.TryGetValue(suite.Id, out var list))
                reasons[suite.Id] = list = [];
            list.Add((rule, file));
        }

        if (named is { Count: > 0 })
        {
            mode = "named";
            foreach (var id in named)
            {
                var suite = catalog.Suites.FirstOrDefault(s => s.Id == id)
                    ?? throw new ArgumentException($"The catalog has no suite '{id}'.");
                Add(suite, AskedFor, null);
            }
        }
        else if (ring >= 2)
        {
            // Ring 2 and later exist to catch what selection missed, so they never select.
            mode = "everything";
            foreach (var suite in inRing)
                Add(suite, $"ring {ring} runs everything", null);
        }
        else
        {
            var docsOnly = files.Count > 0;
            var full = new List<(string Rule, string File)>();
            foreach (var file in files)
            {
                var global = globalTriggers.FirstOrDefault(g => g.IsMatch(file));
                if (global is not null)
                {
                    docsOnly = false;
                    full.Add(($"global_triggers {global.Pattern}", file));
                    continue;
                }
                if (noTestPaths.Any(g => g.IsMatch(file)))
                {
                    // A file with no tests of its own still reaches suites that name it in
                    // triggers (docs-links' '**/*.md'), but never the graph, the sentinels or
                    // the fail-safe. What a docs-only change runs is the catalog's choice.
                    MatchTriggers(file, ring, Add);
                    continue;
                }
                docsOnly = false;
                if (!Claim(file, ring, Add))
                {
                    unclaimed.Add(file);
                    full.Add((FailSafe, file));
                }
            }

            mode = docsOnly ? "docs-only" : full.Count > 0 ? "full" : "affected";
            foreach (var (rule, file) in full)
                foreach (var suite in inRing)
                    Add(suite, rule, file);
            // A change only to no_test_paths runs what their triggers select and nothing else,
            // not even the sentinels (§4.4).
            if (!docsOnly)
                foreach (var suite in inRing.Where(s => s.IsSentinel))
                    Add(suite, Sentinel, null);
        }

        var selected = new List<SelectedSuite>();
        var skipped = new List<SkippedSuite>();
        foreach (var suite in catalog.Suites)
        {
            if (!reasons.TryGetValue(suite.Id, out var list))
                continue;
            var grouped = list.GroupBy(r => r.Rule)
                .Select(g => new Reason(g.Key, g.Where(r => r.File is not null).Select(r => r.File!).Distinct(StringComparer.Ordinal).ToList()))
                .ToList();
            if (platform is not null && !RunsOn(suite, platform))
                skipped.Add(new SkippedSuite(suite, $"runs on {string.Join(", ", suite.Platforms)}, not {platform}"));
            else
                selected.Add(new SelectedSuite(suite, grouped));
        }
        return new Selection(ring, platform, mode, files, unclaimed, selected, skipped);
    }

    /// <summary>
    /// Adds the suites a file reaches through triggers and the project graph, in any ring, and says
    /// whether any rule claimed it. A file claimed only by a later ring's suite is still claimed:
    /// it's known, just not tested this early.
    /// </summary>
    bool Claim(string file, int ring, Action<Suite, string, string?> add)
    {
        var claimed = MatchTriggers(file, ring, add);
        foreach (var owner in graph.OwnersOf(file))
        {
            var reachers = graph.ReachersOf(owner.Folder);
            foreach (var suite in projectSuites.Where(s => reachers.Contains(s.Project!)))
            {
                claimed = true;
                if (suite.Ring <= ring)
                    add(suite, ProjectGraph.PathComparer.Equals(suite.Project, owner.Folder) ? $"project {owner.Folder}" : $"project {suite.Project} reaches {owner.Folder}", file);
            }
            // A suite that ships a project is reached the same way: an embedded resource linked in
            // from docs/protocol/examples once changed the app without selecting its package (#487).
            foreach (var suite in buildSuites.Where(s => reachers.Contains(s.Builds!)))
            {
                claimed = true;
                if (suite.Ring <= ring)
                    add(suite, ProjectGraph.PathComparer.Equals(suite.Builds, owner.Folder) ? $"builds {owner.Folder}" : $"builds {suite.Builds}, which reaches {owner.Folder}", file);
            }
        }
        return claimed;
    }

    public bool IsClaimed(string file) =>
        globalTriggers.Any(g => g.IsMatch(file)) || noTestPaths.Any(g => g.IsMatch(file)) || Claim(file, -1, (_, _, _) => { });

    bool MatchTriggers(string file, int ring, Action<Suite, string, string?> add)
    {
        var matched = false;
        foreach (var (suite, globs) in triggers)
        {
            var match = globs.FirstOrDefault(g => g.IsMatch(file));
            if (match is null)
                continue;
            matched = true;
            if (suite.Ring <= ring)
                add(suite, $"triggers {match.Pattern}", file);
        }
        return matched;
    }

    /// <summary>
    /// Browser suites run wherever Node and the browsers do, so on any desktop OS locally.
    /// </summary>
    public static bool RunsOn(Suite suite, string platform) =>
        suite.Platforms.Count == 0
        || suite.Platforms.Contains(platform, StringComparer.Ordinal)
        || (suite.Platforms.Contains("browser", StringComparer.Ordinal)
            && (platform.StartsWith("win-", StringComparison.Ordinal) || platform.StartsWith("linux-", StringComparison.Ordinal) || platform.StartsWith("macos-", StringComparison.Ordinal)));

    public static string ThisPlatform()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return $"{os}-{arch}";
    }
}
