using System.Text;
using AgentBridge.Protocol;
using Xunit;

// The counter spans an async read and counts the whole process. Run only this case without
// other collections so their allocations cannot consume its work budget (#2599).
[Collection(nameof(HistoryAllocationTests))]
public sealed class HistoryAllocationTests
{
    const string Id = "55555555-5555-4555-8555-555555555555";

    // Found in review of #292: each block of one message copied the whole response so far, so a
    // message of many small blocks cost the square of its length to read.
    [Fact]
    public async Task A_claude_message_of_many_blocks_is_gathered_in_bounded_work_and_keeps_its_end()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        const int blocks = 3000;
        Fixture.Append(transcript, [Claude.User("write a lot", 1), .. Enumerable.Range(0, blocks).Select(i => Claude.AssistantMessage("msg_1", 2, null, Claude.Text($"block {i:D4} " + new string('b', 200))))]);
        await using var source = f.Source();
        await source.RefreshAsync();

        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var page = await source.HistoryAsync(Id, null, 20);
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;

        var response = page.Turns!.Single().Parts.Single(p => p.Kind == HistoryPartKind.Response);
        Assert.EndsWith($"block {blocks - 1:D4} " + new string('b', 200), response.Text);
        Assert.True(response.Truncated);
        Assert.True(Encoding.UTF8.GetByteCount(response.Text!) <= HistoryLimits.Response);
        // Copying the whole response for every block allocates over a gigabyte here.
        Assert.True(allocated < 256L * 1024 * 1024, $"reading the message allocated {allocated:N0} bytes");
    }
}

[CollectionDefinition(nameof(HistoryAllocationTests), DisableParallelization = true)]
public sealed class HistoryAllocationCollection;
