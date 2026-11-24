using AgentBridge.Protocol;
using Xunit;

public sealed class ComposerInputTests
{
    [Fact]
    public void A_plain_box_is_shown_as_it_is_with_the_digest_of_the_whole_text()
    {
        var input = ComposerInput.From("rerun the tests");
        Assert.Equal(("rerun the tests", false, "text", false), (input.Text, input.Truncated, input.Mode, input.Clearable));
        Assert.Empty(input.Marks);
        Assert.Equal(EscapedApproval.Shown("rerun the tests"), input.Digest);
    }

    [Fact]
    public void Hidden_characters_are_escaped_and_marked_as_the_host_shows_them()
    {
        var (shown, marks) = ShownText.RevealMarked("go‮ gol", rightToLeft: true);
        var input = ComposerInput.From("go‮ gol");
        Assert.Equal(shown, input.Text);
        Assert.Equal(marks.Select(m => new[] { m.Start, m.Length }), input.Marks.Select(m => m.ToArray()));
        Assert.Equal(EscapedApproval.Shown(shown), input.Digest);
    }

    [Fact]
    public void A_long_box_is_capped_but_the_digest_covers_all_of_it()
    {
        var whole = new string('a', ComposerInput.Cap + 40);
        var input = ComposerInput.From(whole);
        Assert.Equal((ComposerInput.Cap, true), (input.Text.Length, input.Truncated));
        Assert.Equal(EscapedApproval.Shown(whole), input.Digest);
    }

    [Fact]
    public void The_cut_never_splits_an_escape_or_a_surrogate_pair()
    {
        var atEscape = ComposerInput.From(new string('a', ComposerInput.Cap - 3) + "‮" + "bbbb");
        Assert.True(atEscape.Truncated);
        Assert.Equal(ComposerInput.Cap - 3, atEscape.Text.Length);
        Assert.Empty(atEscape.Marks);
        var atPair = ComposerInput.From(new string('a', ComposerInput.Cap - 1) + "\U0001F600" + "b");
        Assert.Equal(ComposerInput.Cap - 1, atPair.Text.Length);
    }
}
