using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// Who said what: the main agent's own words and its delegates (Attribution), read from what a host
// sends (docs/protocol/v1-sessions.md, Activity and Subagent). The PWA's cases, one for one
// (pwa/test/unit/attribution.test.mjs), so both clients show the same voices. The PWA's case for a
// source or own of the wrong shape is its reader's; the app's is below (Received, #446).
public sealed class AttributionTests
{
    static Session Session(Activity? activity = null, IReadOnlyList<Subagent>? subagents = null, int? more = null) =>
        Make.Session() with { Activity = activity ?? new Activity(), Subagents = subagents, SubagentsMore = more };

    static MainVoice Voice(Activity activity) => Attribution.MainVoiceOf(Session(activity));

    [Fact]
    public void A_delegates_response_is_never_shown_as_the_main_agents_its_own_text_is()
    {
        var v = Voice(new Activity { Response = "Bisect over the last 10 commits.", Source = new ActivitySource { Response = "agent-3a91" }, Own = new OwnActivity { Response = "Started 2 background agents." } });
        Assert.Equal("Started 2 background agents.", v.Response);
        Assert.True(v.Attributed);
    }

    [Fact]
    public void A_delegates_reasoning_beside_the_main_agents_response_leaves_each_in_its_place()
    {
        var v = Voice(new Activity { Response = "HEAD: 1 failing test.", Reasoning = "Check CI before saying so.", Source = new ActivitySource { Reasoning = "agent-c204" }, Own = new OwnActivity { Reasoning = "Two jobs: delegate both." } });
        Assert.Equal(("HEAD: 1 failing test.", "Two jobs: delegate both."), (v.Response, v.Reasoning));
    }

    [Fact]
    public void A_field_the_host_attributes_to_a_delegate_with_no_own_text_means_the_main_agent_has_none()
    {
        var v = Voice(new Activity { Response = "Bisect over the last 10 commits.", Reasoning = "Check CI.", Source = new ActivitySource { Response = "agent-3a91", Reasoning = "agent-c204" } });
        Assert.Equal((null, null, false), (v.Response, v.Reasoning, v.ResponseDropped));
    }

    [Fact]
    public void A_main_agent_response_the_budget_dropped_says_so_rather_than_showing_the_delegates()
    {
        var v = Voice(new Activity { Response = "Bisect over the last 10 commits.", Source = new ActivitySource { Response = "agent-3a91" }, Own = new OwnActivity { ResponseTruncated = true } });
        Assert.Equal((null, true), (v.Response, v.ResponseDropped));
        var cut = Voice(new Activity { Response = "Bisect.", Source = new ActivitySource { Response = "agent-3a91" }, Own = new OwnActivity { Response = "…the end of it.", ResponseTruncated = true } });
        Assert.Equal(("…the end of it.", true, false), (cut.Response, cut.ResponseTruncated, cut.ResponseDropped));
    }

    [Fact]
    public void An_older_host_with_no_source_shows_the_top_level_text_unchanged_and_unlabelled()
    {
        var v = Voice(new Activity { Response = "Done. The test now waits for the socket to drain.", ResponseTruncated = true, Reasoning = "Plan." });
        Assert.Equal(new MainVoice("Done. The test now waits for the socket to drain.", true, false, "Plan.", false), v);
    }

    [Fact]
    public void The_main_agents_own_text_fills_in_where_the_top_level_text_is_absent()
    {
        var v = Voice(new Activity { Own = new OwnActivity { Response = "From the skimmed transcript.", Reasoning = "Skimmed too." } });
        Assert.Equal(("From the skimmed transcript.", "Skimmed too."), (v.Response, v.Reasoning));
        // An empty top-level field is absent too, as the PWA reads it.
        var empty = Voice(new Activity { Response = "Top.", Reasoning = "", Own = new OwnActivity { Reasoning = "Skimmed." } });
        Assert.Equal("Skimmed.", empty.Reasoning);
    }

    // Read as absent, a source the app could not read made the delegate's text the main agent's:
    // "Main agent" on a delegate's words. The activity is not shown instead.
    [Theory]
    [InlineData("""{"response":7}""")]
    [InlineData("""{"reasoning":["agent-c204"]}""")]
    [InlineData("\"agent-3a91\"")]
    public void A_source_the_app_cannot_read_never_makes_a_delegates_text_the_main_agents(string source)
    {
        var node = JsonSerializer.SerializeToNode(Make.Session(), ProtocolJson.Options)!;
        node["activity"] = System.Text.Json.Nodes.JsonNode.Parse($$"""{"response":"Bisect over the last 10 commits.","reasoning":"A delegate's plan.","source":{{source}}}""");

        var v = Attribution.MainVoiceOf(Received.Read<Session>(JsonSerializer.SerializeToElement(node)));

        Assert.Equal(((string?)null, (string?)null), (v.Response, v.Reasoning));
    }

