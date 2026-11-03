using System.Text;
using System.Text.Json;

namespace AgentBridge.Quality;

static class Program
{
    const string Usage = """
        agent-bridge-quality: test selection from quality/catalog.yml (quality/README.md §4).

          select [--ring N] [--base REF] [--head REF] [--files a,b] [--suite a,b]
                 [--platform this|any|<platform>] [--explain] [--repo DIR]
              The suites a change reaches, as JSON, or as a table with --explain. Without
              --files, the change is the diff from the merge base with --base (origin/main),
              plus uncommitted and untracked files unless --head is given.
          check [--repo DIR]
              Fails when a test project, workflow job or pwa test folder is missing from the
              catalog, a catalog project doesn't exist, or a tracked file is claimed by no rule.
          flakes --results DIR [--issues FILE] [--run-url URL] [--commit SHA] [--plan FILE]
                 [--summary FILE] [--dry-run]
              The flake hunt's reporter: classifies each test in DIR/<os>/<project>/run-<i>.trx
              as flaky, failing every run or passing, and plans the issues to open or comment
              on, given the open issues (gh issue list --json number,title,body,labels). Exits 1
              when a test failed every run on some OS, or a run broke outside any test.
        """;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static int Main(string[] args)
    {
        try
        {
            return Run(args, Console.Out);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or IOException or YamlDotNet.Core.YamlException)
        {
            Console.Error.WriteLine($"agent-bridge-quality: {e.Message}");
            return 2;
        }
    }

    internal static int Run(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        var options = ParseOptions(args.Skip(1));
        // The flake hunt's reporter reads test results, not the catalog, so it runs anywhere.
        if (args[0] == "flakes")
            return Flakes(options, output);
        var root = options.GetValueOrDefault("repo") ?? ChangedFiles.RepoRoot(Directory.GetCurrentDirectory());
        var catalog = Catalog.Load(Path.Combine(root, "quality", "catalog.yml"));
        var graph = ProjectGraph.Load(root);

        switch (args[0])
        {
            case "select":
                {
                    // A catalog that breaks its own schema would select wrongly, and silently.
                    var schema = catalog.Validate(graph.CsprojCount);
                    if (schema.Count > 0)
                        throw new FormatException("quality/catalog.yml breaks its schema; run check for the list. First: " + schema[0].Message);
                    var ring = ParseRing(options.GetValueOrDefault("ring"));
                    var platform = ParsePlatform(options.GetValueOrDefault("platform"));

                    var named = List(options.GetValueOrDefault("suite"));
                    var baseRef = options.GetValueOrDefault("base") ?? "origin/main";
                    var head = options.GetValueOrDefault("head");
                    IReadOnlyList<string> files;
                    string? mergeBase = null;
                    if (options.TryGetValue("files", out var list))
                        files = List(list);
                    else if (named.Count > 0)
                        files = [];
                    else
                        (files, mergeBase) = ChangedFiles.Since(root, baseRef, head);
                    var selection = new Selector(catalog, graph).Select(files, ring, platform, named);
                    var source = options.ContainsKey("files") ? "the files given"
                        : named.Count > 0 ? "the suites named"
                        : $"{baseRef}{(head is null ? " and the working tree" : $"..{head}")} (merge base {mergeBase?[..Math.Min(10, mergeBase.Length)]})";
                    if (options.ContainsKey("explain"))
                        Explain(selection, source, output);
                    else
                        output.WriteLine(ToJson(selection, source));
                    return 0;
                }
            case "check":
                {
                    var problems = Completeness.CheckRepo(root, catalog, graph);
                    foreach (var group in problems.GroupBy(p => p.Kind))
                    {
                        output.WriteLine($"{group.Key} ({group.Count()}):");
                        foreach (var problem in group)
                            output.WriteLine($"  {problem.Message}");
                    }
                    output.WriteLine(problems.Count == 0 ? "The catalog is complete." : $"{problems.Count} problem(s).");
                    return problems.Count == 0 ? 0 : 1;
                }
            default:
                throw new ArgumentException($"Unknown command '{args[0]}'.\n{Usage}");
        }
    }

