using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Protocol;

public enum StatusBanner { Unavailable, Operational, Degraded, Outage }

// Systems and programs are uncounted until trusted configuration enables their producer,
// never inferred from what the file happens to contain (Product ruling on #2136).
[Flags]
public enum StatusProducers { None = 0, Systems = 1, Programs = 2 }

public sealed class StatusReading
{
    public bool Valid { get; init; }
    public StatusBanner Banner { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    // Section names and row keys ("systems/f3", "incidents/0"); empty unless the file is valid and fresh.
    public IReadOnlyDictionary<string, bool> Available { get; init; } = new Dictionary<string, bool>();
    public JsonElement? Document { get; init; }
}

// The only reader of status.json (docs/protocol/status-json.md). Anything it does not recognise
// exactly makes the whole file Unavailable, so a bad or stale file can never show as healthy.
public static partial class StatusContract
{
    public const int MaxBytes = 65536, MaxCount = 100000, MaxAgeSeconds = 600;
    public const int NoMergeOutageMinutes = 60, CiOutageMinutes = 60, QueueOutage = 150, QuotaOutagePercent = 5;
    public const int CiRunningDegradedMinutes = 45, QueueDegraded = 25, AgentErrorsDegraded = 3, QuotaDegradedPercent = 20;

    public static readonly IReadOnlyList<string> IncidentLabels = ["Merges delayed", "Builds or tests failing",
        "Fleet capacity reduced", "Messaging delayed", "Updates delayed", "Usage limit reached", "Status data delayed", "Other"];

    delegate bool Check(JsonElement e);
    static readonly HashSet<string> allowed = new(StringComparer.Ordinal);
    public static IReadOnlySet<string> AllowedFields => allowed;
    static readonly string[] Sections = ["delivery", "fleet", "quota", "incidents", "systems", "programs"];

    static bool Count(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) && n is >= 0 and <= MaxCount;
    static bool Percent(JsonElement e) => Count(e) && e.GetInt32() <= 100;
    static Check Enum(params string[] values) => e => e.ValueKind == JsonValueKind.String && values.Contains(e.GetString(), StringComparer.Ordinal);
    static Check Arr(int max, Check item) => e => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() <= max && e.EnumerateArray().All(i => item(i));
    static Check Rows(int max, string spec, params (string, Check)[] nested) => Obj("generatedAt=time", ("rows", Arr(max, Obj(spec, nested))));

    // spec is "name=type ..." with type count, percent, percents, time, one, or a|b|c (a closed set);
    // a name ending in ? is optional. Every other field, and any duplicate key, is refused.
    static Check Obj(string spec, params (string Name, Check Check)[] nested)
    {
        var fields = new Dictionary<string, (Check Check, bool Required)>(StringComparer.Ordinal);
        foreach (var (name, type) in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(f => (f[..f.IndexOf('=')], f[(f.IndexOf('=') + 1)..])))
            fields[name.TrimEnd('?')] = (type switch
            {
                "count" => Count, "percent" => Percent, "percents" => Arr(8, Percent), "time" => e => TryTime(e, out _),
                "one" => e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) && v == 1,
                _ => Enum(type.Split('|')),
            }, !name.EndsWith('?'));
        foreach (var (name, check) in nested) fields[name.TrimEnd('?')] = (check, !name.EndsWith('?'));
        allowed.UnionWith(fields.Keys);
        return e =>
        {
            if (e.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
                if (!seen.Add(p.Name) || !fields.TryGetValue(p.Name, out var f) || !f.Check(p.Value)) return false;
            return fields.All(f => !f.Value.Required || seen.Contains(f.Key));
        };
    }

    const string Band = "low|medium|high|unknown";
    static readonly Check Root = Obj("schema=one generatedAt=time", ("sections", Obj("",
        ("delivery", Obj("generatedAt=time minutesSinceLastMerge=count mainCi=green|red|running|unknown mainCiMinutes=count " +
            "actionsQueued=count actionsInProgress=count readyPrs=count armedPrs=count blockedPrs=count flakes=count",
            ("mergesPerHour", Obj("agentBridge=count fleet=count roadmap=count")))),
        ("fleet", Obj("generatedAt=time sessionsLive=count buildersWorking=count buildersIdle=count reviewersWorking=count " +
            $"reviewersIdle=count agentErrors=count machinesOnline=count readyIssues=count cpuBand={Band} memoryBand={Band}")),
        ("quota", Obj($"generatedAt=time restRemainingPercent=percents modelsInUse=count modelCap=count loadBand={Band}")),
        ("incidents", Rows(20, "generatedAt=time state=investigating|identified|resolved openedAt=time resolvedAt?=time blocked?=merges",
            ("label?", Enum([.. IncidentLabels])))),
        ("systems?", Rows(11, "name=f2-live|f2-standby|f3|f4|f5|f6|f7|f8|counts|people|telemetry state=ok|degraded|down|unknown " +
            "mode=shadow|live|off generatedAt=time")),
        ("programs?", Rows(5, "name=ha-retirement|fleet-view|f7 done=count total=count " +
            "parityState=not-required|pending|passed|failed|unknown generatedAt=time")))));

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", RegexOptions.ECMAScript)]
    private static partial Regex UtcSeconds();

