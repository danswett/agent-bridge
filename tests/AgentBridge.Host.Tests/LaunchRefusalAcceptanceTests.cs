using System.Text.Json;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;
using Xunit.Abstractions;

[Trait("Category", "Network")]
public sealed class LaunchRefusalAcceptanceTests(ITestOutputHelper output) : LaunchFlowFixture
{
    [Theory]
    [InlineData("agent")]
    [InlineData("model")]
    [InlineData("workspace")]
    [InlineData("other-agent-model")]
    [InlineData("offered")]
    public async Task A_synthetic_machines_offered_choices_bound_what_can_start(string scenario)
    {
        var fixture = LaunchOptionsSyntheticFixture.Read();
        locator.Installed.Clear();
        foreach (var agent in fixture.Agents)
        {
            locator.Installed[agent.Id] = Path.Combine(f.Root, agent.Id + ".cmd");
        }
        var config = Config(c => c with
        {
            DefaultAgent = fixture.Defaults.Agent!,
            DefaultWorkspace = fixture.Defaults.Workspace,
            Permissions = fixture.Defaults.Permissions,
            Workspaces = [.. fixture.Workspaces.Select(w => new WorkspaceConfig(
                w.Label, Directory.CreateDirectory(Path.Combine(f.Root, w.Label)).FullName, false))],
            Agents = fixture.Agents.ToDictionary(a => a.Id, a => new AgentConfig
            {
                Models = a.Models,
                Efforts = a.Efforts,
                Contexts = a.Contexts,
            }),
        });
        Serve(config);
        var ch = await OpenAsync();
        var offered = JsonSerializer.Deserialize<LaunchOptions>(
            (await RequestAsync(ch, LaunchProtocol.Options, new { })).GetRawText(), ProtocolJson.Options)!;
        Assert.Equal(fixture.Agents.Select(a => a.Id), offered.Agents.Select(a => a.Id));
        Assert.Equal(fixture.Workspaces.Select(w => w.Label), offered.Workspaces.Select(w => w.Label));
        foreach (var agent in fixture.Agents)
        {
            Assert.Equal(agent.Models, offered.Agents.Single(a => a.Id == agent.Id).Models);
        }

        var chosen = offered.Agents.Single(a => a.Id == fixture.Defaults.Agent);
        var model = chosen.Models.First();
        var agentId = chosen.Id;
        var workspace = offered.Defaults.Workspace!;
        string? reason = null;
        switch (scenario)
        {
            case "agent":
                agentId = LaunchProtocol.Agents.First(a => offered.Agents.All(o => o.Id != a));
                reason = $"Unknown agent '{agentId}'; this machine has {string.Join(", ", offered.Agents.Select(a => a.Id))}";
                break;
            case "model":
                model = "unlisted-model";
                Assert.DoesNotContain(offered.Agents, a => a.Models.Contains(model));
                reason = $"Unknown model '{model}' for {chosen.Label}; it offers {string.Join(", ", chosen.Models)}";
                break;
            case "workspace":
                workspace = "unlisted-workspace";
                Assert.DoesNotContain(offered.Workspaces, w => w.Label == workspace);
                reason = $"Unknown workspace '{workspace}'; this machine has {string.Join(", ", offered.Workspaces.Select(w => w.Label))}";
                break;
            case "other-agent-model":
                model = offered.Agents.Where(a => a.Id != chosen.Id).SelectMany(a => a.Models)
                    .First(m => !chosen.Models.Contains(m));
                reason = $"Unknown model '{model}' for {chosen.Label}; it offers {string.Join(", ", chosen.Models)}";
                break;
        }

        var result = await LaunchAsync(ch, new { agent = agentId, model, workspace, prompt = "reply OK" });
        var row = new LaunchEvidenceRow(LaunchOptionsSyntheticFixture.Machine, agentId, model, workspace,
            result.Outcome_, starter.Specs.Count, "not probed", result.Reason);
        output.WriteLine(JsonSerializer.Serialize(row));
        if (reason is not null)
        {
            Assert.Equal(("refused", reason), (result.Outcome_, result.Reason));
            Assert.Null(result.Session);
            Assert.Equal(JsonValueKind.Undefined, result.Launched.ValueKind);
            Assert.Empty(starter.Specs);
            Assert.Empty(source.Snapshot());
            Assert.Empty(screens.Typed);
        }
        else
        {
            Assert.Equal("started", result.Outcome_);
            Assert.NotNull(result.Session);
            Assert.Equal(agentId, result.Launched.GetProperty("agent").GetString());
            Assert.Equal(model, result.Launched.GetProperty("model").GetString());
            var spec = Assert.Single(starter.Specs);
            Assert.Equal(config.Workspaces.Single(w => w.Label == workspace).Path, spec.WorkingDirectory);
            var args = spec.Arguments.ToList();
            Assert.Equal(model, args[args.IndexOf("--model") + 1]);
        }

        var evidence = LaunchRefusalEvidence.Render([row]);
        Assert.Contains(LaunchOptionsSyntheticFixture.Provenance, evidence);
        Assert.Contains("| Machine | Agent | Model | Workspace | Outcome | Started | Answered | Raw refusal |", evidence);
        Assert.Contains(reason is null ? "| started | yes | not probed |" : "| refused | no | not probed |", evidence);
        output.WriteLine(evidence);
    }
}

public sealed class LaunchRefusalEvidenceTests
{
    [Fact]
    public void Evidence_keeps_every_machine_and_the_raw_refusal_without_breaking_the_table()
    {
        const string Reason = "Unknown model 'x|y'; it offers <default>\nsecond line";
        LaunchEvidenceRow[] rows =
        [
            new("synthetic-one", "copilot", "x|y", "App", "refused", 0, "not probed", Reason),
            new("synthetic-two", "copilot", "fixture-model", "API", "started", 1, "not probed", null),
        ];

        var evidence = LaunchRefusalEvidence.Render(rows);

        Assert.Contains("x&#124;y", evidence);
        Assert.Contains("&lt;default&gt;<br>second line", evidence);
        Assert.Contains("| synthetic-one |", evidence);
        Assert.Contains("| synthetic-two |", evidence);
        Assert.Contains("| refused | no | not probed |", evidence);
        Assert.Contains("| started | yes | not probed |", evidence);
        var json = evidence.Split("```json\n")[1].Split("\n```")[0];
        var saved = JsonSerializer.Deserialize<LaunchEvidenceRow[]>(json)!;
        Assert.Equal(rows, saved);
    }
}
