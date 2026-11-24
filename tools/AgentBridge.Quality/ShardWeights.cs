using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace AgentBridge.Quality;

/// <summary>
/// S4 (#4104), piece 3: generates quality/test-shards.json OFFLINE from CI's TRX files (the <c>coverage-&lt;os&gt;</c> artifact): seconds per test class, per project,
/// through <see cref="ShardPlanner"/>. Bin N is not listed: it is the remainder, so it has tests today and takes whatever is new tomorrow. A project whose
/// total is under the threshold is listed whole in a shard instead of by class. Discovery by reflection or TRX happens here, never as a CI step.
/// </summary>
internal static class ShardWeights
{
    static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    /// <summary>Project -> class -> summed seconds of its tests, over every TRX under the directory.</summary>
    public static Dictionary<string, Dictionary<string, double>> Read(string directory)
    {
        if (!Directory.Exists(directory)) { throw new ArgumentException($"No such directory: {directory}"); }
        var weights = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.trx", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var doc = XDocument.Load(file);
            var classes = new Dictionary<string, (string Project, string Class)>(StringComparer.Ordinal);
            foreach (var unit in doc.Descendants(Ns + "UnitTest"))
            {
                var method = unit.Element(Ns + "TestMethod");
                var codeBase = (string?)method?.Attribute("codeBase") ?? (string?)unit.Attribute("storage");
                var className = (string?)method?.Attribute("className");
                if (codeBase is null || className is null || (string?)unit.Attribute("id") is not { } id) { continue; }
                classes[id] = (Path.GetFileNameWithoutExtension(codeBase.Replace('\\', '/').Split('/')[^1]), className);
            }
            foreach (var result in doc.Descendants(Ns + "UnitTestResult"))
            {
                if ((string?)result.Attribute("testId") is not { } id || !classes.TryGetValue(id, out var home)) { continue; }
                var seconds = TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var d) ? d.TotalSeconds : 0;
                var project = weights.TryGetValue(home.Project, out var p) ? p : weights[home.Project] = new(StringComparer.Ordinal);
                project[home.Class] = project.GetValueOrDefault(home.Class) + seconds;
            }
        }
        if (weights.Count == 0) { throw new ArgumentException($"No test results (*.trx) under {directory}."); }
        return weights;
    }

    public static string Generate(Dictionary<string, Dictionary<string, double>> weights, int shards, double wholeUnderSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shards, 2);
        var big = weights.Where(p => p.Value.Values.Sum() >= wholeUnderSeconds).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        var load = new double[shards];
        var byClass = new List<(string Project, string[][] Bins)>();
        foreach (var (project, classes) in big)
        {
            // The project's heaviest bin goes to the shard that is lightest so far, so the projects' totals even out, not only each project's own.
            var bins = ShardPlanner.Plan(classes.Keys, classes, shards).Shards.Select(s => s.ToArray()).OrderByDescending(b => b.Sum(c => classes[c])).ToArray();
            var shardsByLoad = Enumerable.Range(0, shards).OrderBy(i => load[i]).ThenBy(i => i).ToArray();
            var placed = new string[shards][];
            for (var k = 0; k < shards; k++) { placed[shardsByLoad[k]] = bins[k]; load[shardsByLoad[k]] += bins[k].Sum(c => classes[c]); }
            byClass.Add((project, placed));
        }
        // Small projects: heaviest first onto the lightest shard.
        var whole = Enumerable.Range(0, shards - 1).Select(_ => new List<string>()).ToArray();
        foreach (var (project, classes) in weights.Except(big).OrderByDescending(p => p.Value.Values.Sum()).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            var lightest = Array.IndexOf(load, load.Min());
            if (lightest < shards - 1) { whole[lightest].Add(project); }
            load[lightest] += classes.Values.Sum();
        }

        var text = new StringBuilder($"{{\n  \"shards\": {shards},\n  \"projects\": {{\n");
        text.AppendJoin(",\n", byClass.Select(p => $"    \"{p.Project}\": {{\n" + string.Join(",\n", p.Bins.Take(shards - 1).Select((bin, i) => $"      \"{i + 1}\": [{string.Join(", ", bin.Select(c => $"\"{c}\""))}]")) + "\n    }"));
        text.Append("\n  },\n  \"whole\": {\n");
        text.AppendJoin(",\n", whole.Select((l, i) => $"    \"{i + 1}\": [{string.Join(", ", l.Order(StringComparer.Ordinal).Select(p => $"\"{p}\""))}]"));
        text.Append("\n  }\n}\n");

        // Exact, per known class: a filter term is a containment match ("AnswerFlowTests." is inside "AgentAnswerFlowTests."), so every class we know is simulated
        // against the finished plan and must run in exactly one shard, never none (dropped) and never two. A plan that fails is not written.
        var plan = ShardPlanFile.Parse(text.ToString());
        foreach (var (project, classes) in weights)
        {
            foreach (var name in classes.Keys)
            {
                var running = plan.RunningShards(project, $"{name}.SomeTest").ToArray();
                if (running.Length != 1) { throw new InvalidOperationException($"{project}: class {name} would run in shards [{string.Join(", ", running)}], not exactly one."); }
            }
        }
        return text.ToString();
    }
}
