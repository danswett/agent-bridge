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
        Assert.Equal("75 waiting at a prompt, 75 idle with work: n-0, n-1, n-10, n-100, n-101, n-102, n-103, n-104, n-105, n-106 and 140 more", d.Body);
    }

    [Fact]
    public void A_final_notice_says_it_is_the_last_reminder_and_a_mixed_digest_says_for_how_many()
    {
        Assert.EndsWith("(last reminder)", WatchDigest.Compose([N(WatchRule.Prompt, "a", final: true)], Label)!.Value.Body);
        var mixed = WatchDigest.Compose([N(WatchRule.Prompt, "a", final: true), N(WatchRule.Prompt, "b"), N(WatchRule.Prompt, "c")], Label)!.Value;
        Assert.EndsWith("(last reminder for 1)", mixed.Body);
    }

    [Theory]
    [InlineData(10, "n-9")]
    [InlineData(11, "n-8 and 1 more")]
    public void Exactly_ten_are_listed_with_no_and_more_and_eleven_say_and_one_more(int count, string tail)
    {
        var all = Enumerable.Range(0, count).Select(i => N(WatchRule.Prompt, i.ToString())).ToList();
        Assert.EndsWith(tail, WatchDigest.Compose(all, Label)!.Value.Body);
    }

    static string Names(string body) => body[(body.IndexOf(": ", StringComparison.Ordinal) + 2)..];

    [Fact]
    public void A_name_of_twenty_four_characters_is_kept_and_one_of_twenty_five_is_cut_to_twenty_four()
    {
        Assert.Equal(new string('x', 24), Names(WatchDigest.Compose([N(WatchRule.Prompt, "a")], _ => new string('x', 24))!.Value.Body));
        Assert.Equal(new string('x', 24), Names(WatchDigest.Compose([N(WatchRule.Prompt, "a")], _ => new string('x', 25))!.Value.Body));
    }

    // P1 (Quality, #4335): a cut by UTF-16 unit left a lone high surrogate, which the push serializer turns into U+FFFD.
    [Fact]
    public void A_cut_never_splits_an_emoji_or_leaves_a_lone_surrogate()
    {
        var label = new string('x', 23) + char.ConvertFromUtf32(0x1F600) + "tail";
        var name = Names(WatchDigest.Compose([N(WatchRule.Prompt, "a")], _ => label)!.Value.Body);
        Assert.Equal(new string('x', 23), name);
        Assert.DoesNotContain(name, c => char.IsSurrogate(c));
        var fits = new string('x', 22) + char.ConvertFromUtf32(0x1F600) + "tail";
        Assert.Equal(new string('x', 22) + char.ConvertFromUtf32(0x1F600), Names(WatchDigest.Compose([N(WatchRule.Prompt, "a")], _ => fits)!.Value.Body));
    }

    [Fact]
    public void A_cut_keeps_a_combining_mark_with_its_letter()
    {
        var label = new string('x', 23) + "e" + (char)0x0301 + "tail";
        Assert.Equal(new string('x', 23), Names(WatchDigest.Compose([N(WatchRule.Prompt, "a")], _ => label)!.Value.Body));
    }

    [Fact]
    public void Control_characters_and_direction_overrides_are_dropped_and_an_empty_or_missing_name_reads_a_session()
    {
        string Name(string? l) => Names(WatchDigest.Compose([N(WatchRule.Prompt, "a")], _ => l!)!.Value.Body);
        Assert.Equal("a b c d", Name("a" + (char)0x202E + "b" + (char)13 + (char)10 + "c" + (char)7 + "d"));
        Assert.Equal("evil name", Name(((char)0x202E) + "evil" + (char)0x2066 + " name" + (char)0x200F));
        Assert.Equal("a session", Name(""));
        Assert.Equal("a session", Name("   " + (char)0x202E + " "));
        Assert.Equal("a session", Name(null));
    }

    [Fact]
    public void The_same_sessions_in_any_order_read_the_same_so_the_one_replaced_slot_does_not_flap()
    {
        var a = WatchDigest.Compose([N(WatchRule.Prompt, "b"), N(WatchRule.Prompt, "a"), N(WatchRule.Prompt, "c")], Label)!.Value;
        var b = WatchDigest.Compose([N(WatchRule.Prompt, "c"), N(WatchRule.Prompt, "b"), N(WatchRule.Prompt, "a")], Label)!.Value;
        Assert.Equal(a, b);
        Assert.EndsWith("n-a, n-b, n-c", a.Body);
    }

    [Fact]
    public void Last_reminder_for_N_counts_only_the_names_listed()
    {
        var all = Enumerable.Range(0, 12).Select(i => N(WatchRule.Prompt, i.ToString("00"), final: i == 11)).ToList();
        var body = WatchDigest.Compose(all, Label)!.Value.Body;
        Assert.EndsWith("and 2 more", body);
        Assert.DoesNotContain("last reminder", body);
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
