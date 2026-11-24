using AgentBridge.App.Core;
using Xunit;

// #3305 slice 3a: a script credential lives 30 days and is never extended silently, so the page says so before it runs out.
// The note comes from the host's own expiry (the latest among the script's live credentials) and nothing else; renewal stays the Rotate press.
public sealed class ScriptCredentialExpiryNoteTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 24, 8, 0, 0, TimeSpan.Zero);

    static ScriptCredentialRow Row(TimeSpan left, int live = 1) => new("dispatcher", "read+reply", Now + left, live, false, null);

    [Fact]
    public void A_credential_with_three_days_left_says_so()
    {
        Assert.Equal("expires in 3 days", Row(TimeSpan.FromDays(3)).ExpiryNote(Now));
        Assert.Equal("expires in 1 day", Row(TimeSpan.FromHours(5)).ExpiryNote(Now));
    }

    [Fact]
    public void A_credential_with_plenty_left_says_nothing()
    {
        Assert.Null(Row(TimeSpan.FromDays(20)).ExpiryNote(Now));
        Assert.Null(Row(TimeSpan.FromDays(8)).ExpiryNote(Now));
        Assert.NotNull(Row(TimeSpan.FromDays(7)).ExpiryNote(Now));
    }

    [Fact]
    public void An_expired_credential_says_expired_and_that_it_fails()
    {
        Assert.Equal("expired: the script's runs fail until it is rotated", Row(TimeSpan.FromDays(-1)).ExpiryNote(Now));
        Assert.Equal("expired: the script's runs fail until it is rotated", Row(TimeSpan.Zero).ExpiryNote(Now));
    }

    [Fact]
    public void During_a_rotation_the_note_follows_the_host_reported_latest_expiry_alone()
    {
        Assert.Null(Row(TimeSpan.FromDays(20), live: 2).ExpiryNote(Now));
        Assert.Equal("expires in 2 days", Row(TimeSpan.FromDays(2), live: 2).ExpiryNote(Now));
    }
}
