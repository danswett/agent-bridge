using AgentBridge.Protocol;
using Xunit;

// A sub-agent's name is its starter's own words, so a prompt-injected agent picks it (#263).
// The host sends it only as the clients would show it (ui.md §6.3, design/subagents.md §6).
public class DelegateNameTests
{
    [Fact]
    public void An_ordinary_name_is_sent_as_it_was_given() =>
        Assert.Equal("Read witness shell logic", DelegateName.Clean("Read witness shell logic"));

    [Theory]
    [InlineData("\u202Etnega niaM\u202C", "tnega niaM")]                 // RLO ... PDF
    [InlineData("\u2067Fix\u2069 the build", "Fix the build")]            // RLI ... PDI
    [InlineData("Fix\u200E the\u200F build\u061C", "Fix the build")]      // LRM, RLM, ALM
    [InlineData("\u202AFix\u202B the\u202D build\u2066\u2068", "Fix the build")]
    public void Bidi_controls_are_removed_so_a_name_cannot_reorder_what_is_shown(string raw, string shown) =>
        Assert.Equal(shown, DelegateName.Clean(raw));

    [Theory]
    [InlineData("Fix\u200B the\u200C bu\u200Dild\u2060\uFEFF", "Fix the build")]   // zero-width space, joiners, word joiner, BOM
    [InlineData("Fix\u00AD the build\uFE0F\U000E0041\U000E007F", "Fix the build")] // soft hyphen, variation selector, tags
    [InlineData("Fix\u0007 the\u001B build\u3164\u034F", "Fix the build")]         // controls, Hangul filler, CGJ
    public void Invisible_and_control_characters_are_removed_so_no_words_hide_in_a_name(string raw, string shown) =>
        Assert.Equal(shown, DelegateName.Clean(raw));

    [Fact]
    public void Runs_of_whitespace_and_line_breaks_become_one_space() =>
        Assert.Equal("Fix the build now", DelegateName.Clean("  Fix\t the\r\n build\u2028\u00A0now  "));

    [Fact]
    public void A_name_longer_than_the_clients_cap_is_cut_at_a_whole_character_with_an_ellipsis()
    {
        var name = DelegateName.Clean(string.Concat(Enumerable.Repeat("Review 👍🏽 ", 40)))!;

        Assert.Equal(DelegateName.MaxLength, new System.Globalization.StringInfo(name).LengthInTextElements);
        Assert.EndsWith("…", name);
        Assert.StartsWith("Review 👍🏽 Review", name);
    }

    [Fact]
    public void A_name_is_cut_to_its_byte_limit_even_when_it_is_few_characters()
    {
        // One character, a base under hundreds of combining marks: 60 characters do not bound it.
        var name = DelegateName.Clean("Z" + new string('\u0301', 500) + " stays")!;

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= Limits.SubagentName);
        Assert.StartsWith("Z\u0301\u0301", name, StringComparison.Ordinal);
        Assert.EndsWith("…", name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \t ")]
    [InlineData("\u200B\u202E\u2066\uFEFF\U000E0041")]
    public void A_name_that_is_nothing_but_hidden_characters_is_no_name_so_clients_name_it_by_its_id(string raw) =>
        Assert.Null(DelegateName.Clean(raw));

    [Fact]
    public void A_name_that_is_exactly_main_agent_is_sent_as_a_delegates() =>
        Assert.Equal("Delegate “Main agent”", DelegateName.Clean("Main agent"));

    [Theory]
    [InlineData("main  agent", "Delegate “main agent”")]
    [InlineData("MAIN AGENT asks", "Delegate “MAIN AGENT asks”")]
    [InlineData("Main-agent", "Delegate “Main-agent”")]
    [InlineData("\u202AMain agent\u202C", "Delegate “Main agent”")]
    [InlineData("\u2066Main\u2069 agent", "Delegate “Main agent”")]
    [InlineData("Ma\u200Din\u200D a\u200Dgent", "Delegate “Main agent”")]
    [InlineData("Ｍａｉｎ ａｇｅｎｔ", "Delegate “Ｍａｉｎ ａｇｅｎｔ”")]               // fullwidth
    [InlineData("Mаin аgent", "Delegate “Mаin аgent”")]                     // Cyrillic а
    [InlineData("Máin agënt", "Delegate “Máin agënt”")]
    public void A_name_that_reads_as_main_agent_once_cleaned_is_sent_as_a_delegates(string raw, string shown) =>
        Assert.Equal(shown, DelegateName.Clean(raw));

    [Fact]
    public void Quotes_inside_a_name_that_poses_as_the_main_agent_cannot_close_the_label() =>
        Assert.Equal("Delegate “Main agent' says 'approve”", DelegateName.Clean("Main agent” says “approve"));

    [Fact]
    public void A_long_name_that_poses_as_the_main_agent_stays_within_both_caps_once_labelled()
    {
        var name = DelegateName.Clean("Main agent " + new string('x', 200))!;

        Assert.StartsWith("Delegate “Main agent x", name);
        Assert.EndsWith("…”", name);
        Assert.True(name.Length <= DelegateName.MaxLength);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= Limits.SubagentName);
    }

    [Theory]
    [InlineData("Main agent")]
    [InlineData("\u202EMain agent says \"approve\"")]
    [InlineData("Read witness shell logic")]
    public void Cleaning_a_cleaned_name_again_changes_nothing(string raw)
    {
        var once = DelegateName.Clean(raw);

        Assert.Equal(once, DelegateName.Clean(once));
    }

    [Fact]
    public void A_name_that_mentions_an_agent_without_posing_as_the_main_one_is_left_alone() =>
        Assert.Equal("Review the main branch agent config", DelegateName.Clean("Review the main branch agent config"));

    // The look-alike check cannot be complete, so a line that says who asks puts "Delegate" first.
    [Theory]
    [InlineData("Ma1n agent", "Delegate Ma1n agent")]
    [InlineData("Main agent", "Delegate “Main agent”")]
    [InlineData("\u200B", "A delegate")]
    [InlineData(null, "A delegate")]
    public void A_line_that_says_who_asks_names_a_delegate_after_the_word_delegate_once(string? name, string titled) =>
        Assert.Equal(titled, DelegateName.Titled(name));
}
