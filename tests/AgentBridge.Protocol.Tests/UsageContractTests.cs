using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

public sealed class UsageContractTests
{
    [Fact]
    public void A_report_from_a_newer_host_with_an_unknown_agent_and_source_still_reads()
    {
        const string json = """
            {"agents":[{"agent":"gemini","label":"Gemini CLI","source":"oracle","windows":[{"key":"daily","label":"Daily","percent":12.5,"burst":true}],
              "measuredAt":"2026-10-28T20:00:00.000Z","colour":"blue"}]}
            """;

        var report = JsonSerializer.Deserialize<UsageReport>(json, ProtocolJson.Options)!;

        var agent = Assert.Single(report.Agents);
        Assert.Equal((AgentKind.Unknown, "Gemini CLI", UsageSource.None), (agent.Agent, agent.Label, agent.Source));
        Assert.Equal(12.5, Assert.Single(agent.Windows).Percent);
    }

    [Fact]
    public void A_report_serializes_to_the_contracts_names_and_leaves_out_what_is_absent()
    {
        var report = new UsageReport([new AgentUsage
        {
            Agent = AgentKind.Codex,
            Label = "Codex",
            Source = UsageSource.Transcript,
            Windows = [new UsageWindow { Key = "primary", Label = "5h limit", Percent = 23.5, ResetsAt = new DateTimeOffset(2026, 10, 28, 22, 0, 0, TimeSpan.Zero) }],
            MeasuredAt = new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero),
        }]);