    static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "explain", "dry-run" };
    static readonly HashSet<string> Valued = new(StringComparer.Ordinal) { "ring", "base", "head", "files", "suite", "platform", "repo", "results", "issues", "run-url", "commit", "plan", "summary" };

    /// <summary>
    /// The flake hunt's reporter (quality/README.md §5): classifies every test in the hunt's TRX
    /// files, writes the summary and the issues to open or comment on, and fails when a test
    /// failed every run on some OS or a run broke outside any test. It never touches GitHub;
    /// quality/publish-flakes.ps1 carries out the plan.
    /// </summary>
    static int Flakes(Dictionary<string, string> options, TextWriter output)
    {
        var results = options.GetValueOrDefault("results") ?? throw new ArgumentException("flakes needs --results DIR.");
        var hunt = FlakeHunt.Load(results);
        // No runs at all would read as a clean night.
        if (hunt.Runs.Count == 0)
            throw new InvalidOperationException($"{results} holds no runs.");
        var verdicts = hunt.Classify();
        var open = options.TryGetValue("issues", out var issues) ? OpenIssue.Parse(File.ReadAllText(issues)) : [];
        var context = new FlakeContext(options.GetValueOrDefault("run-url") ?? "(no run)", options.GetValueOrDefault("commit") ?? "(unknown commit)", DateTimeOffset.UtcNow);
        var plan = FlakeReport.Plan(verdicts, open, context);
        var summary = FlakeReport.Summary(hunt, verdicts, plan, options.ContainsKey("dry-run"));
        if (options.TryGetValue("plan", out var planFile))
            File.WriteAllText(planFile, JsonSerializer.Serialize(plan, Json));
        if (options.TryGetValue("summary", out var summaryFile))
            File.WriteAllText(summaryFile, summary);
        output.WriteLine(summary);
        return verdicts.Any(v => v.Kinds.Contains(Verdict.Failing)) || hunt.Broken.Count > 0 ? 1 : 0;
    }

    internal static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string? pending = null;
        foreach (var arg in args)
        {
            if (pending is not null)
            {
                if (arg.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"--{pending} needs a value.");
                // Repeated --files and --suite accumulate, so a shell can pass one per argument.
                options[pending] = options.TryGetValue(pending, out var existing) && existing.Length > 0 ? existing + "," + arg : arg;
                pending = null;
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal) && Flags.Contains(arg[2..]))
                options[arg[2..]] = "";
            else if (arg.StartsWith("--", StringComparison.Ordinal) && Valued.Contains(arg[2..]))
                pending = arg[2..];
            else
                throw new ArgumentException($"Unexpected argument '{arg}'.");
        }
        if (pending is not null)
            throw new ArgumentException($"--{pending} needs a value.");
        return options;
    }

    // A misspelled platform would match no suite, and every one would be skipped as if that were
    // the plan.
    internal static string? ParsePlatform(string? value) => value switch
    {
        null or "any" => null,
        "this" => Selector.ThisPlatform(),
        _ when Catalog.PlatformNames.Contains(value, StringComparer.Ordinal) => value,
        _ => throw new ArgumentException($"--platform {value} is not a platform: use any, this, or one of {string.Join(", ", Catalog.PlatformNames)}."),
    };
    // Out of range would select nothing at all, and look like a passing plan.
    internal static int ParseRing(string? value)
    {
        if (!int.TryParse(value ?? "0", System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ring) || ring is < 0 or > 4)
            throw new ArgumentException($"--ring {value} is not a ring: rings are 0 to 4.");
        return ring;
    }

    static List<string> List(string? value) =>
        value is null ? [] : value.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    internal static string ToJson(Selection selection, string source) =>
        JsonSerializer.Serialize(new
        {
            selection.Ring,
            selection.Platform,
            Source = source,
            selection.Mode,
            // The dotnet test filter for the ring as a whole, so CI's product job (a draft's fast
            // ring) leaves out what ring 0 does without a second copy of the categories.
            TestFilter = selection.Ring == 0 ? string.Join('&', RingCommand.Ring0Exclusions) : null,
            selection.Files,
            selection.Unclaimed,
            Matrix = selection.Matrix,
            Jobs = JobsOf(selection),
            Suites = selection.Suites.Select(s => new
            {
                s.Suite.Id,
                s.Suite.Kind,
                s.Suite.Ring,
                s.Suite.Runner,
                s.Suite.Status,
                s.Suite.Platforms,
                s.Suite.Project,
                s.Suite.Command,
                s.Suite.Where,
                // What run.ps1 runs: the command with this ring's test filter, if any.
                RunCommand = RingCommand.For(s.Suite.Command, selection.Ring),
                s.Suite.SecuritySensitive,
                Reasons = s.Reasons.Select(r => new { r.Rule, r.Files }),
            }),
            Skipped = selection.Skipped.Select(s => new { s.Suite.Id, s.Why }),
        }, Json);

    /// <summary>
    /// The workflow jobs the selected suites run in, by their where:, with the suites each one
    /// runs: CI's plan job skips any job not listed (quality/README.md §4.7). Read from the
    /// catalog, not kept in ci.yml as well, so moving a suite to another job moves its trigger with
    /// it. A where naming a whole workflow ("ha-contract-drift.yml") is listed as job "*".
    /// </summary>
    internal static SortedDictionary<string, SortedDictionary<string, List<string>>> JobsOf(Selection selection)
    {
        var jobs = new SortedDictionary<string, SortedDictionary<string, List<string>>>(StringComparer.Ordinal);
        foreach (var s in selection.Suites.Where(s => !string.IsNullOrWhiteSpace(s.Suite.Where)))
            foreach (var (file, names) in Completeness.ParseWhere(s.Suite.Where!))
            {
                if (!jobs.TryGetValue(file, out var byJob))
                    jobs[file] = byJob = new(StringComparer.Ordinal);
                foreach (var job in names.Count == 0 ? ["*"] : names)
                {
                    if (!byJob.TryGetValue(job, out var ids))
                        byJob[job] = ids = [];
                    if (!ids.Contains(s.Suite.Id))
                        ids.Add(s.Suite.Id);
                }
            }
        return jobs;
    }

    internal static void Explain(Selection selection, string source, TextWriter output)
    {
        output.WriteLine($"Ring {selection.Ring}{(selection.Platform is null ? ", every platform" : $", on {selection.Platform}")}. " +
            $"{selection.Files.Count} changed file(s) from {source}. Mode: {selection.Mode}.");
        // Global triggers and the fail-safe pull in every suite, so say so once rather than per row.
        static bool WholeRing(Reason r) => r.Rule.StartsWith("global_triggers ", StringComparison.Ordinal) || r.Rule == Selector.FailSafe;
        var wholeRing = selection.Suites.SelectMany(s => s.Reasons).Where(WholeRing).GroupBy(r => r.Rule)
            .Select(g => new Reason(g.Key, g.SelectMany(r => r.Files).Distinct(StringComparer.Ordinal).ToList())).ToList();
        foreach (var reason in wholeRing)
            output.WriteLine($"The whole ring runs, by {reason.Rule}: {Files(reason.Files)}");
        if (selection.Mode == "docs-only")
            output.WriteLine("Every file is in no_test_paths, so only suites whose triggers name them run, and no sentinels.");
        output.WriteLine();

        if (selection.Suites.Count == 0)
            output.WriteLine("No suites selected.");
        else
        {
            var width = Math.Max(5, selection.Suites.Max(s => s.Suite.Id.Length));
            output.WriteLine($"{"Suite".PadRight(width)}  Ring  {"Runner",-7}  {"Status",-7}  Why");
            foreach (var s in selection.Suites)
            {
                var prefix = $"{s.Suite.Id.PadRight(width)}  {s.Suite.Ring,-4}  {s.Suite.Runner,-7}  {s.Suite.Status,-7}  ";
                var whys = s.Reasons.Where(r => !WholeRing(r))
                    .Select(r => r.Files.Count == 0 ? r.Rule : $"{r.Rule}: {Files(r.Files)}")
                    .Prepend(s.Reasons.Any(WholeRing) ? "the whole ring (above)" : null)
                    .OfType<string>();
                var first = true;
                foreach (var why in whys)
                {
                    output.WriteLine((first ? prefix : new string(' ', prefix.Length)) + why);
                    first = false;
                }
            }
        }

        if (selection.Skipped.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Selected, but not on this platform:");
            var width = selection.Skipped.Max(s => s.Suite.Id.Length);
            foreach (var s in selection.Skipped)
                output.WriteLine($"  {s.Suite.Id.PadRight(width)}  {s.Why}");
        }

        if (selection.Suites.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("OS matrix:");
            foreach (var (platform, ids) in selection.Matrix)
                output.WriteLine($"  {platform}: {string.Join(", ", ids)}");
        }
    }

    static string Files(IReadOnlyList<string> files)
    {
        var sb = new StringBuilder(string.Join(", ", files.Take(3)));
        if (files.Count > 3)
            sb.Append($" (+{files.Count - 3} more)");
        return sb.ToString();
    }
}
