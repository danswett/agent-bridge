using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// agent-bridge-host audit gate: the windowed HA-fallback count as one command (HA deletion gate E4(f); #1401, #1396;
// docs/design/ha-gate-counters.md section 6). It reads the audit log over a window that starts at --since, reduces it with
// the 50 launch, 30 approval, 20 stop and 500 message floors, and exits 0 (PASS), 1 (FAIL) or 2 (INCOMPLETE).
// Read-only: it takes no writer, creates no profile and repairs nothing. The message counts come from the fleet's send
// log through --messages ({"relayDelivered": n, "haFallbacks": n}); without them the answer is INCOMPLETE, never zero
// fallbacks. A PASS is this host's reading only: the fleet combiner takes one --json file per pinned host, applies the floors
// to the fleet sums, and names a host with no file INCOMPLETE. --host labels that file and --json needs it (a reading names its host); the host never reads an identity.
internal static partial class AuditGateCommand
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    const string Usage = "usage: agent-bridge-host audit gate --since <ISO time with offset> [--profile <name>] [--messages <file>] [--host <id>] [--json (needs --host)]";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex HostId();

    // The closed set of reason codes the JSON carries (schema 1): never text. A shortfall of a floor is below-floor:<unit>.
    static readonly HashSet<string> Known =
    [
        "invalidated-window", "ha-fallback", "coverage-break", "retention-trimmed-into-window", "unresolved-intent", "audit-incomplete",
        "sequence-break", "missing-window-evidence", "missing-message-evidence",
        "below-floor:launch", "below-floor:approval", "below-floor:stop", "below-floor:message",
    ];

    internal static int Run(IReadOnlyList<string> args, string profileRoot, TextWriter output, TextWriter error)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = false;
        for (var i = 2; i < args.Count; i++)
        {
            if (args[i] == "--json" && !json) { json = true; continue; }
            if (args[i] is not ("--since" or "--profile" or "--messages" or "--host") || i + 1 >= args.Count
                || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith('-') || !options.TryAdd(args[i], args[i + 1])) { return Fail(error, Usage); }
            i++;
        }
        var profile = options.GetValueOrDefault("--profile", "default");
        var host = options.GetValueOrDefault("--host");
        if (!options.TryGetValue("--since", out var sinceText) || !AuditCommands.TryParseTime(sinceText, out var since)
            || !Identity.IsValidProfile(profile) || host is not null && !HostId().IsMatch(host) || json && host is null) { return Fail(error, Usage); }

        GateMessageEvidence? messages = null;
        if (options.TryGetValue("--messages", out var messagesFile))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(messagesFile));
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("relayDelivered", out var delivered) || !delivered.TryGetInt32(out var d)
                    || !e.TryGetProperty("haFallbacks", out var fallbacks) || !fallbacks.TryGetInt32(out var f)) { throw new FormatException("needs integer relayDelivered and haFallbacks"); }
                messages = new GateMessageEvidence(d, f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
            {
                return Fail(error, $"audit gate: cannot use --messages {messagesFile}: {e.Message}");
            }
        }

        var report = GateAuditProjection.ReadWindow(Path.Combine(profileRoot, profile), since);
        var verdict = GateVerdict.Evaluate(report, messages);
        var dispatches = Math.Max(report.LegacyHaDispatches, report.Totals.Where(t => t.Path == "legacy-ha").Sum(t => t.Successes));
        if (json) { output.WriteLine(JsonSerializer.Serialize(Describe(host, since, report, verdict, dispatches, messages), Json)); }
        else { Print(output, host, since, report, verdict, dispatches, messages); }
        return verdict.ExitCode;
    }

    static string[] Codes(GateVerdict verdict) =>
        [.. verdict.Reasons.Select(r => r.Contains(" < ") ? "below-floor:" + r.Split(' ')[0] : r.StartsWith("ha-fallback", StringComparison.Ordinal) ? "ha-fallback" : r)
            .Select(code => Known.Contains(code) ? code : "audit-incomplete").Distinct()];

    // Schema 1, closed: the combiner refuses a field it does not know by name.
    static object Describe(string? host, DateTimeOffset since, GateAuditReport report, GateVerdict verdict, int dispatches, GateMessageEvidence? messages) => new
    {
        schema = 1,
        host = host!,
        since = since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        kind = verdict.Kind.ToString().ToLowerInvariant(),
        reasons = Codes(verdict),
        relay = GateVerdict.Units.ToDictionary(u => u.Name, u => verdict.Relay[u.Action]),
        messages = messages is null ? null : new { relayDelivered = messages.RelayDelivered, haFallbacks = messages.HaFallbacks },
        invalidated = report.Invalidated,
        legacyHaDispatches = dispatches,
    };

    static void Print(TextWriter output, string? host, DateTimeOffset since, GateAuditReport report, GateVerdict verdict, int dispatches, GateMessageEvidence? messages)
    {
        var pacific = TimeZoneInfo.ConvertTime(since, Pacific).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
        output.WriteLine($"audit gate{(host is null ? "" : " " + host)}: window since {pacific}");
        foreach (var (action, name, needed) in GateVerdict.Units)
        {
            var local = report.Totals.Where(t => t.Action == action && t.Path == "local").Sum(t => t.Successes);
            output.WriteLine($"  {Plural(name),-10} relay {verdict.Relay[action]} / {needed}   (local {local})");
        }
        output.WriteLine(messages is null ? "  messages   no message evidence given" : $"  messages   relay {messages.RelayDelivered} / {GateVerdict.Messages}   (HA fallbacks {messages.HaFallbacks})");
        output.WriteLine($"  legacy-HA dispatches {dispatches}");
        output.WriteLine($"verdict: {verdict.Kind.ToString().ToUpperInvariant()}");
        foreach (var reason in verdict.Reasons) { output.WriteLine($"  {reason}"); }
        output.WriteLine("This is one host's reading (single host), not retirement evidence by itself: the fleet gate needs every pinned host.");
    }

    static string Plural(string unit) => unit == "launch" ? "launches" : unit + "s";

    static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 2;
    }
}
