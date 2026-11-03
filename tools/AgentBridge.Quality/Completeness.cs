using System.Xml.Linq;
using YamlDotNet.RepresentationModel;

namespace AgentBridge.Quality;

sealed record Problem(string Kind, string Message);

/// <summary>
/// Keeps the catalog honest: anything that tests or runs in CI must be in it, and everything it
/// names must exist. Without this the catalog rots silently and the selector stops selecting.
/// </summary>
static class Completeness
{
    public sealed record Inputs(
        IReadOnlyList<string> SolutionProjects,
        IReadOnlyDictionary<string, IReadOnlyList<string>> WorkflowJobs,
        IReadOnlyList<string> TrackedFiles);

    public static IReadOnlyList<Problem> Check(Catalog catalog, ProjectGraph graph, Inputs inputs)
    {
        var problems = new List<Problem>();
        var cataloged = catalog.Suites.Where(s => s.Project is not null).Select(s => s.Project!).ToHashSet(StringComparer.Ordinal);

        foreach (var folder in inputs.SolutionProjects)
            if (graph[folder]?.IsTestProject == true && !cataloged.Contains(folder))
                problems.Add(new("test-project", $"{folder} is a test project in AgentBridge.slnx, but no suite's project names it"));

        problems.AddRange(catalog.Validate(graph.CsprojCount));

        var covered = new HashSet<(string File, string Job)>();
        foreach (var suite in catalog.Suites.Where(s => !string.IsNullOrWhiteSpace(s.Where)))
            foreach (var (file, jobs) in ParseWhere(suite.Where!))
            {
                if (!inputs.WorkflowJobs.TryGetValue(file, out var actual))
                {
                    problems.Add(new("workflow-job", $"suite {suite.Id} says it runs in {file}, which isn't in .github/workflows"));
                    continue;
                }
                foreach (var job in jobs.Count == 0 ? actual : jobs)
                {
                    if (!actual.Contains(job, StringComparer.Ordinal))
                        problems.Add(new("workflow-job", $"suite {suite.Id} says it runs in {file} job {job}, which doesn't exist"));
                    covered.Add((file, job));
                }
            }
        foreach (var (file, jobs) in inputs.WorkflowJobs.OrderBy(w => w.Key, StringComparer.Ordinal))
            foreach (var job in jobs)
                if (!covered.Contains((file, job)))
                    problems.Add(new("workflow-job", $"{file} job {job} isn't named in any suite's where:"));

        var commands = catalog.Suites.Where(s => s.Command is not null).Select(s => s.Command!).ToList();
        foreach (var folder in PwaSpecFolders(inputs.TrackedFiles))
            if (!commands.Any(c => RunsFolder(c, folder.Path, folder.HasPlaywrightSpecs)))
                problems.Add(new("pwa-spec-folder", $"{folder.Path} holds tests, but no suite's command runs them"));

        var selector = new Selector(catalog, graph);
        foreach (var file in inputs.TrackedFiles)
            if (!selector.IsClaimed(file))
                problems.Add(new("unclaimed", $"{file} is claimed by no rule, so a change to it runs the full ring"));

        return problems;
    }

    public static IReadOnlyList<Problem> CheckRepo(string root, Catalog catalog, ProjectGraph graph)
    {
        var slnx = Path.Combine(root, "AgentBridge.slnx");
        var workflows = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var workflowDir = Path.Combine(root, ".github", "workflows");
        if (Directory.Exists(workflowDir))
            foreach (var file in Directory.EnumerateFiles(workflowDir).Where(f => f.EndsWith(".yml", StringComparison.Ordinal) || f.EndsWith(".yaml", StringComparison.Ordinal)))
                workflows[Path.GetFileName(file)] = WorkflowJobs(File.ReadAllText(file));
        return Check(catalog, graph, new Inputs(
            File.Exists(slnx) ? SolutionProjects(File.ReadAllText(slnx)) : [],
            workflows,
            ChangedFiles.Tracked(root)));
    }

    public static IReadOnlyList<string> SolutionProjects(string slnxXml) =>
        XDocument.Parse(slnxXml).Descendants("Project")
            .Select(p => p.Attribute("Path")?.Value)
            .Where(p => p is not null)
            .Select(p => RepoPath.Folder(RepoPath.Normalize(p!)))
            .ToList();

    public static IReadOnlyList<string> WorkflowJobs(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return [];
        return root.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobs) && jobs is YamlMappingNode map
            ? map.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value!).ToList()
            : [];
    }

    /// <summary>
    /// "ci.yml product" names one job, "ci.yml spikes, s2-noise-interop" several, and
    /// "ha-contract-drift.yml" alone the whole workflow. Several files are separated by ';'.
    /// </summary>
    public static IEnumerable<(string File, IReadOnlyList<string> Jobs)> ParseWhere(string where)
    {
        foreach (var part in where.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = part.IndexOf(' ');
            var file = space < 0 ? part : part[..space];
            var jobs = space < 0 ? [] : part[(space + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            yield return (file, jobs);
        }
    }

    static IEnumerable<(string Path, bool HasPlaywrightSpecs)> PwaSpecFolders(IEnumerable<string> tracked) =>
        tracked.Where(f => f.StartsWith("pwa/test/", StringComparison.Ordinal)
                && (f.EndsWith(".spec.mjs", StringComparison.Ordinal) || f.EndsWith(".test.mjs", StringComparison.Ordinal)))
            .GroupBy(RepoPath.Folder)
            .Select(g => (g.Key, g.Any(f => f.EndsWith(".spec.mjs", StringComparison.Ordinal))))
            .OrderBy(x => x.Key, StringComparer.Ordinal);

    /// <summary>
    /// Whether a suite's command runs a test folder: it names the folder, alone or with one file
    /// glob after it (after its own "cd"), or it's a bare "playwright test", which runs every spec
    /// under pwa/test (playwright.config.mjs). "test/unit/*.test.mjs" names test/unit, not test.
    /// </summary>
    internal static bool RunsFolder(string command, string folder, bool hasPlaywrightSpecs)
    {
        var relative = folder;
        var trimmed = command.TrimStart();
        if (trimmed.StartsWith("cd ", StringComparison.Ordinal))
        {
            var end = trimmed.IndexOf("&&", StringComparison.Ordinal);
            var dir = RepoPath.Normalize(trimmed[3..(end < 0 ? trimmed.Length : end)]);
            if (folder.StartsWith(dir + "/", StringComparison.Ordinal))
                relative = folder[(dir.Length + 1)..];
        }
        // "playwright test" is a subcommand, not the folder "test".
        var words = command.Replace("playwright test", "playwright", StringComparison.Ordinal);
        if (System.Text.RegularExpressions.Regex.IsMatch(words, $"""(^|[\s"']){System.Text.RegularExpressions.Regex.Escape(relative)}(/[^/\s"']*[*.][^/\s"']*)?([\s"']|$)"""))
            return true;
        if (!hasPlaywrightSpecs)
            return false;
        var at = command.IndexOf("playwright test", StringComparison.Ordinal);
        if (at < 0)
            return false;
        var rest = command[(at + "playwright test".Length)..].TrimStart();
        return rest.Length == 0 || rest.StartsWith('#') || rest.StartsWith('-');
    }
}