        Assert.Equal(
            """{"agents":[{"agent":"codex","label":"Codex","source":"transcript","windows":[{"key":"primary","label":"5h limit","percent":23.5,"resetsAt":"2026-10-28T22:00:00.000Z"}],"measuredAt":"2026-10-28T20:00:00.000Z"}]}""",
            JsonSerializer.Serialize(report, ProtocolJson.Options));
    }

    [Fact]
    public void A_further_copilot_account_carries_its_place_and_fits_beside_every_agent_within_the_budget()
    {
        AgentUsage Copilot(string login, int? slot) => new()
        {
            Agent = AgentKind.Copilot,
            Label = "GitHub Copilot",
            Account = login,
            Source = UsageSource.Api,
            Slot = slot,
            Error = new string('x', UsageProtocol.Error),
            Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = 40, Used = 600000, Limit = 1500000, Unit = "AIC" }],
            MeasuredAt = DateTimeOffset.UnixEpoch,
        };
        var codex = new AgentUsage { Agent = AgentKind.Codex, Label = "Codex", Source = UsageSource.None, Error = "no rollout", MeasuredAt = DateTimeOffset.UnixEpoch };
        // The signed-in account, Claude and Codex, and the three further accounts there are places for.
        var report = new UsageReport([Copilot("octocat", null), codex with { Agent = AgentKind.Claude, Label = "Claude Code" }, codex,
            Copilot("hubot", 2), Copilot("monalisa", 3), Copilot("dswett_microsoft", 4)]);

        var fitted = UsageProtocol.Fit(report);

        Assert.Equal(report.Agents.Select(a => (a.Account, a.Slot)), fitted.Agents.Select(a => (a.Account, a.Slot)));
        Assert.True(ProtocolJson.ByteCount(fitted) <= UsageProtocol.MaxReportBytes);
        var json = JsonSerializer.Serialize(fitted, ProtocolJson.Options);
        Assert.Contains("\"account\":\"hubot\",\"source\":\"api\"", json, StringComparison.Ordinal);
        Assert.Contains("\"slot\":2}", json, StringComparison.Ordinal);
        Assert.Equal(3, json.Split("\"slot\":").Length - 1);
        Assert.Equal(3, JsonSerializer.Deserialize<UsageReport>(json, ProtocolJson.Options)!.Agents.Count(a => a.Slot is not null));
    }

    [Fact]
    public void Fitting_a_report_cuts_long_text_and_keeps_it_within_its_budget()
    {
        var agent = new AgentUsage
        {
            Agent = AgentKind.Copilot,
            Label = "GitHub Copilot",
            Source = UsageSource.Cache,
            Error = new string('é', 2000),
            Windows = [.. Enumerable.Range(0, 20).Select(i => new UsageWindow { Key = $"w{i}", Label = string.Concat(Enumerable.Repeat("😀", 100)), Percent = i })],
            MeasuredAt = DateTimeOffset.UnixEpoch,
        };

        var fitted = UsageProtocol.Fit(new UsageReport([.. Enumerable.Repeat(agent, 20)]));

        Assert.True(ProtocolJson.ByteCount(fitted) <= UsageProtocol.MaxReportBytes);
        Assert.InRange(fitted.Agents.Count, 1, UsageProtocol.MaxAgents);
        Assert.All(fitted.Agents, a => Assert.True(a.Windows.Count <= UsageProtocol.MaxWindows));
        Assert.All(fitted.Agents, a => Assert.True(a.Error is null || System.Text.Encoding.UTF8.GetByteCount(a.Error) <= UsageProtocol.Error));
    }

    [Fact]
    public void A_shared_report_keeps_the_hosts_own_readings_first_and_drops_the_oldest_peer_readings_past_its_limits()
    {
        var at = new DateTimeOffset(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);
        var reader = new UsageReader(new string('a', 32), "Rehearsal VM");
        AgentUsage Claude(int i, string? error = null, int windows = 1, string label = "Weekly") => new()
        {
            Agent = AgentKind.Claude, Label = "Claude Code", AccountKey = $"{i:x12}", Source = UsageSource.Api, MeasuredAt = at.AddHours(-i), Error = error, ReadBy = reader,
            Windows = [.. Enumerable.Range(0, windows).Select(w => new UsageWindow { Key = $"w{w}", Label = label, Unit = label, Percent = i })],
        };
        var own = new UsageReport([
            new AgentUsage { Agent = AgentKind.Copilot, Label = "GitHub Copilot", Account = "octo", Source = UsageSource.Api, MeasuredAt = at, Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = 40 }] },
            new AgentUsage { Agent = AgentKind.Codex, Label = "Codex", Source = UsageSource.Transcript, MeasuredAt = at, Windows = [new UsageWindow { Key = "primary", Label = "5h limit", Percent = 5 }] },
        ]);
        // Shuffled, so the order kept is the host's doing.
        var peers = Enumerable.Range(1, 20).OrderBy(i => (i * 7) % 20).ToList();

        var counted = UsageShare.Serve(own, peers.Select(i => Claude(i)));
        Assert.Equal(UsageProtocol.MaxSharedAgents, counted.Agents.Count);
        Assert.Equal(own.Agents, counted.Agents.Take(2));
        Assert.Equal(Enumerable.Range(1, 14).Select(i => $"{i:x12}"), counted.Agents.Skip(2).Select(a => a.AccountKey));
        Assert.Equal("Rehearsal VM", counted.Agents[2].ReadBy!.Name);

        // Every field at its limit, in characters JSON escapes to six bytes each: fewer fit.
        var escaped = new string('\u0001', 4000);
        var heavy = UsageShare.Serve(own, peers.Select(i => Claude(i, escaped, windows: 9, label: escaped)));
        Assert.True(ProtocolJson.ByteCount(heavy) <= UsageProtocol.MaxSharedReportBytes);
        Assert.InRange(heavy.Agents.Count, 3, UsageProtocol.MaxSharedAgents - 1);
        Assert.Equal(Enumerable.Range(1, heavy.Agents.Count - 2).Select(i => $"{i:x12}"), heavy.Agents.Skip(2).Select(a => a.AccountKey));
        // A peer's error is passed on bounded, never expanded.
        Assert.All(heavy.Agents.Skip(2), a => Assert.Equal(UsageProtocol.Error, System.Text.Encoding.UTF8.GetByteCount(a.Error!)));
    }

    [Fact]
    public void A_reader_that_is_not_a_device_id_is_dropped_and_its_name_is_bounded_and_plain()
    {
        var a = new AgentUsage { Agent = AgentKind.Claude, Label = "Claude Code", Source = UsageSource.Api, MeasuredAt = DateTimeOffset.UnixEpoch };

        Assert.Null(UsageProtocol.FitAgent(a with { ReadBy = new UsageReader("not-an-id", "VM") }).ReadBy);
        var named = UsageProtocol.FitAgent(a with { ReadBy = new UsageReader(new string('b', 32), "VM\u001b[2J" + new string('n', 200)) }).ReadBy!;
        Assert.Equal("VM[2J" + new string('n', UsageProtocol.ReaderName - 5), named.Name);
        Assert.Contains("\"readBy\":{\"id\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"name\":\"VM[2J",
            JsonSerializer.Serialize(a with { ReadBy = named }, ProtocolJson.Options), StringComparison.Ordinal);
    }
}
