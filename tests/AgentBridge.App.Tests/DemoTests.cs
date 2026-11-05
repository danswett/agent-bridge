using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// The demo host the app runs with --demo: every example session reaches the window's view
// model, and it answers as a cooperative host would.
[Trait("Category", "Network")]
public sealed class DemoTests
{
    [Fact]
    public async Task The_demo_shows_every_example_session_and_takes_an_answer()
    {
        await using var demo = DemoHost.Start();
        await using var connection = new HostConnection(ct => LocalClient.ConnectAsync(demo.Endpoint, TimeSpan.FromSeconds(1), ct), new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        var deliveries = new Deliveries(connection, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(50) };
        var main = new MainViewModel(connection, new CardContext { Deliveries = deliveries, Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel());
        // Started once the window's model listens, as the app does: it builds cards from the
        // upserts it hears.
        connection.Start();

        // The usage rows are refreshed when the link says connected, which can come a moment
        // after the sessions' upserts: they arrive on the connection's other thread.
        await Wait.UntilAsync(() => main.Sessions.Count == DemoHost.Examples().Count && main.Usage.Rows.Count == 2, "every demo session and the usage");
        var choice = main.Sessions.Single(c => c.Id == "demo-choice");
        choice.Question!.Choose(choice.Question.Fields[0].Options[0]);

        await Wait.UntilAsync(() => choice.Question is null && choice.HasReceipt, "the question to be answered");
        Assert.Equal("Your answer: Delivered. The session has your answer.", choice.ReceiptText);
    }

    [Fact]
    public async Task Heavy_sessions_added_reach_the_window_and_the_heavy_one_gives_its_whole_tool_history()
    {
        await using var demo = DemoHost.Start();
        await using var connection = new HostConnection(ct => LocalClient.ConnectAsync(demo.Endpoint, TimeSpan.FromSeconds(1), ct), new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, new LockedDispatcher()), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel());
        connection.Start();
        await Wait.UntilAsync(() => main.Sessions.Count == DemoHost.Examples().Count, "every demo session");

        await demo.AddHeavyAsync();

        await Wait.UntilAsync(() => main.Sessions.Count == DemoHost.Examples().Count + DemoHost.HeavySessions, "the heavy sessions");
        var heavy = await connection.ReadAsync<Session>(Ops.SessionGet, new { id = DemoHost.HeavyId }, TimeSpan.FromSeconds(5));
        Assert.Equal(Limits.History, heavy.Activity.History?.Count);
    }
}

// The self-test's live-resize pass (#765) needs a window as heavy as a real one: more than 15
// sessions, a conversation of over 200 items, a full tool history and a strip of delegates, all
// within what a host may send.
public sealed class HeavyDemoTests
{
    [Fact]
    public void The_heavy_demo_is_as_heavy_as_a_real_window_and_within_what_a_host_may_send()
    {
        var sessions = DemoHost.Heavy(DateTimeOffset.UtcNow);
        var heavy = sessions.Single(s => s.Id == DemoHost.HeavyId);
        var blocks = MarkdownModel.Parse(heavy.Activity.Response);
        var items = blocks.Sum(b => b is MdList list ? list.Items.Count : 1);

        Assert.True(DemoHost.Examples().Count + sessions.Count > 15);
        Assert.True(items >= 200, $"{items} items");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(heavy.Activity.Response!) <= Limits.Response);
        Assert.Equal(Limits.History, heavy.Activity.History!.Count);
        Assert.InRange(heavy.Subagents!.Count, 9, Limits.Subagents);
        Assert.Same(heavy, SessionBudget.Fit(heavy));
        Assert.DoesNotContain(sessions.Select(s => s.Id), DemoHost.Examples().Select(s => s.Id).Contains);
    }

    [Fact]
    public void The_heavy_response_ends_with_a_run_of_linked_paragraphs_after_the_blocks_it_had_before()
    {
        var heavy = DemoHost.Heavy(DateTimeOffset.UtcNow).Single(s => s.Id == DemoHost.HeavyId);
        var blocks = MarkdownModel.Parse(heavy.Activity.Response);
        static bool Linked(MdBlock b) => b is MdParagraph p && p.Inlines.Any(i => i is MdLink);
        var run = blocks.Skip(blocks.Count - 1 - DemoHost.HeavyLinkedRun).Take(DemoHost.HeavyLinkedRun).ToList();

        Assert.Equal(DemoHost.HeavyLinkedRun, run.Count);
        Assert.All(run, b => Assert.True(Linked(b)));
        Assert.False(Linked(blocks[blocks.Count - 2 - DemoHost.HeavyLinkedRun]));
        Assert.False(Linked(blocks[^1]));
        // The 193 blocks the resize pass's other checks used, so the block in their middle is the same.
        Assert.Equal(193, blocks.Count - DemoHost.HeavyLinkedRun);
    }
}
