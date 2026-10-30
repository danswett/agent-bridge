using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// The demo host the app runs with --demo: every example session reaches the window's view
// model, and it answers as a cooperative host would.
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
}
