using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.Rehearsal;

internal enum Verdict { Pass, Fail, Error, Manual }

// What one check found. Fail means the product did something other than the checklist
// expects; Error means the check itself could not be made (no host, no transcript), which is
// never a pass; Manual means a person has to look.
internal sealed record Outcome(Verdict Verdict, string Detail, object? Data = null)
{
    public static Outcome Pass(string detail, object? data = null) => new(Verdict.Pass, detail, data);
    public static Outcome Fail(string detail, object? data = null) => new(Verdict.Fail, detail, data);
    public static Outcome Error(string detail, object? data = null) => new(Verdict.Error, detail, data);
    public static Outcome Manual(string detail, object? data = null) => new(Verdict.Manual, detail, data);

    public int ExitCode => Verdict switch { Verdict.Pass => 0, Verdict.Fail => 1, Verdict.Error => 2, _ => 3 };
}

// Where a session's transcript (or the host log) ended when it was marked. Checks read only
// what was written after it, so evidence is the record a step produced, found by what it
// says, and never something older that happens to match.
internal sealed record Mark(string Kind, string Path, long Offset, DateTimeOffset At);

internal sealed record EvidenceRecord(string Step, string Command, IReadOnlyList<string> Argv, string Verdict, string Detail, JsonElement? Data, DateTimeOffset At);

// The run's evidence folder: one JSON line per attempt at a step under steps/, and the marks.
internal sealed class Evidence(string folder)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Folder { get; } = Path.GetFullPath(folder);
    string MarksFile => Path.Combine(Folder, "marks.json");
    string StepsFolder => Path.Combine(Folder, "steps");

    public void Record(string step, string command, IReadOnlyList<string> argv, Outcome outcome, DateTimeOffset at)
    {
        if (!ValidStep(step)) { throw new UsageException("--step is letters, digits and '-' only"); }
        Directory.CreateDirectory(StepsFolder);
        var data = outcome.Data is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(outcome.Data, Json);
        var record = new EvidenceRecord(step, command, argv, outcome.Verdict.ToString().ToLowerInvariant(), outcome.Detail, data, at);
        File.AppendAllText(Path.Combine(StepsFolder, step + ".jsonl"), JsonSerializer.Serialize(record, Json) + "\n", new UTF8Encoding(false));
    }

    public static bool ValidStep(string step) => step.Length is > 0 and <= 40 && step.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public Dictionary<string, Mark> Marks()
    {
        if (!File.Exists(MarksFile)) { return new(StringComparer.OrdinalIgnoreCase); }
        var marks = JsonSerializer.Deserialize<Dictionary<string, Mark>>(File.ReadAllText(MarksFile), Json);
        return new(marks ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public void SaveMark(string key, Mark mark)
    {
        Directory.CreateDirectory(Folder);
        var marks = Marks();
        marks[key] = mark;
        File.WriteAllText(MarksFile, JsonSerializer.Serialize(marks, Json), new UTF8Encoding(false));
    }

    public IReadOnlyList<EvidenceRecord> Records()
    {
        if (!Directory.Exists(StepsFolder)) { return []; }
        var all = new List<EvidenceRecord>();
        foreach (var file in Directory.EnumerateFiles(StepsFolder, "*.jsonl"))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.Length == 0) { continue; }
                try
                {
                    if (JsonSerializer.Deserialize<EvidenceRecord>(line, Json) is { } r) { all.Add(r); }
                }
                catch (JsonException) { }
            }
        }
        return all;
    }

    // One row per step: its latest attempt, how many there were, and when, in Pacific time.
    // Given the checklist's steps, those with no evidence yet are listed after them.
    public string Report(TimeZoneInfo zone, IReadOnlyList<string>? checklist = null)
    {
        var rows = Records().GroupBy(r => r.Step, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Latest: g.OrderBy(r => r.At).Last(), Attempts: g.Count()))
            .OrderBy(x => x.Latest.Step, StringComparer.Ordinal)
            .ToList();
        var sb = new StringBuilder();
        sb.AppendLine("| Step | Result | Attempts | Last run | Detail |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var (latest, attempts) in rows)
        {
            var when = TimeZoneInfo.ConvertTime(latest.At, zone).ToString("yyyy-MM-dd HH:mm:ss");
            sb.AppendLine($"| {latest.Step} | {latest.Verdict} | {attempts} | {when} | {Cell(latest.Detail)} |");
        }
        var counts = rows.GroupBy(r => r.Latest.Verdict).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}");
        sb.AppendLine();
        sb.AppendLine($"{rows.Count} steps: {string.Join(", ", counts)}.");
        if (checklist is not null)
        {
            var run = rows.Select(r => r.Latest.Step).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = checklist.Where(s => !run.Contains(s)).ToList();
            sb.AppendLine(missing.Count == 0 ? "Every step in the checklist has evidence." : $"Not run yet ({missing.Count}): {string.Join(", ", missing)}.");
        }
        return sb.ToString();
    }

    // A screenshot a step's check rests on, kept beside its record as screens/<step>.<ext>.
    public string SaveScreenshot(string step, string file)
    {
        if (!ValidStep(step)) { throw new UsageException("--step is letters, digits and '-' only"); }
        var folder = Path.Combine(Folder, "screens");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, step + Path.GetExtension(file).ToLowerInvariant());
        File.Copy(file, target, overwrite: true);
        return Path.GetRelativePath(Folder, target).Replace('\\', '/');
    }

    static string Cell(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    public static TimeZoneInfo Pacific()
    {
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }
}
