using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Mac;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class LaunchFlowTests
{
    sealed class UnconfirmedStarter(IAgentStarter starter) : IAgentStarter
    {
        public string? Unavailable() => starter.Unavailable();
        public string? CommandLine(StartSpec spec, out string? problem) => starter.CommandLine(spec, out problem);
        public Started Start(StartSpec spec)
        {
            starter.Start(spec);
            throw new ManagedTmuxStartUnconfirmedException("the launch response was lost");
        }
        public bool Running(Started started) => starter.Running(started);
    }

    LaunchService UnconfirmedLauncher(LaunchConfig config, AttachmentStore? attachments = null) => new(source, probe, screens,
        new UnconfirmedStarter(starter), locator, new AgencyCatalog(agency), new LaunchServiceOptions
        {
            CopilotHome = f.CopilotHome, ClaudeHome = f.ClaudeHome, CodexHome = f.CodexHome,
            Config = config, PlatformSupported = true, IsSystemDirectory = _ => false,
        }, attachments: attachments);

    [Fact]
    public async Task An_uncertain_tmux_start_is_remembered_without_replaying_or_releasing_its_attachments()
    {
        using var store = new AttachmentStore(new AttachmentOptions { StagingRoot = Path.Combine(f.Root, "attachments") }, sweep: false);
        var launcher = UnconfirmedLauncher(Config(), store);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var file = Guid.NewGuid().ToString();
        store.Start("phone", new() { Attachment = file, Name = "shot.png", Size = png.Length, Sha256 = Attachments.Base64Url(System.Security.Cryptography.SHA256.HashData(png)) });
        store.Chunk("phone", new() { Attachment = file, Offset = 0, Data = Convert.ToBase64String(png) });
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", Attachments = [new() { Id = file }] };

        var result = await launcher.LaunchAsync(request, "phone");
        Assert.Equal(LaunchOutcome.Unconfirmed, result.Outcome);
        Assert.NotNull(result.Launched);
        Assert.Equal(result, await launcher.LaunchAsync(request, "phone"));
        Assert.Single(starter.Specs);
        Assert.Null(store.Claim("phone", [new() { Id = file }]).Claim);
        // The file may already be in the launched agent's prompt; consumption keeps its
        // staged copy available even though the upload ID cannot be handed over again.
        Assert.Contains(Directory.EnumerateFiles(store.StagingRoot, "*", SearchOption.AllDirectories), p => File.ReadAllBytes(p).SequenceEqual(png));
    }

    [Fact]
    public async Task An_uncertain_tmux_start_keeps_its_worktree_reserved_until_checked_at_the_machine()
    {
        Git(app, "init");
        Git(app, "config", "user.email", "fixture@example.invalid");
        Git(app, "config", "user.name", "Fixture");
        File.WriteAllText(Path.Combine(app, "file.txt"), "fixture");
        Git(app, "add", "file.txt");
        Git(app, "commit", "-m", "fixture");
        var config = Config(c => c with { Workspaces = [new("App", app, true)] });
        var launcher = UnconfirmedLauncher(config);
        var result = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App" }, "phone");
        Assert.Equal(LaunchOutcome.Unconfirmed, result.Outcome);
        Assert.NotNull(result.Launched?.Cwd);
        var listing = AgentBridge.Host.Delivery.Launch.Git.Run(app, ["worktree", "list", "--porcelain"]);
        Assert.Contains("locked " + Worktrees.ReservationPrefix, listing.Output);
    }
}
