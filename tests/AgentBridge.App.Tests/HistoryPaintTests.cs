using AgentBridge.App;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class HistoryPaintTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Network")]
    public async Task Host_metadata_marks_unattributed_transcript_prompts_on_load_and_arrival(bool arrival)
    {
        await using var host = new HistoryPaintHost();
        await host.AppendAsync(0, agent: false);
        await host.AppendAsync(1);
        Assert.Equal(UserMessageFrom.Agent, host.Current.Activity.UserMessageFrom);
        await host.AppendAsync(2, agent: false);
        await using var connection = new HostConnection(ct => LocalClient.ConnectAsync(host.Endpoint, ct: ct), new LockedDispatcher());
        connection.Start();
        await connection.WhenConnectedAsync().WaitAsync(FakeHost.Patience);
        var history = new SessionHistory(connection, HistoryPaintHost.Id);
        await history.LoadNewestAsync();
        if (arrival)
        {
            await host.AppendAsync(3);
            Assert.Equal(UserMessageFrom.Agent, host.Current.Activity.UserMessageFrom);
            await history.LoadNewestAsync();
        }
        Assert.True(host.HistoryRequests > 0);
        Assert.Equal(HistoryLoad.StartReached, history.State);
        var agent = history.Turns.Single(t => t.Prompt?.Text == HistoryPaintHost.Prompt(arrival ? 3 : 1));
        Assert.Equal(UserMessageFrom.Agent, agent.Prompt!.From);
        Assert.Equal(arrival ? HistoryPaintHost.Reply(3) : HistoryPaintHost.Reply(1), Assert.Single(agent.Parts).Text);
        Assert.Null(history.Turns.Single(t => t.Prompt?.Text == HistoryPaintHost.Prompt(2)).Prompt!.From);
        var row = HistoryDraw.Rows([agent], null).Single();
        Assert.False(row.Disclosure!.Open);
        Assert.Null(row.Prompt);
    }

    [Fact]
    public void The_host_path_frame_diagnostic_is_part_of_the_published_app_self_test()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        var entry = File.ReadAllText(Path.Combine(root, "src", "AgentBridge.App", "SelfTest.cs"));
        Assert.Contains("await CheckHistoryFormattedFromFirstFrame(window, main, Check);", entry, StringComparison.Ordinal);
    }
}
