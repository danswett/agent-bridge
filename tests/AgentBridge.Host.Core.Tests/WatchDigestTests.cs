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
    public void A_final_notice_says_it_is_the_last_reminder_and_the_text_carries_no_timestamp()
    {
        var d = WatchDigest.Compose([N(WatchRule.Prompt, "a", final: true)], Label)!.Value;
        Assert.EndsWith("(last reminder)", d.Body);
        Assert.DoesNotContain("2026", d.Body);
    }
}