    static bool TryTime(JsonElement e, out DateTimeOffset t)
    {
        t = default;
        return e.ValueKind == JsonValueKind.String && UtcSeconds().IsMatch(e.GetString()!) && DateTimeOffset.TryParseExact(e.GetString(),
            "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t);
    }

    // Exactly ten minutes old is still fresh; one second more, or any time after now, is not.
    static bool Fresh(JsonElement e, DateTimeOffset now) => e.TryGetProperty("generatedAt", out var g)
        && TryTime(g, out var t) && t <= now && (now - t).TotalSeconds <= MaxAgeSeconds;

    static IEnumerable<JsonElement> RowsOf(JsonElement sections, string name) =>
        sections.TryGetProperty(name, out var s) && s.TryGetProperty("rows", out var rows) ? rows.EnumerateArray() : [];

    // Systems and programs rows are keyed by their closed code; incidents have no name, so by position.
    static string RowKey(JsonElement row, int i) => row.TryGetProperty("name", out var n) ? n.GetString()! : i.ToString(CultureInfo.InvariantCulture);

    static bool Consistent(JsonElement sections) =>
        RowsOf(sections, "incidents").All(i => !(i.TryGetProperty("blocked", out _) && !i.TryGetProperty("label", out _))
            && (!i.TryGetProperty("resolvedAt", out var r) || (i.GetProperty("state").GetString() == "resolved"
                && TryTime(r, out var end) && TryTime(i.GetProperty("openedAt"), out var start) && end >= start)))
        && RowsOf(sections, "programs").All(p => p.GetProperty("done").GetInt32() <= p.GetProperty("total").GetInt32())
        && new[] { "systems", "programs" }.All(n => RowsOf(sections, n).GroupBy(r => r.GetProperty("name").GetString()).All(g => g.Count() == 1));

    public static StatusReading Read(string json, DateTimeOffset now, StatusProducers counted = StatusProducers.None) =>
        Read(Encoding.UTF8.GetBytes(json), now, counted);

    public static StatusReading Read(byte[] utf8, DateTimeOffset now, StatusProducers counted = StatusProducers.None)
    {
        var refused = new StatusReading { Banner = StatusBanner.Unavailable };
        if (utf8.Length > MaxBytes) return refused;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { return refused; }
        using (doc)
        {
            var root = doc.RootElement;
            if (!Root(root) || !Fresh(root, now) || !Consistent(root.GetProperty("sections"))) return refused;
            var sections = root.GetProperty("sections");
            var available = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var name in Sections)
            {
                var fresh = available[name] = sections.TryGetProperty(name, out var s) && Fresh(s, now);
                foreach (var (row, i) in RowsOf(sections, name).Select((r, i) => (r, i)))
                    available[$"{name}/{RowKey(row, i)}"] = fresh && Fresh(row, now);
            }
            var (banner, reasons) = Derive(sections, available, counted);
            return new StatusReading { Valid = true, Banner = banner, Reasons = reasons, Available = available, Document = root.Clone() };
        }
    }

    // A pure function of validated, available numbers only; Outage wins over Degraded.
    static (StatusBanner, IReadOnlyList<string>) Derive(JsonElement s, Dictionary<string, bool> available, StatusProducers counted)
    {
        var (outage, degraded) = (new List<string>(), new List<string>());
        var counting = Sections.Where(n => n switch
        {
            "systems" => counted.HasFlag(StatusProducers.Systems),
            "programs" => counted.HasFlag(StatusProducers.Programs),
            _ => true,
        }).ToHashSet();
        degraded.AddRange(available.Where(a => !a.Value && counting.Contains(a.Key.Split('/')[0])).Select(a => $"{a.Key}-unavailable"));
        int N(string section, string field) => s.GetProperty(section).GetProperty(field).GetInt32();
        if (available["delivery"])
        {
            var (ci, ciMinutes) = (s.GetProperty("delivery").GetProperty("mainCi").GetString(), N("delivery", "mainCiMinutes"));
            if (N("delivery", "readyPrs") > 0 && N("delivery", "minutesSinceLastMerge") >= NoMergeOutageMinutes) outage.Add("no-merge");
            if (ci == "red" && ciMinutes >= CiOutageMinutes) outage.Add("main-ci-red");
            else if (ci is "red" or "unknown" || (ci == "running" && ciMinutes > CiRunningDegradedMinutes)) degraded.Add("main-ci");
            if (N("delivery", "actionsQueued") > QueueOutage) outage.Add("queue");
            else if (N("delivery", "actionsQueued") > QueueDegraded) degraded.Add("queue");
        }
        if (available["fleet"] && N("fleet", "agentErrors") > AgentErrorsDegraded) degraded.Add("agent-errors");
        if (available["fleet"] && N("fleet", "buildersWorking") * 2 < N("fleet", "readyIssues")) degraded.Add("builders-short");
        if (available["quota"])
        {
            // No identity reported is unknown quota, which must not read as plenty.
            var lowest = s.GetProperty("quota").GetProperty("restRemainingPercent").EnumerateArray().Select(p => p.GetInt32()).DefaultIfEmpty(-1).Min();
            if (lowest is >= 0 and < QuotaOutagePercent) outage.Add("quota");
            else if (lowest < QuotaDegradedPercent) degraded.Add("quota");
            if (N("quota", "modelCap") > 0 && N("quota", "modelsInUse") >= N("quota", "modelCap")) degraded.Add("model-at-cap");
        }
        bool AnyRow(string section, string field, Func<string?, bool> bad) => counting.Contains(section) &&
            RowsOf(s, section).Where((r, i) => available[$"{section}/{RowKey(r, i)}"]).Any(r => bad(r.GetProperty(field).GetString()));
        if (AnyRow("incidents", "state", v => v != "resolved")) degraded.Add("incident-open");
        if (AnyRow("systems", "state", v => v != "ok")) degraded.Add("system-not-ok");
        if (AnyRow("programs", "parityState", v => v == "failed")) degraded.Add("parity-failed");
        return (outage.Count > 0 ? StatusBanner.Outage : degraded.Count > 0 ? StatusBanner.Degraded : StatusBanner.Operational, [.. outage, .. degraded]);
    }
}
