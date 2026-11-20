using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;
using static AgentBridge.Protocol.StatusBanner;

public class StatusContractTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    static string At(int secondsAgo) => Now.AddSeconds(-secondsAgo).ToString("yyyy-MM-ddTHH:mm:ssZ");

    static JsonObject Baseline() => JsonNode.Parse($$"""
        { "schema": 1, "generatedAt": "{{At(0)}}", "sections": {
          "delivery": { "generatedAt": "{{At(60)}}", "mergesPerHour": { "agentBridge": 12, "fleet": 3, "roadmap": 0 },
            "minutesSinceLastMerge": 5, "mainCi": "green", "mainCiMinutes": 3, "actionsQueued": 2, "actionsInProgress": 4,
            "readyPrs": 3, "armedPrs": 1, "blockedPrs": 0, "flakes": 0 },
          "fleet": { "generatedAt": "{{At(60)}}", "sessionsLive": 9, "buildersWorking": 3, "buildersIdle": 1, "reviewersWorking": 2,
            "reviewersIdle": 0, "agentErrors": 0, "machinesOnline": 5, "readyIssues": 4, "cpuBand": "medium", "memoryBand": "low" },
          "quota": { "generatedAt": "{{At(60)}}", "restRemainingPercent": [80, 60], "modelsInUse": 4, "modelCap": 10, "loadBand": "low" },
          "incidents": { "generatedAt": "{{At(60)}}", "rows": [ { "generatedAt": "{{At(60)}}", "state": "resolved",
            "openedAt": "{{At(3000)}}", "resolvedAt": "{{At(1000)}}", "label": "Merges delayed", "blocked": "merges" } ] },
          "systems": { "generatedAt": "{{At(60)}}", "rows": [ { "name": "f3", "state": "ok", "mode": "live", "generatedAt": "{{At(60)}}" } ] },
          "programs": { "generatedAt": "{{At(60)}}", "rows": [ { "name": "f7", "done": 2, "total": 5, "parityState": "pending", "generatedAt": "{{At(60)}}" } ] }
        } }
        """)!.AsObject();

    static StatusReading Read(Action<JsonObject>? change = null, StatusProducers counted = StatusProducers.Systems | StatusProducers.Programs)
    {
        var json = Baseline();
        change?.Invoke(json);
        return StatusContract.Read(json.ToJsonString(), Now, counted);
    }

    static JsonObject Section(JsonObject json, string name) => json["sections"]![name]!.AsObject();
    static JsonObject Row(JsonObject json, string name) => Section(json, name)["rows"]![0]!.AsObject();

    // "delivery.mainCi=red quota.restRemainingPercent=[4]": paths under sections; JSON values, else strings.
    static void Apply(JsonObject json, string changes)
    {
        foreach (var change in changes.Split(' '))
        {
            var (path, raw) = (change[..change.IndexOf('=')].Split('.'), change[(change.IndexOf('=') + 1)..]);
            JsonNode node = json["sections"]!;
            foreach (var step in path[..^1]) node = int.TryParse(step, out var i) ? node[i]! : node[step]!;
            JsonNode value;
            try { value = JsonNode.Parse(raw)!; } catch (JsonException) { value = raw; }
            node[path[^1]] = value;
        }
    }

    [Fact]
    public void A_fresh_complete_file_is_valid_operational_and_every_row_available()
    {
        var reading = Read();
        Assert.True(reading.Valid);
        Assert.Equal(Operational, reading.Banner);
        Assert.NotNull(reading.Document);
        Assert.Empty(reading.Reasons);
        Assert.All(new[] { "delivery", "fleet", "quota", "incidents", "systems", "programs", "systems/f3", "programs/f7", "incidents/0" },
            key => Assert.True(reading.Available[key], key));
    }

    public static TheoryData<string, Action<JsonObject>> Refused => new()
    {
        { "wrong type", j => Section(j, "delivery")["readyPrs"] = "3" },
        { "fractional count", j => Section(j, "delivery")["readyPrs"] = 3.5 },
        { "unknown field", j => Section(j, "fleet")["machineName"] = "worker" },
        { "unknown root field", j => j["nodeId"] = "agent_bridge_0123456789abcdef" },
        { "unknown section", j => j["sections"]!["tokens"] = new JsonObject() },
        { "incident title", j => Row(j, "incidents")["title"] = "Relay down" },
        { "label outside the closed set", j => Row(j, "incidents")["label"] = "Relay down" },
        { "blocked without a label", j => Row(j, "incidents").Remove("label") },
        { "resolved time on an open incident", j => Row(j, "incidents")["state"] = "identified" },
        { "resolved before opened", j => Row(j, "incidents")["resolvedAt"] = At(4000) },
        { "missing required field", j => Section(j, "fleet").Remove("agentErrors") },
        { "missing required section", j => j["sections"]!.AsObject().Remove("quota") },
        { "oversized count", j => Section(j, "delivery")["readyPrs"] = 100001 },
        { "negative count", j => Section(j, "delivery")["flakes"] = -1 },
        { "percent over 100", j => Section(j, "quota")["restRemainingPercent"] = new JsonArray(101) },
        { "too many incidents", j => Section(j, "incidents")["rows"] = new JsonArray([.. Enumerable.Range(0, 21).Select(_ => Row(j, "incidents").DeepClone())]) },
        { "duplicate system", j => Section(j, "systems")["rows"]!.AsArray().Add(Row(j, "systems").DeepClone()) },
        { "done over total", j => Row(j, "programs")["done"] = 6 },
        { "unknown system code", j => Row(j, "systems")["name"] = "relay-west" },
        { "offset time", j => j["generatedAt"] = "2026-11-20T04:00:00-08:00" },
        { "impossible date", j => j["generatedAt"] = "2026-02-30T12:00:00Z" },
        { "unknown schema version", j => j["schema"] = 2 },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_refused_file_reads_unavailable_and_exposes_no_values(string why, Action<JsonObject> change)
    {
        var reading = Read(change);
        Assert.False(reading.Valid, why);
        Assert.Equal(Unavailable, reading.Banner);
        Assert.Null(reading.Document);
        Assert.Empty(reading.Available);
    }

    [Fact]
    public void Duplicate_keys_unparseable_and_oversized_files_are_refused()
    {
        var json = Baseline().ToJsonString();
        Assert.False(StatusContract.Read(json.Replace("\"schema\":1", "\"schema\":1,\"schema\":1"), Now).Valid);
        Assert.False(StatusContract.Read(json[..^1], Now).Valid);
        Assert.False(StatusContract.Read(json + new string(' ', StatusContract.MaxBytes), Now).Valid);
        Assert.False(StatusContract.Read(Encoding.UTF8.GetBytes(json).Concat(new byte[] { 0xFF }).ToArray(), Now).Valid);
    }

    [Theory]
    [InlineData(600, true)] [InlineData(601, false)]
    [InlineData(-1, false)]
    public void A_file_over_ten_minutes_old_or_from_the_future_is_unavailable(int secondsAgo, bool fresh)
    {
        var reading = Read(j => j["generatedAt"] = At(secondsAgo));
        Assert.Equal(fresh, reading.Valid);
        Assert.Equal(fresh ? Operational : Unavailable, reading.Banner);
        Assert.Equal(fresh, reading.Document is not null);
    }

    [Theory]
    [InlineData("delivery.generatedAt", "delivery")] [InlineData("incidents.generatedAt", "incidents")]
    [InlineData("systems.rows.0.generatedAt", "systems/f3")] [InlineData("programs.rows.0.generatedAt", "programs/f7")]
    [InlineData("incidents.rows.0.generatedAt", "incidents/0")]
    public void A_stale_or_future_section_or_row_is_unavailable_and_degrades(string path, string key)
    {
        foreach (var secondsAgo in new[] { 601, -1 })
        {
            var reading = Read(j => Apply(j, $"{path}={At(secondsAgo)}"));
            Assert.True(reading.Valid);
            Assert.False(reading.Available[key]);
            Assert.Equal(Degraded, reading.Banner);
        }
    }

    [Fact]
    public void Unproduced_systems_and_programs_show_unavailable_but_count_only_once_enabled()
    {
        static void Drop(JsonObject j) { j["sections"]!.AsObject().Remove("systems"); j["sections"]!.AsObject().Remove("programs"); }
        var uncounted = Read(Drop, StatusProducers.None);
        Assert.Equal(Operational, uncounted.Banner);
        Assert.False(uncounted.Available["systems"]);
        Assert.Equal(Operational, Read(j => Apply(j, "systems.rows.0.state=down"), StatusProducers.None).Banner);
        Assert.Equal(Degraded, Read(Drop, StatusProducers.Systems).Banner);
    }

    [Theory]
    [InlineData("delivery.minutesSinceLastMerge=59", Operational)] [InlineData("delivery.minutesSinceLastMerge=60", Outage)]
    [InlineData("delivery.minutesSinceLastMerge=600 delivery.readyPrs=0", Operational)]
    [InlineData("delivery.mainCi=red delivery.mainCiMinutes=59", Degraded)] [InlineData("delivery.mainCi=red delivery.mainCiMinutes=60", Outage)]
    [InlineData("delivery.mainCi=running delivery.mainCiMinutes=45", Operational)]
    [InlineData("delivery.mainCi=running delivery.mainCiMinutes=46", Degraded)] [InlineData("delivery.mainCi=unknown", Degraded)]
    [InlineData("delivery.actionsQueued=25", Operational)] [InlineData("delivery.actionsQueued=26", Degraded)]
    [InlineData("delivery.actionsQueued=150", Degraded)] [InlineData("delivery.actionsQueued=151", Outage)]
    [InlineData("fleet.agentErrors=3", Operational)] [InlineData("fleet.agentErrors=4", Degraded)]
    [InlineData("fleet.buildersWorking=2 fleet.readyIssues=4", Operational)] [InlineData("fleet.buildersWorking=2 fleet.readyIssues=5", Degraded)]
    [InlineData("quota.restRemainingPercent=[20,90]", Operational)] [InlineData("quota.restRemainingPercent=[90,19]", Degraded)]
    [InlineData("quota.restRemainingPercent=[5]", Degraded)] [InlineData("quota.restRemainingPercent=[4]", Outage)]
    [InlineData("quota.restRemainingPercent=[]", Degraded)] [InlineData("quota.modelsInUse=9", Operational)]
    [InlineData("quota.modelsInUse=10", Degraded)] [InlineData("incidents.rows.0.state=identified incidents.rows.0.resolvedAt=null", Degraded)]
    [InlineData("systems.rows.0.state=degraded", Degraded)] [InlineData("programs.rows.0.parityState=failed", Degraded)]
    [InlineData("delivery.actionsQueued=151 fleet.agentErrors=9", Outage)]
    public void The_banner_follows_the_documented_thresholds_at_every_boundary(string changes, StatusBanner expected)
    {
        var reading = Read(j =>
        {
            Apply(j, changes);
            if (Row(j, "incidents")["resolvedAt"] is null) Row(j, "incidents").Remove("resolvedAt");
        });
        Assert.True(reading.Valid);
        Assert.Equal(expected, reading.Banner);
    }

    [Fact]
    public void The_allow_list_names_no_node_machine_token_or_free_text_field()
    {
        Assert.Contains("restRemainingPercent", StatusContract.AllowedFields);
        Assert.DoesNotContain(StatusContract.AllowedFields, f => System.Text.RegularExpressions.Regex.IsMatch(f,
            "(?i)node|machine(?!sOnline)|host|token|key|secret|title|text|path|url|session(?!sLive)|account|user|login|description"));
    }
}
