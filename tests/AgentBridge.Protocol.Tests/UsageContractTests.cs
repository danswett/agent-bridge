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
}
