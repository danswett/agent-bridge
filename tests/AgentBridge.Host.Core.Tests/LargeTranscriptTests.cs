using System.Diagnostics;
using System.Text;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Conversation;
using AgentBridge.Host.Core.Transcripts;
using AgentBridge.Protocol;
using Xunit;
using Xunit.Abstractions;

public class LargeTranscriptTests(ITestOutputHelper output)
{
    sealed class Counter : ILineSink
    {
        public long Bytes;
        public bool Limited;
        public void Reset() { Bytes = 0; Limited = false; }
        public void Skim(ReadOnlyMemory<byte> line) => Bytes += line.Length + 1;
        public void Parse(ReadOnlyMemory<byte> line) => Bytes += line.Length + 1;
        public void Skipped() => Limited = true;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_250_MiB_transcript_opens_at_its_newest_reply_with_bounded_work(bool giantFirstLine, bool alreadyOpen)
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "synthetic.jsonl");
        var table = "| lane | head | result | next | owner |\n| --- | --- | --- | --- | --- |\n"
            + string.Concat(Enumerable.Repeat("| Windows | " + new string('a', 128) + " | pass | read | builder |\n", 70));
        var noise = Encoding.UTF8.GetBytes(Copilot.Event("tool.execution_complete", new { content = table }, 1) + "\n");
        var padding = Enumerable.Repeat((byte)'x', noise.Length).ToArray();
        var options = new SessionSourceOptions { CopilotHome = f.CopilotHome, ClaudeHome = f.ClaudeHome, CodexHome = f.CodexHome };
        var sink = new Counter();
        var tail = new TranscriptTail(path, options);
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            file.Write(Encoding.UTF8.GetBytes("{}\n"));
            file.Flush();
            if (alreadyOpen) { Assert.Equal(TailOutcome.Restarted, tail.Read(sink)); }
            if (giantFirstLine) { file.Write(Encoding.UTF8.GetBytes("{\"padding\":\"")); }
            while (file.Position < 250L * 1024 * 1024)
            {
                file.Write(giantFirstLine ? padding : noise);
            }
            if (giantFirstLine) { file.Write(Encoding.UTF8.GetBytes("\"}\n")); }
            foreach (var line in new[] { Copilot.User("Newest prompt", 2), Copilot.TurnStart(3), Copilot.Assistant(table + "\nNewest reply", 4), Copilot.TurnEnd(5) })
            {
                file.Write(Encoding.UTF8.GetBytes(line + "\n"));
            }
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        tail.Read(sink);
        var page = new HistoryReader(new HistoryBudgets(), new byte[32]).Read("synthetic", AgentKind.Copilot, path, null, 1, new HistorySeed(new Dictionary<string, string?>(), new Dictionary<string, string>()), true);
        watch.Stop();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"opened {new FileInfo(path).Length} bytes in {watch.Elapsed.TotalMilliseconds:F1} ms; allocated {bytes}; consumed {sink.Bytes}");
        Assert.True(sink.Limited);
        Assert.InRange(sink.Bytes, 1, 8 * 1024 * 1024);
        Assert.Equal("Newest prompt", Assert.Single(page.Turns!).Prompt!.Text);
        Assert.EndsWith("Newest reply", page.Turns![0].Parts.Single(p => p.Kind == HistoryPartKind.Response).Text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Opening took {watch.Elapsed}");
        Assert.InRange(bytes, 0, 96L * 1024 * 1024);
    }

    [Fact]
    public void History_beyond_the_scan_budget_is_visibly_limited_instead_of_silently_empty()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Root, "limited.jsonl");
        var noise = Encoding.UTF8.GetBytes(Copilot.Event("tool.execution_complete", new { content = new string('x', 16000) }, 1) + "\n");
        using (var file = File.Create(path))
        {
            while (file.Position < 10 * 1024 * 1024) { file.Write(noise); }
        }
        var page = new HistoryReader(new HistoryBudgets(), new byte[32]).Read("synthetic", AgentKind.Copilot, path, null, 1, new HistorySeed(new Dictionary<string, string?>(), new Dictionary<string, string>()), true);
        Assert.Empty(page.Turns!);
        Assert.True(page.Start?.Limited);
        Assert.True(page.Partial);
    }
}
