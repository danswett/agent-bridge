using AgentBridge.Host.Core.Watch;
using Xunit;

// Session watch S2 (docs/design/session-watch.md, #4262): ONE person digest per machine per pass, never one push per session.
public class WatchDigestTests
{
    static WatchNotice N(WatchRule rule, string id, bool final = false) => new(rule, id, null, TimeSpan.FromMinutes(7), final);
    static string Label(string id) => "n-" + id;

    [Fact]
    public void Nothing_to_say_is_no_digest() => Assert.Null(WatchDigest.Compose([], Label));

    [Fact]
    public void A_single_prompt_names_the_session_and_says_what_it_is_doing()
    {
        var d = WatchDigest.Compose([N(WatchRule.Prompt, "a")], Label)!.Value;
        Assert.Equal("1 session needs you", d.Title);
        Assert.Equal("1 waiting at a prompt: n-a", d.Body);
    }

    [Fact]
    public void A_hundred_and_fifty_stalls_make_one_digest_that_lists_ten_and_counts_the_rest()
    {
        var all = Enumerable.Range(0, 150).Select(i => N(i % 2 == 0 ? WatchRule.Prompt : WatchRule.Idle, i.ToString())).ToList();
        var d = WatchDigest.Compose(all, Label)!.Value;
        Assert.Equal("150 sessions need you", d.Title);
        Assert.Equal("75 waiting at a prompt, 75 idle with work: n-0, n-1, n-2, n-3, n-4, n-5, n-6, n-7, n-8, n-9 and 140 more", d.Body);
    }

    [Fact]
    public void Missing_and_finished_sessions_are_counted_in_plain_words_and_finished_is_an_offer_not_an_order()
    {
        var d = WatchDigest.Compose([N(WatchRule.Prompt, "a"), N(WatchRule.Missing, "b"), N(WatchRule.Finished, "c"), N(WatchRule.Finished, "d")], Label)!.Value;
        Assert.Equal("4 sessions need you", d.Title);
        Assert.Equal("1 waiting at a prompt, 1 missing, 2 finished and idle (end them?): n-a, n-b, n-c, n-d", d.Body);
    }

    [Fact]
    public void A_final_notice_says_it_is_the_last_reminder_and_a_mixed_digest_says_for_how_many()
    {
        Assert.EndsWith("(last reminder)", WatchDigest.Compose([N(WatchRule.Prompt, "a", final: true)], Label)!.Value.Body);
        var mixed = WatchDigest.Compose([N(WatchRule.Prompt, "a", final: true), N(WatchRule.Prompt, "b"), N(WatchRule.Prompt, "c")], Label)!.Value;
        Assert.EndsWith("(last reminder for 1)", mixed.Body);
    }

    [Theory]
    [InlineData(10, "")]
    [InlineData(11, " and 1 more")]
    public void Exactly_ten_are_listed_with_no_and_more_and_eleven_say_and_one_more(int count, string tail)
    {
        var all = Enumerable.Range(0, count).Select(i => N(WatchRule.Prompt, i.ToString())).ToList();
        Assert.EndsWith("n-9" + tail, WatchDigest.Compose(all, Label)!.Value.Body);
    }

    [Fact]
    public void A_very_long_label_is_cut_so_the_and_more_tail_survives_the_push_size_limit()
    {
        var all = Enumerable.Range(0, 20).Select(i => N(WatchRule.Prompt, i.ToString())).ToList();
        var d = WatchDigest.Compose(all, id => new string('x', 500))!.Value;
        Assert.EndsWith("and 10 more", d.Body);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(d.Body) < 512);
    }
}
