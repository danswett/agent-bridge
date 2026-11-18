using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Protocol;
using Xunit;

public sealed class SessionDisplayTitlesTests
{
    [Fact]
    public void Titles_and_clears_survive_reload_without_changing_the_agents_title()
    {
        using var fixture = new Fixture();
        var store = new SessionDisplayTitles(fixture.Root);
        var session = new Session { Id = "session", Title = "Agent title", Agent = AgentKind.Copilot,
            Status = SessionStatus.Idle, StatusSince = Fixture.T0, UpdatedAt = Fixture.T0, Activity = new() };
        store.Set(session.Id, "Shared \u65e5\u672c\u8a9e", Fixture.T0.AddSeconds(1));
        var loaded = new SessionDisplayTitles(fixture.Root).Apply(session);
        var wire = JsonSerializer.SerializeToElement(loaded, ProtocolJson.Options);
        Assert.Equal("Shared \u65e5\u672c\u8a9e", wire.GetProperty("displayTitle").GetString());
        Assert.Equal(session.Title, loaded.Title);
        Assert.Equal(Fixture.T0.AddSeconds(1), loaded.UpdatedAt);
        store.Set(session.Id, " ", Fixture.T0.AddSeconds(2));
        loaded = new SessionDisplayTitles(fixture.Root).Apply(session);
        Assert.Null(new SessionDisplayTitles(fixture.Root).Get(session.Id));
        Assert.Equal(Fixture.T0.AddSeconds(2), loaded.UpdatedAt);
        Assert.Equal(session.Title, loaded.Title);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "fleet-names.json")));
    }

    [Theory]
    [InlineData("bad\nname")]
    [InlineData("bad\u200bname")]
    public void Invalid_titles_do_not_replace_durable_state(string title)
    {
        using var fixture = new Fixture();
        var store = new SessionDisplayTitles(fixture.Root);
        store.Set("session", new string('x', 120), Fixture.T0);
        Assert.Throws<ArgumentException>(() => store.Set("session", title, Fixture.T0));
        Assert.Throws<ArgumentException>(() => store.Set("session", new string('x', 121), Fixture.T0));
        Assert.Equal(new string('x', 120), new SessionDisplayTitles(fixture.Root).Get("session"));
    }

    [Fact]
    public void Unreadable_title_state_is_not_treated_as_an_empty_store()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "display-titles.json"), "{broken");
        Assert.Throws<JsonException>(() => new SessionDisplayTitles(fixture.Root));
    }
}
