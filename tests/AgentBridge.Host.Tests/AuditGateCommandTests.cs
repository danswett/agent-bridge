using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

// agent-bridge-host audit gate: the windowed HA-fallback count (HA deletion gate E4(f), #1401, #1396) as one
// command. Read-only; a PASS covers this host only and is never retirement evidence by itself.
public sealed class AuditGateCommandTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "audit-gate-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-20T00:00:00Z"));
    string Profile => Path.Combine(root, "default");
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }

    AgentAuditLog Log() { Directory.CreateDirectory(Profile); return new AgentAuditLog(Profile, clock); }

    // One logical action: an intent row and its terminal row, on the given path.
    void Op(AgentAuditLog log, string action, string path = "relay", string outcome = "ok")
    {
        var op = Guid.NewGuid();
        var request = Guid.NewGuid().ToString();
        var session = Guid.NewGuid().ToString();
        var actor = path == "legacy-ha" ? "ha-component" : "person-client";
        var launch = action == "session.launch";
        var gate = new GateAuditMetadata(1, op, Guid.Empty, 0, "intent", path, actor, op, "none", Question: action == "session.answer" ? Guid.NewGuid() : null);
        var intent = new AgentAuditEntry("in", launch ? request : op.ToString(), "person:phone-1", [], action, launch ? request : session, "reserved", Gate: gate);
        Assert.True(log.Append(intent));
        var success = action switch { "session.launch" => "started", "session.answer" => "delivered", _ => "ended" };
        Assert.True(log.Append(intent with { Target = session, Outcome = outcome == "ok" ? success : outcome, Gate = gate with { Phase = "terminal", ReasonCode = outcome == "ok" ? "none" : "failed" } }));
    }

    void Floors(AgentAuditLog log, int launches = 50, int answers = 30, int stops = 20, string path = "relay")
    {
        for (var i = 0; i < launches; i++) { Op(log, "session.launch", path); }
        for (var i = 0; i < answers; i++) { Op(log, "session.answer", path); }
        for (var i = 0; i < stops; i++) { Op(log, "session.stop", path); }
    }

    string Messages(int delivered = 500, int fallbacks = 0)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "messages-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { relayDelivered = delivered, haFallbacks = fallbacks }));
        return path;
    }

    (int Code, string Out, string Err) Gate(string since, params string[] more)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var args = new List<string> { "audit", "gate", "--since", since };
        args.AddRange(more);
        var code = AuditCommands.Run(args, root, o, e);
        return (code, o.ToString(), e.ToString());
    }

    const string Since = "2026-11-19T00:00:00Z";

    [Fact]
    public void Relay_credit_at_every_floor_with_message_evidence_passes_with_exit_0()
    {
        Floors(Log());
        var r = Gate(Since, "--messages", Messages());
        Assert.Equal(0, r.Code);
        Assert.Contains("PASS", r.Out);
        Assert.Contains("single host", r.Out);
    }

    [Fact]
    public void A_floor_not_reached_is_incomplete_with_exit_2_and_names_it()
    {
        Floors(Log(), launches: 49);
        var r = Gate(Since, "--messages", Messages());
        Assert.Equal(2, r.Code);
        Assert.Contains("INCOMPLETE", r.Out);
        Assert.Contains("launch 49 < 50", r.Out);
    }

    [Fact]
    public void A_legacy_HA_dispatch_in_the_window_fails_with_exit_1()
    {
        var log = Log();
        Floors(log);
        Op(log, "session.launch", "legacy-ha", outcome: "failed");
        var r = Gate(Since, "--messages", Messages());
        Assert.Equal(1, r.Code);
        Assert.Contains("FAIL", r.Out);
        Assert.Contains("ha-fallback", r.Out);
    }


    [Fact]
    public void A_message_recorder_fallback_fails()
    {
        Floors(Log());
        Assert.Equal(1, Gate(Since, "--messages", Messages(500, 1)).Code);
    }

    [Theory]
    [InlineData("{\"relayDelivered\":500,\"haFallbacks\":-1}")]
    [InlineData("{\"relayDelivered\":-5,\"haFallbacks\":0}")]
    [InlineData("not json")]
    [InlineData("{\"relayDelivered\":500}")]
    public void Message_evidence_that_is_negative_malformed_or_incomplete_is_refused_not_passed(string content)
    {
        Floors(Log());
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "bad.json");
        File.WriteAllText(path, content);
        var r = Gate(Since, "--messages", path);
        Assert.Equal(2, r.Code);
        Assert.DoesNotContain("PASS", r.Out);
        Assert.Contains("--messages", r.Err);
    }

    [Fact]
    public void Operations_before_the_start_date_are_not_counted()
    {
        var log = Log();
        Floors(log);
        clock.Advance(TimeSpan.FromDays(3));
        var r = Gate("2026-11-22T00:00:00Z", "--messages", Messages());
        Assert.Equal(2, r.Code);
        Assert.Contains("missing-window-evidence", r.Out);
    }

    [Fact]
    public void Json_output_carries_the_per_host_totals_and_says_single_host()
    {
        var log = Log();
        Floors(log);
        Op(log, "session.launch", "local");
        var r = Gate(Since, "--messages", Messages(), "--host", "host-a", "--json");
        Assert.Equal(0, r.Code);
        using var doc = JsonDocument.Parse(r.Out);
        var json = doc.RootElement;
        Assert.Equal(1, json.GetProperty("schema").GetInt32());
        Assert.Equal("host-a", json.GetProperty("host").GetString());
        Assert.Equal("2026-11-19T00:00:00Z", json.GetProperty("since").GetString());
        Assert.Equal("pass", json.GetProperty("kind").GetString());
        Assert.Equal((50, 30, 20), (json.GetProperty("relay").GetProperty("launch").GetInt32(), json.GetProperty("relay").GetProperty("approval").GetInt32(), json.GetProperty("relay").GetProperty("stop").GetInt32()));
        Assert.Equal(0, json.GetProperty("legacyHaDispatches").GetInt32());
        Assert.Equal((500, 0), (json.GetProperty("messages").GetProperty("relayDelivered").GetInt32(), json.GetProperty("messages").GetProperty("haFallbacks").GetInt32()));
        Assert.Empty(json.GetProperty("reasons").EnumerateArray());
        // A closed v1 schema: the combiner refuses a field it does not know by name.
        Assert.Equal(["host", "invalidated", "kind", "legacyHaDispatches", "messages", "reasons", "relay", "schema", "since"], json.EnumerateObject().Select(p => p.Name).Order());
        // What a fleet combiner reads instead of this host's own verdict (per-host floors are never expected to be met alone).
        Assert.False(json.GetProperty("invalidated").GetBoolean());
    }


    [Theory]
    [InlineData("")]
    [InlineData("--since")]
    [InlineData("--since yesterday")]
    [InlineData("--since 2026-11-19T00:00:00Z --bogus x")]
    [InlineData("--since 2026-11-19T00:00:00Z --json")]
    public void Bad_or_incomplete_arguments_are_a_usage_error(string joined)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = AuditCommands.Run(["audit", "gate", .. joined.Split(' ', StringSplitOptions.RemoveEmptyEntries)], root, o, e);
        Assert.Equal(2, code);
        Assert.Contains("usage", e.ToString());
        Assert.DoesNotContain("PASS", o.ToString());
    }

    [Fact]
    public void The_command_is_read_only_and_does_not_create_a_missing_profile()
    {
        var r = Gate(Since, "--profile", "nope");
        Assert.Equal(2, r.Code);
        Assert.False(Directory.Exists(Path.Combine(root, "nope")));

        Floors(Log());
        var files = Directory.GetFiles(Profile, "*", SearchOption.AllDirectories).Select(f => (f, new FileInfo(f).Length, File.GetLastWriteTimeUtc(f))).Order().ToArray();
        Gate(Since, "--messages", Messages());
        Assert.Equal(files, Directory.GetFiles(Profile, "*", SearchOption.AllDirectories).Select(f => (f, new FileInfo(f).Length, File.GetLastWriteTimeUtc(f))).Order().ToArray());
    }


    [Fact]
    public void Json_has_null_messages_and_a_missing_message_evidence_code_when_no_file_is_given()
    {
        Floors(Log());
        using var none = JsonDocument.Parse(Gate(Since, "--host", "host-a", "--json").Out);
        Assert.Null(none.RootElement.GetProperty("messages").GetString());
        Assert.Equal("host-a", none.RootElement.GetProperty("host").GetString());
        Assert.Equal("incomplete", none.RootElement.GetProperty("kind").GetString());
        Assert.Contains("missing-message-evidence", none.RootElement.GetProperty("reasons").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("")]
    public void A_host_label_that_is_not_a_plain_id_is_a_usage_error(string host)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var args = new List<string> { "audit", "gate", "--since", Since, "--host", host };
        Assert.Equal(2, AuditCommands.Run(args, root, o, e));
        Assert.Contains("usage", e.ToString());
    }

    static readonly string[] Codes =
    [
        "invalidated-window", "ha-fallback", "coverage-break", "retention-trimmed-into-window", "unresolved-intent", "audit-incomplete",
        "sequence-break", "missing-window-evidence", "missing-message-evidence",
        "below-floor:launch", "below-floor:approval", "below-floor:stop", "below-floor:message",
    ];

    [Fact]
    public void Json_reasons_are_only_codes_from_the_closed_list_never_text()
    {
        var log = Log();
        Floors(log, launches: 3, answers: 2, stops: 1);
        Op(log, "session.launch", "legacy-ha", outcome: "failed");
        foreach (var args in new[] { new[] { "--messages", Messages(10, 2) }, Array.Empty<string>() })
        {
            using var doc = JsonDocument.Parse(Gate(Since, [.. args, "--host", "host-a", "--json"]).Out);
            var reasons = doc.RootElement.GetProperty("reasons").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.Contains("ha-fallback", reasons);
            Assert.All(reasons, code => Assert.Contains(code, Codes));
            Assert.Equal(reasons.Distinct().Count(), reasons.Length);
        }
    }
}