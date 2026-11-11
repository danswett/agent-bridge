using System.Text.Json;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;
using Xunit.Abstractions;

[Trait("Category", "Network")]
public sealed class LaunchRefusalAcceptanceTests(ITestOutputHelper output) : LaunchFlowFixture
{
    [Theory]
    [InlineData("agent", "synthetic-machine")]
    [InlineData("model", "synthetic-machine")]
    [InlineData("workspace", "synthetic-machine")]
    [InlineData("other-agent-model", "synthetic-machine")]
    [InlineData("offered", "synthetic-machine")]
    [InlineData("model", "another-synthetic-machine")]
    public async Task A_synthetic_machines_offered_choices_bound_what_can_start(string scenario, string machine)
    {
        machineName = machine;
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
                reason = $"agent not offered by {machine}: '{agentId}'; it offers {string.Join(", ", offered.Agents.Select(a => a.Id))}";
                break;
            case "model":
                model = "unlisted-model";
                Assert.DoesNotContain(offered.Agents, a => a.Models.Contains(model));
                reason = $"model not offered by {machine}: '{model}' for {chosen.Label}; it offers {string.Join(", ", chosen.Models)}";
                break;
            case "workspace":
                workspace = "unlisted-workspace";
                Assert.DoesNotContain(offered.Workspaces, w => w.Label == workspace);
                reason = $"workspace not offered by {machine}: '{workspace}'; it offers {string.Join(", ", offered.Workspaces.Select(w => w.Label))}";
                break;
            case "other-agent-model":
                model = offered.Agents.Where(a => a.Id != chosen.Id).SelectMany(a => a.Models)
                    .First(m => !chosen.Models.Contains(m));
                reason = $"model not offered by {machine}: '{model}' for {chosen.Label}; it offers {string.Join(", ", chosen.Models)}";
                break;
        }

        var result = await LaunchAsync(ch, new { agent = agentId, model, workspace, prompt = "reply OK" });
        var row = new LaunchEvidenceRow(machine, agentId, model, workspace,
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

    [Fact]
    public async Task A_refusal_bounds_its_displayed_list_without_removing_choices_from_launch_options()
    {
        machineName = "synthetic-machine";
        var models = Enumerable.Range(0, 16).Select(i => $"model-{i:D2}-" + new string('x', 100)).ToArray();
        Serve(Config(c => c with
        {
            Agents = new Dictionary<string, AgentConfig> { ["copilot"] = new() { Models = models } },
        }));
        var ch = await OpenAsync();
        var before = await RequestAsync(ch, LaunchProtocol.Options, new { });
        var offered = before.GetProperty("agents")[0].GetProperty("models").EnumerateArray().Select(m => m.GetString()).ToArray();
        Assert.Equal(models, offered);

        var result = await LaunchAsync(ch, new { agent = "copilot", model = "unlisted-model", workspace = "App" });

        Assert.Equal("refused", result.Outcome_);
        Assert.StartsWith("model not offered by synthetic-machine: 'unlisted-model' for Copilot; it offers ", result.Reason);
        Assert.Contains(models[0], result.Reason);
        Assert.EndsWith("... (see launch.options for all 16 choices)", result.Reason);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.Reason!) < 768);
        Assert.Null(result.Session);
        Assert.Equal(JsonValueKind.Undefined, result.Launched.ValueKind);
        Assert.Empty(starter.Specs);
        Assert.Empty(source.Snapshot());
        Assert.Empty(screens.Typed);
        var after = await RequestAsync(ch, LaunchProtocol.Options, new { });
        Assert.Equal(offered, after.GetProperty("agents")[0].GetProperty("models").EnumerateArray().Select(m => m.GetString()));
        output.WriteLine(result.Reason);
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