    [Fact]
    public void The_worked_example_shows_the_main_agents_own_words_not_either_delegates()
    {
        var example = JsonSerializer.Deserialize<Session>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "session-subagents.json")), ProtocolJson.Options)!;
        var v = Attribution.MainVoiceOf(example);
        Assert.Equal(("Started 2 background agents: run-suite and witness-logic.", "Two independent jobs: delegate both."), (v.Response, v.Reasoning));
    }

    static Subagent Delegate(string id, SubagentStatus status = SubagentStatus.Working, SubagentOutcome? outcome = null, string? parent = null, SubagentKind kind = SubagentKind.Background) =>
        new() { Id = id, Kind = kind, Name = $"Delegate {id}", Status = status, Outcome = outcome, Parent = parent };

    static IEnumerable<string> Rows(IEnumerable<DelegateEntry> list) => list.Select(r => $"{new string(' ', 2 * r.Depth)}{r.Id}:{r.State.Glyph.ToString().ToLowerInvariant()}");

    [Fact]
    public void Delegates_list_running_ones_first_then_idle_ones_each_nested_under_its_parent_with_the_finished_apart()
    {
        var d = Attribution.DelegatesOf(Session(subagents:
        [
            Delegate("idle", SubagentStatus.Idle, SubagentOutcome.Done),
            Delegate("a"),
            Delegate("a1", parent: "a", kind: SubagentKind.Task),
            Delegate("b", SubagentStatus.Waiting),
            Delegate("a1x", parent: "a1", kind: SubagentKind.Task),
            Delegate("done", SubagentStatus.Ended, SubagentOutcome.Done, kind: SubagentKind.Task),
            Delegate("done1", SubagentStatus.Ended, SubagentOutcome.Failed, "done", SubagentKind.Task),
            Delegate("orphan", SubagentStatus.Ended, parent: "a", kind: SubagentKind.Task),
        ], more: 2))!;

        Assert.Equal(["a:working", "  a1:working", "    a1x:working", "b:needs", "idle:idle"], Rows(d.Active));
        Assert.Equal(["done:done", "  done1:failed", "orphan:ended"], Rows(d.Finished));
        Assert.Equal(2, d.More);
        // Who started each, for what the indent can't say aloud, or at all across the two lists.
        var parents = d.Active.Concat(d.Finished).ToDictionary(r => r.Id, r => r.ParentName);
        Assert.Equal(new string?[] { null, "Delegate a1", "Delegate done", "Delegate a" }, new[] { parents["a"], parents["a1x"], parents["done1"], parents["orphan"] });
    }

    [Fact]
    public void A_finished_delegates_glyph_comes_from_its_outcome()
    {
        var d = Attribution.DelegatesOf(Session(subagents: [Delegate("x", SubagentStatus.Ended, SubagentOutcome.Failed), Delegate("y", SubagentStatus.Ended, SubagentOutcome.Done)]))!;
        Assert.Equal([(StatusGlyph.Failed, "Failed"), (StatusGlyph.Done, "Done")], d.Finished.Select(r => (r.State.Glyph, r.State.Words)));
    }

    [Fact]
    public void A_parent_loop_from_a_broken_host_still_lists_every_delegate_once()
    {
        var d = Attribution.DelegatesOf(Session(subagents: [Delegate("a", parent: "b"), Delegate("b", parent: "a"), Delegate("c", parent: "c")]))!;
        Assert.Equal(["a", "b", "c"], d.Active.Select(r => r.Id).Order());
    }

    [Fact]
    public void A_host_that_lists_no_delegates_gets_no_delegates_whatever_its_background_count()
    {
        Assert.Null(Attribution.DelegatesOf(Session(new Activity { Background = Background.Of(2, 0) })));
        Assert.Equal((0, 0, 3), Attribution.DelegatesOf(Session(subagents: [], more: 3)) is { } d ? (d.Active.Count, d.Finished.Count, d.More) : default);
        // The budget can drop every entry and keep only the count: still a host that lists them.
        Assert.Equal(3, Attribution.DelegatesOf(Session(more: 3))?.More);
    }

    [Fact]
    public void A_running_delegate_says_how_long_it_has_run_a_finished_one_that_it_finished()
    {
        var now = DateTimeOffset.Parse("2026-11-01T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("4m", Attribution.TimeOf(Delegate("w") with { StartedAt = now.AddMinutes(-4) }, now));
        Assert.Equal("finished 3m ago", Attribution.TimeOf(Delegate("e", SubagentStatus.Ended) with { EndedAt = now.AddMinutes(-3) }, now));
        Assert.Equal("finished", Attribution.TimeOf(Delegate("i", SubagentStatus.Idle), now));
    }
}
