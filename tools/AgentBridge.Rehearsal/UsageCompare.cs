using System.Text.Json;

namespace AgentBridge.Rehearsal;

// One governor/app pair for the ten-pair check in docs/design/agent-usage-read.md (#1405).
// Excluded: the vendor figure changed between the two reads, so the pair is retried and not counted.
internal sealed record UsagePair(
    string Agent, string Key,
    DateTimeOffset GovernorReadAt, double GovernorPercent, DateTimeOffset GovernorMeasuredAt,
    DateTimeOffset AppReadAt, double AppPercent, DateTimeOffset AppMeasuredAt,
    bool Excluded = false);

internal static class UsageCompare
{
    const int Needed = 10;
    const double MaxPercentGap = 1.0;
    static readonly TimeSpan MaxReadGap = TimeSpan.FromSeconds(60);
    static readonly TimeSpan MinSpacing = TimeSpan.FromMinutes(1);

    // The two allowances the governor takes from HA today. Codex's weekly window is the key
    // `secondary` while its label is Weekly (docs/design/agent-usage-read.md).
    static readonly (string Agent, string Key)[] Required = [("copilot", "plan"), ("codex", "secondary")];

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Outcome Evaluate(IReadOnlyList<UsagePair> pairs, TimeSpan readInterval)
    {
        var problems = new List<string>();
        var counted = new Dictionary<string, int>();
        foreach (var (agent, key) in Required)
        {
            var name = $"{agent} {key}";
            var mine = pairs.Where(p => !p.Excluded && p.Agent == agent && p.Key == key).OrderBy(p => p.GovernorReadAt).ToList();
            counted[name] = mine.Count;
            if (mine.Count < Needed) { problems.Add($"{name}: {mine.Count} counted pairs, {Needed} needed"); }
            for (var i = 0; i < mine.Count; i++)
            {
                var p = mine[i];
                if (Math.Abs(p.GovernorPercent - p.AppPercent) > MaxPercentGap)
                {
                    problems.Add($"{name} pair {i + 1}: percent {p.GovernorPercent} vs {p.AppPercent} differs by more than {MaxPercentGap}");
                }
                if ((p.AppReadAt - p.GovernorReadAt).Duration() > MaxReadGap)
                {
                    problems.Add($"{name} pair {i + 1}: app read more than {MaxReadGap.TotalSeconds:0} seconds from the governor read");
                }
                if ((p.AppMeasuredAt - p.GovernorMeasuredAt).Duration() > readInterval)
                {
                    problems.Add($"{name} pair {i + 1}: measuredAt differs by more than one read interval ({readInterval.TotalSeconds:0} s)");
                }
                if (i > 0 && p.GovernorReadAt - mine[i - 1].GovernorReadAt < MinSpacing)
                {
                    problems.Add($"{name} pair {i + 1}: taken less than a minute after the previous reading");
                }
            }
        }
        return problems.Count == 0
            ? Outcome.Pass($"Ten pairs agree for every required allowance (percent within {MaxPercentGap}, measuredAt within {readInterval.TotalSeconds:0} s).", counted)
            : Outcome.Fail(string.Join("; ", problems), counted);
    }

    public static Outcome Run(string pairsFile, TimeSpan readInterval)
    {
        List<UsagePair> pairs = [];
        try
        {
            foreach (var line in File.ReadLines(pairsFile).Where(l => l.Trim().Length > 0))
            {
                pairs.Add(JsonSerializer.Deserialize<UsagePair>(line, Json) ?? throw new JsonException("empty pair"));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return Outcome.Error($"cannot read pairs file: {e.Message}");
        }
        return Evaluate(pairs, readInterval);
    }
}
