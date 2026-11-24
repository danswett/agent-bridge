namespace AgentBridge.Quality;

/// <summary>
/// The product job's test shards (S4, #4104): which test classes each shard runs. A shard is a VSTest filter of
/// <c>FullyQualifiedName~Class.</c> terms, one per class. Two things must hold or a test is dropped or run twice: every class is in exactly one
/// shard, and a term matches only its own shard's tests. A term "X." also matches "FakeX." (containment), so classes like that are one unit and travel together.
/// </summary>
internal sealed record ShardPlan(IReadOnlyList<IReadOnlyList<string>> Shards)
{
    /// <summary>The <c>dotnet test --filter</c> for one shard: sorted terms joined by <c>|</c>, empty for a shard with no classes.</summary>
    public string Filter(int index) =>
        string.Join('|', Shards[index].Order(StringComparer.Ordinal).Select(c => $"FullyQualifiedName~{c}."));

    /// <summary>The shards whose filter would run a test: how VSTest's containment test sees it. Exactly one for every test of a planned class.</summary>
    public IEnumerable<int> ShardsMatching(string testFullName)
    {
        for (var i = 0; i < Shards.Count; i++)
        {
            if (Shards[i].Any(c => testFullName.Contains(c + ".", StringComparison.Ordinal))) { yield return i; }
        }
    }
}

internal static class ShardPlanner
{
    // What the filter language reads as syntax. A class name holding one is refused: a quietly misread term would drop its tests.
    static readonly char[] FilterSyntax = ['(', ')', '&', '|', '=', '!', '~', '\\'];

    /// <summary>Refuses a class name the filter language would misread (it would drop that class's tests quietly).</summary>
    public static void RefuseUnfilterable(string name)
    {
        if (name.Length == 0 || name.IndexOfAny(FilterSyntax) >= 0 || name.Any(char.IsWhiteSpace))
            throw new ArgumentException($"Test class name '{name}' cannot be a filter term.", nameof(name));
    }

    /// <summary>
    /// A pure function of the discovered classes and their recorded seconds, so every shard job computes the same plan. A class with no recorded
    /// time weighs the median of those that have one, so a new class is spread rather than piled onto a shard. Longest unit first, onto the lightest shard.
    /// </summary>
    public static ShardPlan Plan(IEnumerable<string> classes, IReadOnlyDictionary<string, double> seconds, int shards)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shards, 1);
        var names = classes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var name in names) { RefuseUnfilterable(name); }

        var known = names.Where(n => seconds.TryGetValue(n, out var s) && double.IsFinite(s) && s > 0).Select(n => seconds[n]).Order().ToList();
        var fallback = known.Count == 0 ? 1.0 : known[known.Count / 2];
        double Weight(string name) => seconds.TryGetValue(name, out var s) && double.IsFinite(s) && s > 0 ? s : fallback;

        // Classes whose term matches another class's tests are one unit.
        var parent = names.ToDictionary(n => n, n => n, StringComparer.Ordinal);
        string Find(string x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        foreach (var x in names)
        {
            foreach (var y in names)
            {
                if (x != y && (y + ".").Contains(x + ".", StringComparison.Ordinal))
                {
                    var (a, b) = (Find(x), Find(y));
                    if (a != b) { parent[string.CompareOrdinal(a, b) < 0 ? b : a] = string.CompareOrdinal(a, b) < 0 ? a : b; }
                }
            }
        }
        var units = names.GroupBy(Find, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Classes: g.ToList(), Seconds: g.Sum(Weight)))
            .OrderByDescending(u => u.Seconds).ThenBy(u => u.Name, StringComparer.Ordinal).ToList();

        var bins = Enumerable.Range(0, shards).Select(_ => new List<string>()).ToArray();
        var load = new double[shards];
        foreach (var unit in units)
        {
            var lightest = 0;
            for (var i = 1; i < shards; i++) { if (load[i] < load[lightest]) { lightest = i; } }
            bins[lightest].AddRange(unit.Classes);
            load[lightest] += unit.Seconds;
        }
        return new ShardPlan([.. bins.Select(b => (IReadOnlyList<string>)[.. b.Order(StringComparer.Ordinal)])]);
    }
}
