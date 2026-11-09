using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class DetailCopyTests
{
    readonly FakeTimeProvider clock = new();

    [Fact]
    public void Copy_puts_exactly_the_id_on_the_clipboard_and_says_Copied_then_Copy_again()
    {
        string? put = null;
        var copy = new DetailCopy(t => { put = t; return true; }, time: clock);

        Assert.True(copy.Copy("8f43bdfc-0001"));
        Assert.Equal("8f43bdfc-0001", put);
        Assert.Equal(DetailCopy.Done, copy.Label);
        clock.Advance(DetailCopy.ResetAfter);
        Assert.Equal(DetailCopy.Ready, copy.Label);
    }

    [Fact]
    public void Copy_says_Copy_failed_when_the_clipboard_refuses_or_is_held_by_another_program()
    {
        var refused = new DetailCopy(_ => false, time: clock);
        Assert.False(refused.Copy("x"));
        Assert.Equal(DetailCopy.Failed, refused.Label);

        var held = new DetailCopy(_ => throw new UnauthorizedAccessException(), time: clock);
        Assert.False(held.Copy("x"));
        Assert.Equal(DetailCopy.Failed, held.Label);
    }

    [Fact]
    public void A_second_copy_keeps_its_word_showing_past_the_first_copys_reset()
    {
        var copy = new DetailCopy(_ => true, time: clock);
        copy.Copy("x");
        clock.Advance(DetailCopy.ResetAfter - TimeSpan.FromMilliseconds(100));
        copy.Copy("x");
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(DetailCopy.Done, copy.Label);
        clock.Advance(DetailCopy.ResetAfter);
        Assert.Equal(DetailCopy.Ready, copy.Label);
    }
}
