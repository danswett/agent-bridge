using System.Text.Json;
using AgentBridge.Host.Core.Diagnostics;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

// What a finished launch leaves in the diagnostics journal (#737 S4): its outcome and which agent,
// once, and nothing of what the person asked for.
public sealed partial class LaunchFlowTests
{
    static IReadOnlyList<JournalEntry> Entries(DiagnosticsJournal journal) => journal.Read(int.MaxValue, int.MaxValue).Entries;

    [Fact]
    public async Task A_finished_launch_journals_its_outcome_and_agent_once_and_a_repeat_adds_nothing()
    {
        var journal = new DiagnosticsJournal(TimeProvider.System);
        var launcher = Launcher(Config(), journal: journal);
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App" };

        var first = await launcher.LaunchAsync(request, "phone");
        await launcher.LaunchAsync(request, "phone");

        Assert.Equal(LaunchOutcome.Started, first.Outcome);
        var entry = Assert.Single(Entries(journal));
        Assert.Equal(JournalKind.LaunchRequested, entry.Kind);
        Assert.Equal(JournalFamily.Launch, entry.Family);
        Assert.Equal(JournalOutcome.Ok, entry.Outcome);
        Assert.Equal(2, entry.Code);
    }

    [Fact]
    public async Task A_launch_that_failed_or_was_refused_journals_that_without_the_workspace_prompt_or_reason()
    {
        var journal = new DiagnosticsJournal(TimeProvider.System);
        var launcher = Launcher(Config(), journal: journal);
        starter.Throws = new IOException("secret-reason-text");

        var failed = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", Prompt = "secret-prompt-text" }, "phone");
        var refused = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "claude", Workspace = "secret-workspace-name", Prompt = "secret-prompt-text" }, "phone");

        Assert.Equal(LaunchOutcome.Failed, failed.Outcome);
        Assert.Equal(LaunchOutcome.Refused, refused.Outcome);
        var entries = Entries(journal);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e is { Outcome: JournalOutcome.Failed, Code: 2 });
        Assert.Contains(entries, e => e is { Outcome: JournalOutcome.Refused, Code: 3 });
        var text = string.Join("\n", entries.Select(e => JsonSerializer.Serialize(e, ProtocolJson.Options)));
        foreach (var secret in new[] { "secret-prompt-text", "secret-workspace-name", "secret-reason-text", app, "App" })
        {
            Assert.DoesNotContain(secret, text);
        }
    }

    [Fact]
    public async Task A_launch_with_no_known_agent_journals_no_code_and_a_launch_without_a_journal_still_runs()
    {
        var journal = new DiagnosticsJournal(TimeProvider.System);

        await Launcher(Config(), journal: journal).LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "nonesuch", Workspace = "App" }, "phone");
        var plain = await Launcher(Config()).LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App" }, "phone");

        Assert.Null(Assert.Single(Entries(journal)).Code);
        Assert.Equal(LaunchOutcome.Started, plain.Outcome);
    }
}
