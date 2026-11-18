using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class FleetNamesTests
{
    AgentCredentials? titleCredentials;
    SessionDisplayTitles? displayTitles;
    async Task UseDisplayTitleStateAsync()
    {
        await source.DisposeAsync();
        displayTitles = new(fixture.Root);
        source = new SessionSource(fixture.Options(stateFile: Path.Combine(fixture.Root, "sessions-listed.json")),
            fixture.Processes, fixture.Time, presentation: displayTitles.Apply);
        await source.RefreshAsync();
        service = MakeService();
    }

    async Task<JsonElement> TitleAsync(object data)
    {
        var result = await LocalApi.Handler(service, Control(), callers, credentials: titleCredentials)(channel, "session.title", JsonSerializer.SerializeToElement(data));
        Assert.NotNull(result);
        return JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
    }

    static string? DisplayTitle(Session session)
    {
        var json = JsonSerializer.SerializeToElement(session, ProtocolJson.Options);
        return json.TryGetProperty("displayTitle", out var title) ? title.GetString() : null;
    }

    [Theory]
    [InlineData(CallerKind.App)]
    [InlineData(CallerKind.HostCli)]
    public async Task Display_titles_are_host_state_and_survive_a_store_reload(CallerKind kind)
    {
        await UseDisplayTitleStateAsync();
        callers.Current = new FixedCallerGuard(new Caller(kind, "person"));
        var before = source.Get(Id)!;
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var result = await TitleAsync(new { id = Id, title = "Shared \u65e5\u672c\u8a9e" });
        Assert.Equal(Id, result.GetProperty("id").GetString());
        Assert.Equal("Shared \u65e5\u672c\u8a9e", result.GetProperty("displayTitle").GetString());
        Assert.Equal("Shared \u65e5\u672c\u8a9e", DisplayTitle(source.Get(Id)!));
        Assert.Equal(before.Title, source.Get(Id)!.Title);
        Assert.True(source.Get(Id)!.UpdatedAt > before.UpdatedAt);
        await UseDisplayTitleStateAsync();
        Assert.Equal("Shared \u65e5\u672c\u8a9e", DisplayTitle(Assert.Single(source.List().Sessions)));
        var namedAt = source.Get(Id)!.UpdatedAt;
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var clear = await TitleAsync(new { id = Id, title = "   " });
        Assert.Equal(JsonValueKind.Null, clear.GetProperty("displayTitle").ValueKind);
        Assert.Null(DisplayTitle(source.Get(Id)!));
        Assert.Equal(before.Title, source.Get(Id)!.Title);
        Assert.True(source.Get(Id)!.UpdatedAt > namedAt);
        await UseDisplayTitleStateAsync();
        Assert.Null(DisplayTitle(Assert.Single(source.List().Sessions)));
    }

    [Theory]
    [InlineData(CallerKind.Agent, "agent session")]
    [InlineData(CallerKind.Agent, "MCP child")]
    [InlineData(CallerKind.Other, "script")]
    public async Task Display_title_changes_refuse_non_person_callers(CallerKind kind, string description)
    {
        await UseDisplayTitleStateAsync();
        callers.Current = new FixedCallerGuard(new Caller(kind, description));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => TitleAsync(new { id = Id, title = "Not allowed" }));
        Assert.Contains("only the app or host command line", refused.Message, StringComparison.Ordinal);
        Assert.Null(DisplayTitle(source.Get(Id)!));
    }

    [Fact]
    public async Task Display_title_changes_reclassify_an_already_open_pipe()
    {
        await UseDisplayTitleStateAsync();
        await TitleAsync(new { id = Id, title = "Original" });
        callers.Current = new FreshAgentCaller();
        await Assert.ThrowsAsync<InvalidOperationException>(() => TitleAsync(new { id = Id, title = "Not allowed" }));
        Assert.Equal("Original", DisplayTitle(source.Get(Id)!));
    }

    [Theory]
    [InlineData(CallerKind.App, "live")]
    [InlineData(CallerKind.HostCli, "live")]
    [InlineData(CallerKind.App, "expired")]
    [InlineData(CallerKind.App, "revoked")]
    public async Task Display_titles_refuse_credentialed_connections_even_when_the_caller_looks_like_a_person(CallerKind kind, string state)
    {
        await UseDisplayTitleStateAsync();
        callers.Current = new FixedCallerGuard(new Caller(kind, "person"));
        titleCredentials = new(fixture.Root, "host", fixture.Time);
        var at = fixture.Time.GetUtcNow();
        var token = titleCredentials.Mint("session", "launch", 100, at, AgentProtocol.ScopeReadReplyLaunch, lifetime: TimeSpan.FromMinutes(1));
        titleCredentials.Accept(channel, titleCredentials.Check(token, new ProcessAncestry([new PeerProcess(100, at, "agent.exe")], true)).Accepted!);
        if (state == "expired") { fixture.Time.Advance(TimeSpan.FromMinutes(2)); }
        if (state == "revoked") { titleCredentials.Revoke("session", "test"); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => TitleAsync(new { id = Id, title = "Not allowed" }));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "display-titles.json")));
    }

    [Theory]
    [InlineData("""{"id":"$id","title":"bad\nname"}""")]
    [InlineData("""{"id":"$id","title":"bad\u200bname"}""")]
    [InlineData("""{"id":"$id","title":7}""")]
    [InlineData("""{"id":"$id","title":null}""")]
    [InlineData("""{"id":"missing","title":"Name"}""")]
    public async Task Invalid_display_title_requests_leave_the_previous_title_unchanged(string json)
    {
        await UseDisplayTitleStateAsync();
        await TitleAsync(new { id = Id, title = "Original" });
        await Assert.ThrowsAsync<ArgumentException>(() => TitleAsync(JsonSerializer.Deserialize<JsonElement>(json.Replace("$id", Id, StringComparison.Ordinal))));
        await Assert.ThrowsAsync<ArgumentException>(() => TitleAsync(new { id = Id, title = new string('x', 121) }));
        Assert.Equal("Original", DisplayTitle(source.Get(Id)!));
    }

    [Fact]
    public async Task Display_title_changes_do_not_change_fleet_names_or_registry_publication()
    {
        await UseDisplayTitleStateAsync();
        await CallAsync(new { id = Id, name = "Fleet name" });
        var saved = File.ReadAllText(PathToNames);
        var published = await PublishedNameAsync();
        await TitleAsync(new { id = Id, title = "A different display title" });
        Assert.Equal(saved, File.ReadAllText(PathToNames));
        Assert.Equal(published, await PublishedNameAsync());
        Assert.Equal("Served session", source.Get(Id)!.Title);
    }
}
