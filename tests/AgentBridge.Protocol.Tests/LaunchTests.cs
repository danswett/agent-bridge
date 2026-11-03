using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

public class LaunchTests
{
    const string Uuid = "6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b";

    static LaunchRequest Launch(string? agent = null, string? workspace = null, string? resume = null, string? model = null, string? permissions = null,
        string? prompt = null, IReadOnlyList<AttachmentRef>? attachments = null) =>
        new() { Request = Uuid, Agent = agent, Workspace = workspace, Resume = resume, Model = model, Permissions = permissions, Prompt = prompt, Attachments = attachments };

    [Fact]
    public void A_launch_may_leave_every_setting_to_the_machine()
    {
        Assert.Null(LaunchProtocol.Check(Launch()));
        Assert.Null(LaunchProtocol.Check(Launch(agent: "agency", workspace: "Bridge", model: "claude-opus-5.5", permissions: "allowAll", prompt: "Review the open PR")));
        // An empty model is the agent's own default, over the machine's.
        Assert.Null(LaunchProtocol.Check(Launch(model: "")));
    }

    [Fact]
    public void A_launch_names_only_launchers_and_permissions_the_protocol_knows()
    {
        Assert.StartsWith("unknown agent 'gemini'", LaunchProtocol.Check(Launch(agent: "gemini")));
        Assert.StartsWith("unknown permissions 'yolo'", LaunchProtocol.Check(Launch(permissions: "yolo")));
    }

    [Theory]
    [InlineData("--dangerously-skip-permissions")]
    [InlineData("gpt 6")]
    [InlineData("a\"b")]
    [InlineData("x;rm")]
    public void A_model_that_could_be_read_as_an_option_or_split_on_a_command_line_is_refused(string model) =>
        Assert.EndsWith("is not a model any agent offers", LaunchProtocol.Check(Launch(model: model)));

    [Fact]
    public void A_resume_reopens_in_its_own_folder_so_naming_a_workspace_too_is_refused()
    {
        Assert.Equal("a resumed session reopens in its own folder; name a workspace or a session to resume, not both",
            LaunchProtocol.Check(Launch(resume: "0f3c", workspace: "Bridge")));
        Assert.Null(LaunchProtocol.Check(Launch(resume: "0f3c")));
    }

    [Fact]
    public void A_prompt_is_bounded_and_may_not_start_a_command()
    {
        Assert.Null(LaunchProtocol.Check(Launch(prompt: new string('a', LaunchProtocol.MaxPrompt))));
        Assert.StartsWith("the prompt is longer than 16 KiB", LaunchProtocol.Check(Launch(prompt: new string('a', LaunchProtocol.MaxPrompt + 1))));
        Assert.StartsWith("a prompt cannot start with /", LaunchProtocol.Check(Launch(prompt: " \n/help")));
        Assert.StartsWith("a prompt cannot start with !", LaunchProtocol.Check(Launch(prompt: "!rm -rf .")));
    }

    [Fact]
    public void Attachments_are_uuids_sent_first_at_most_ten_and_each_once()
    {
        var a = new AttachmentRef { Id = "4c1d7e2a-9b3f-4e6a-8c5d-0f1e2d3c4b5a" };
        Assert.Null(LaunchProtocol.Check(Launch(attachments: [a])));
        Assert.Equal("the same attachment is in the launch twice", LaunchProtocol.Check(Launch(attachments: [a, a])));
        Assert.StartsWith("each attachment must be the UUID", LaunchProtocol.Check(Launch(attachments: [new AttachmentRef { Id = "x" }])));
        Assert.StartsWith("a launch carries at most 10", LaunchProtocol.Check(Launch(attachments: [.. Enumerable.Range(0, 11).Select(_ => new AttachmentRef { Id = Guid.NewGuid().ToString() })])));
    }

    [Fact]
    public void A_prompt_reaches_the_agent_as_one_line()
    {
        Assert.Equal("Fix the build   and open a PR", LaunchProtocol.Flatten("  Fix the build\r\n and open\ta PR\n"));
        Assert.Equal("one two", LaunchProtocol.Flatten("one\u2028two"));
        Assert.Equal("", LaunchProtocol.Flatten(null));
        Assert.DoesNotContain(LaunchProtocol.Flatten("a\tb\u0007c\u2029d"), c => char.IsControl(c) || c is '\u2028' or '\u2029');
    }

    [Fact]
    public void A_launch_reads_from_and_writes_to_the_contract_json_and_an_unknown_outcome_is_unconfirmed()
    {
        var read = JsonSerializer.Deserialize<LaunchRequest>($$"""{"request":"{{Uuid}}","agent":"copilot","workspace":"Bridge","effort":"high","permissions":"ask","prompt":"go","future":true}""", ProtocolJson.Options)!;
        Assert.Equal(("copilot", "Bridge", "high", "ask", "go"), (read.Agent, read.Workspace, read.Effort, read.Permissions, read.Prompt));
        Assert.Equal("""{"outcome":"started","session":"0f3c","launched":{"agent":"copilot","workspace":"Bridge","cwd":"C:\\w","worktree":true,"permissions":"allowAll"}}""",
            JsonSerializer.Serialize(new LaunchResult(LaunchOutcome.Started)
            {
                Session = "0f3c",
                Launched = new LaunchedWith { Agent = "copilot", Workspace = "Bridge", Cwd = @"C:\w", Worktree = true, Permissions = "allowAll" },
            }, ProtocolJson.Options));
        Assert.Equal("""{"outcome":"busy","reason":"why"}""", JsonSerializer.Serialize(new LaunchResult(LaunchOutcome.Busy, "why"), ProtocolJson.Options));
        // Something may have started: never read as refused.
        Assert.Equal(LaunchOutcome.Unconfirmed, JsonSerializer.Deserialize<LaunchResult>("""{"outcome":"queued"}""", ProtocolJson.Options)!.Outcome);
    }

    [Fact]
    public void Launch_options_name_each_agent_by_kind_and_leave_out_what_is_unset()
    {
        var options = new LaunchOptions
        {
            Agents = [new LaunchAgent { Id = "agency", Label = "Agency", Kind = AgentKind.Copilot, Models = ["auto"], Profiles = ["home"], Defaults = new LaunchTuning { Profile = "home" } }],
            Workspaces = [new LaunchWorkspace { Label = "Bridge", Path = @"C:\repos\bridge", Isolate = true }],
            Defaults = new LaunchDefaults { Agent = "agency", Workspace = "Bridge", Permissions = "ask" },
        };
        Assert.Equal("""{"agents":[{"id":"agency","label":"Agency","kind":"copilot","models":["auto"],"efforts":[],"contexts":[],"profiles":["home"],"defaults":{"profile":"home"}}],"workspaces":[{"label":"Bridge","path":"C:\\repos\\bridge","isolate":true}],"resumable":[],"defaults":{"agent":"agency","workspace":"Bridge","permissions":"ask"}}""",
            JsonSerializer.Serialize(options, ProtocolJson.Options));
    }
}
