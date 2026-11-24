using System.Text.Json;

namespace AgentBridge.Quality;

internal enum ShardRun
{
    /// <summary>Nothing of this project runs in this shard.</summary>
    None,
    /// <summary>The whole project runs, with no filter.</summary>
    All,
    /// <summary>The project runs with <see cref="ShardChoice.Filter"/>.</summary>
    Filtered,
}

internal sealed record ShardChoice(ShardRun Run, string? Filter = null);

/// <summary>
/// <c>quality/test-shards.json</c> (S4, #4104): what each test shard job runs. Shards 1 to N-1 LIST their classes, per project; shard N is the
/// <b>remainder</b> and runs everything not listed, by a negated filter, so a class that is new, renamed or found by no scan lands there and is never
/// dropped, with no discovery step. Small projects are listed whole in a shard; a project listed nowhere runs whole in the remainder.
/// What the file must keep true, checked when it is read: no class in two lists (nor two classes whose terms match each other's tests, which would run
/// twice), and every filter under <see cref="MostFilterCharacters"/> so the command line stays far from Windows' 32 K limit.
/// </summary>
internal sealed class ShardPlanFile
{
    public const int MostFilterCharacters = 24000;

    readonly Dictionary<string, List<string>[]> projects = new(StringComparer.Ordinal);
    readonly List<string>[] whole;

    ShardPlanFile(int shards) => (Shards, whole) = (shards, NewLists(shards));

    public int Shards { get; }

    static List<string>[] NewLists(int shards) => [.. Enumerable.Range(0, Math.Max(shards - 1, 0)).Select(_ => new List<string>())];

    public static ShardPlanFile Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false }); }
        catch (JsonException e) { throw new ArgumentException($"The shard plan is not valid JSON: {e.Message}", nameof(json)); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("shards", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var shards) || shards < 1)
                throw new ArgumentException("The shard plan needs \"shards\": a whole number of at least 1.", nameof(json));
            var plan = new ShardPlanFile(shards);
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "shards": break;
                    case "projects":
                        foreach (var project in property.Value.EnumerateObject()) { plan.projects[project.Name] = plan.Lists(project.Value, $"project {project.Name}"); }
                        break;
                    case "whole":
                        var lists = plan.Lists(property.Value, "whole");
                        for (var i = 0; i < lists.Length; i++) { plan.whole[i].AddRange(lists[i]); }
                        break;
                    default: throw new ArgumentException($"The shard plan has an unknown property \"{property.Name}\".", nameof(json));
                }
            }
            plan.Validate();
            return plan;
        }
    }

    // {"1": [...], "2": [...]}: shards 1 to N-1 only; N is the remainder and is never listed.
    List<string>[] Lists(JsonElement element, string where)
    {
        var lists = NewLists(Shards);
        foreach (var entry in element.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out var shard) || shard < 1 || shard > Shards - 1)
                throw new ArgumentException($"In {where}, \"{entry.Name}\" is not a shard from 1 to {Shards - 1}: shard {Shards} is the remainder and lists nothing.");
            foreach (var name in entry.Value.EnumerateArray()) { lists[shard - 1].Add(name.GetString() ?? throw new ArgumentException($"In {where}, a name is not text.")); }
        }
        return lists;
    }

    void Validate()
    {
        foreach (var (project, lists) in projects)
        {
            var owner = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < lists.Length; i++)
            {
                foreach (var name in lists[i])
                {
                    ShardPlanner.RefuseUnfilterable(name);
                    if (!owner.TryAdd(name, i + 1)) { throw new ArgumentException($"In {project}, class {name} is listed in shards {owner[name]} and {i + 1}."); }
                }
            }
            // "X." also matches "FakeX.": two such classes in different shards would both run in both.
            foreach (var x in owner)
            {
                foreach (var y in owner.Where(y => y.Value != x.Value && (y.Key + ".").Contains(x.Key + ".", StringComparison.Ordinal)))
                    throw new ArgumentException($"In {project}, class {y.Key} (shard {y.Value}) matches the tests of {x.Key} (shard {x.Value}) and would run twice.");
            }
        }
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < whole.Length; i++)
        {
            foreach (var name in whole[i])
            {
                if (!owners.TryAdd(name, i + 1)) { throw new ArgumentException($"Project {name} is listed whole in shards {owners[name]} and {i + 1}."); }
                if (projects.ContainsKey(name)) { throw new ArgumentException($"Project {name} is both listed by class and listed whole."); }
            }
        }
        // Every filter, here: a plan that would break a shard job's command line is refused when it is read.
        foreach (var project in projects.Keys)
        {
            for (var shard = 1; shard <= Shards; shard++)
            {
                if (For(project, shard).Filter is { Length: > MostFilterCharacters } filter)
                    throw new ArgumentException($"The filter for {project} in shard {shard} is {filter.Length} characters, over the {MostFilterCharacters} budget.");
            }
        }
    }

    /// <summary>The shards that would run a test, by VSTest's containment rule: exactly one for every test of a known class, or the plan is wrong.</summary>
    public IEnumerable<int> RunningShards(string project, string testFullName)
    {
        for (var shard = 1; shard <= Shards; shard++)
        {
            var choice = For(project, shard);
            var runs = choice.Run switch
            {
                ShardRun.All => true,
                ShardRun.None => false,
                _ when choice.Filter!.StartsWith("FullyQualifiedName!~", StringComparison.Ordinal) => choice.Filter.Split('&').All(t => !testFullName.Contains(t["FullyQualifiedName!~".Length..], StringComparison.Ordinal)),
                _ => choice.Filter.Split('|').Any(t => testFullName.Contains(t["FullyQualifiedName~".Length..], StringComparison.Ordinal)),
            };
            if (runs) { yield return shard; }
        }
    }

    public ShardChoice For(string project, int shard)
    {
        if (shard < 1 || shard > Shards) { throw new ArgumentException($"Shard {shard} is not from 1 to {Shards}."); }
        if (projects.TryGetValue(project, out var lists))
        {
            if (shard < Shards)
                return lists[shard - 1].Count == 0 ? new(ShardRun.None) : new(ShardRun.Filtered, string.Join('|', lists[shard - 1].Order(StringComparer.Ordinal).Select(c => $"FullyQualifiedName~{c}.")));
            var listed = lists.SelectMany(l => l).Order(StringComparer.Ordinal).ToList();
            return listed.Count == 0 ? new(ShardRun.All) : new(ShardRun.Filtered, string.Join('&', listed.Select(c => $"FullyQualifiedName!~{c}.")));
        }
        var home = Array.FindIndex(whole, l => l.Contains(project, StringComparer.Ordinal));
        return home >= 0 ? new(home + 1 == shard ? ShardRun.All : ShardRun.None) : new(shard == Shards ? ShardRun.All : ShardRun.None);
    }
}
