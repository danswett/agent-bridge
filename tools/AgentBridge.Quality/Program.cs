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
        var root = options.GetValueOrDefault("repo") ?? ChangedFiles.RepoRoot(Directory.GetCurrentDirectory());
        var catalog = Catalog.Load(Path.Combine(root, "quality", "catalog.yml"));
        var graph = ProjectGraph.Load(root);

        switch (args[0])
        {
            case "select":
                {
                    var ring = int.Parse(options.GetValueOrDefault("ring") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                    var platform = options.GetValueOrDefault("platform") switch
                    {
                        null or "any" => null,
                        "this" => Selector.ThisPlatform(),
                        var p => p,
                    };
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

    static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string? pending = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (pending is not null)
                    options[pending] = "";
                pending = arg[2..];
                if (pending is "explain")
                {
                    options[pending] = "";
                    pending = null;
                }
            }
            else if (pending is not null)
            {
                // Repeated --files and --suite accumulate, so a shell can pass one per argument.
                options[pending] = options.TryGetValue(pending, out var existing) && existing.Length > 0 ? existing + "," + arg : arg;
                pending = null;
            }
            else
                throw new ArgumentException($"Unexpected argument '{arg}'.");
        }
        if (pending is not null)
            options[pending] = "";
        return options;
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
            selection.Files,
            selection.Unclaimed,
            Matrix = selection.Matrix,
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
                s.Suite.SecuritySensitive,
                Reasons = s.Reasons.Select(r => new { r.Rule, r.Files }),
            }),
            Skipped = selection.Skipped.Select(s => new { s.Suite.Id, s.Why }),
        }, Json);

    internal static void Explain(Selection selection, string source, TextWriter output)
    {
        output.WriteLine($"Ring {selection.Ring}{(selection.Platform is null ? ", every platform" : $", on {selection.Platform}")}. " +
            $"{selection.Files.Count} changed file(s) from {source}. Mode: {selection.Mode}.");
        if (selection.Unclaimed.Count > 0)
            output.WriteLine($"No rule claims {Files(selection.Unclaimed)}, so the whole ring runs.");
        if (selection.Mode == "docs-only")
            output.WriteLine("Every file is in no_test_paths, so nothing but the docs checks runs.");
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
                var first = true;
                foreach (var reason in s.Reasons)
                {
                    var why = reason.Files.Count == 0 ? reason.Rule : $"{reason.Rule}: {Files(reason.Files)}";
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
