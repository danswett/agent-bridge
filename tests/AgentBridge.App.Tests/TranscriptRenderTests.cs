using System.Collections.Specialized;
using System.Text;
using System.Text.RegularExpressions;
using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class TranscriptRenderTests
{
    [Fact]
    public void Large_messages_and_five_lane_tables_keep_the_newest_history_message()
    {
        Assert.InRange(Encoding.UTF8.GetByteCount(LargeMessageTranscriptFixture.Response), 15 * 1024, 16 * 1024);

        var rows = HistoryDraw.Rows(LargeMessageTranscriptFixture.Turns, null);

        Assert.Equal(["earlier", "large", "newest"], rows.Select(r => r.Id));
        Assert.Equal(LargeMessageTranscriptFixture.Prose.TrimEnd(), rows[1].Blocks[0].Text);
        var table = Assert.IsType<MdTable>(rows[1].Blocks[1].Block);
        Assert.True(table.HasHeader);
        Assert.Equal(6, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(5, row.Count));
        Assert.All(table.Rows.Skip(1), row => Assert.Equal(LargeMessageTranscriptFixture.Hash, string.Concat(row[2].Select(i => Assert.IsType<MdText>(i).Text))));
        Assert.Equal("Latest response ending.", rows[1].Blocks[^1].Text);
        Assert.Equal(LargeMessageTranscriptFixture.Newest, Assert.Single(rows[^1].Blocks).Text);
    }

    [Fact]
    public void A_large_live_reply_keeps_its_table_when_a_new_block_arrives_without_resetting()
    {
        var shown = new ConversationItems();
        ConversationBlocks.Update(shown, MarkdownModel.Parse(LargeMessageTranscriptFixture.Response));
        var table = shown.Single(b => b.Block is MdTable);
        var changes = new List<NotifyCollectionChangedAction>();
        shown.CollectionChanged += (_, e) => changes.Add(e.Action);

        ConversationBlocks.Update(shown, MarkdownModel.Parse(LargeMessageTranscriptFixture.Response + "\n\n" + LargeMessageTranscriptFixture.Newest));

        Assert.Same(table, shown.Single(b => b.Block is MdTable));
        Assert.Equal(LargeMessageTranscriptFixture.Newest, shown[^1].Text);
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
    }

    // WinUI is not loaded by the offline suite; keep its diagnostic wiring covered here.
    [Fact]
    public void Conversation_render_failures_log_only_metadata_and_preserve_the_exception()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        var source = File.ReadAllText(Path.Combine(root, "src", "AgentBridge.App", "SessionView.Conversation.cs"));
        var start = source.IndexOf("    void ShowResponse(", StringComparison.Ordinal);
        var end = source.IndexOf("    void HookConversation(", start, StringComparison.Ordinal);
        var response = source[start..end];

        Assert.Contains("catch (Exception", response, StringComparison.Ordinal);
        var failure = response[response.IndexOf("catch (Exception", StringComparison.Ordinal)..];
        var line = Assert.Single(Regex.Matches(failure, @"AppLog\.Write\((?<line>[^\r\n]+)\);").Cast<Match>()).Groups["line"].Value;
        Assert.Contains("render-failure", line, StringComparison.Ordinal);
        Assert.Contains("chars=", line, StringComparison.Ordinal);
        Assert.Contains("blocks=", line, StringComparison.Ordinal);
        Assert.Contains("GetType()", line, StringComparison.Ordinal);
        Assert.DoesNotContain(".Message", line, StringComparison.Ordinal);
        Assert.DoesNotContain(".ToString()", line, StringComparison.Ordinal);
        Assert.DoesNotContain("{e}", line, StringComparison.Ordinal);
        Assert.DoesNotContain("{from}", line, StringComparison.Ordinal);
        Assert.Contains("throw;", failure, StringComparison.Ordinal);
    }
}
