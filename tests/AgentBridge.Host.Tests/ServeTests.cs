using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The host's Protocol v1 service end to end: fixture sessions read by the real
// SessionSource, served through the in-process relay over a Noise channel, to a client.
[Trait("Category", "Network")]
public sealed class ServeTests : IAsyncLifetime
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    const string Other = "99999999-9999-4999-8999-999999999999";
    const string Third = "77777777-7777-4777-8777-777777777777";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly Fixture f = new();
    TestRelay relay = null!;
    SessionSource source = null!;
    SessionService service = null!;
    Device host = null!;
    Device client = null!;
    string events = "";

    public async Task InitializeAsync()
    {
        events = f.CopilotSession(Id, 100, name: "Served session");
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.ToolStart("c1", "powershell", new { command = "dir" }, 3));
        f.Processes.Run(AgentKind.Copilot, 100);
        // Real time, not the fixture's fake clock: coalesced upserts must flush on their own
        // while the relay carries them.
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"));

        relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        host.Hub.Handler = service.HandleAsync;
        client = await relay.ConnectAsync(token, "Phone", "pwa");
    }

    public async Task DisposeAsync()
    {
        await relay.DisposeAsync();
        await source.DisposeAsync();
        f.Dispose();
    }

    async Task<SecureChannel> OpenAsync()
    {
        await client.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        return await client.Hub.OpenAsync(host.Id);
    }

    static JsonElement Result(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result");
    }

    static async Task<ChannelEvent> NextAsync(SecureChannel ch)
    {
        using var cts = new CancellationTokenSource(Patience);
        return await ch.Events.ReadAsync(cts.Token);
    }

    // Reads events up to and including sessions.synced.
    static async Task<List<ChannelEvent>> SnapshotAsync(SecureChannel ch)
    {
        var got = new List<ChannelEvent>();
        do { got.Add(await NextAsync(ch)); } while (got[^1].Name != Events.SessionsSynced);
        return got;
    }

    // A change the client shows, to the fixture's session.
    async Task ChangeAsync(string text, int second)
    {
        Fixture.Append(events, Copilot.ToolDone("c1", second), Copilot.Assistant(text, second + 1));
        await source.RefreshAsync();
    }

    [Fact]
    public async Task Hello_names_the_machine_and_offers_only_reading_sessions()
    {
        var ch = await OpenAsync();
        var hello = Result(await ch.RequestAsync(Ops.Hello, new { }));

        Assert.Equal(1, hello.GetProperty("protocol").GetInt32());
        Assert.Equal("Test host", hello.GetProperty("machine").GetProperty("name").GetString());
        Assert.Equal("windows", hello.GetProperty("machine").GetProperty("os").GetString());
        Assert.Equal([Capabilities.SessionsRead, Capabilities.SessionsPatch, Capabilities.HelloChanged, Capabilities.SessionsFocus, HistoryProtocol.Capability], hello.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task Listing_and_getting_return_the_machines_sessions_in_contract_form()
    {
        var ch = await OpenAsync();

        var list = Result(await ch.RequestAsync(Ops.SessionsList, new { }));
        var only = Assert.Single(list.GetProperty("sessions").EnumerateArray());
        Assert.Equal(Id, only.GetProperty("id").GetString());
        Assert.Equal("Served session", only.GetProperty("title").GetString());
        Assert.False(only.GetProperty("activity").TryGetProperty("history", out _));
        Assert.False(list.TryGetProperty("next", out _));

        var full = Result(await ch.RequestAsync(Ops.SessionGet, new { id = Id }));
        Assert.Equal("powershell", full.GetProperty("activity").GetProperty("history")[0].GetProperty("name").GetString());

        var gone = Result(await ch.RequestAsync(Ops.SessionGet, new { id = Other }));
        Assert.True(gone.GetProperty("missing").GetBoolean());
    }

    [Fact]
    public async Task Getting_one_sub_agent_returns_its_own_words_and_tools_and_missing_for_one_the_session_does_not_have()
    {
        Fixture.Append(events, Copilot.ToolStart("t1", "task", new { description = "Run the suite", prompt = "Run it", mode = "background" }, 4),
            Copilot.SubagentStarted("t1", "agent-1", 4), Copilot.User("Run it", 4, "agent-1"), Copilot.TurnStart(4, "agent-1"),
            Copilot.Assistant("", 5, reasoning: "start with the slow ones", tools: ["s1"], agentId: "agent-1"),
            Copilot.ToolStart("s1", "powershell", new { command = "dotnet test" }, 5, "agent-1"));
        await source.RefreshAsync();
        var ch = await OpenAsync();

        var listed = Assert.Single(Result(await ch.RequestAsync(Ops.SessionsList, new { })).GetProperty("sessions")[0].GetProperty("subagents").EnumerateArray());
        Assert.False(listed.TryGetProperty("history", out _));

        var agent = Result(await ch.RequestAsync(Ops.SessionGet, new { id = Id, agent = "agent-1" }));
        Assert.Equal("Run the suite", agent.GetProperty("name").GetString());
        Assert.Equal("start with the slow ones", agent.GetProperty("reasoning").GetString());
        Assert.Equal("powershell", Assert.Single(agent.GetProperty("history").EnumerateArray()).GetProperty("name").GetString());

        Assert.True(Result(await ch.RequestAsync(Ops.SessionGet, new { id = Id, agent = "agent-2" })).GetProperty("missing").GetBoolean());
        Assert.True(Result(await ch.RequestAsync(Ops.SessionGet, new { id = Other, agent = "agent-1" })).GetProperty("missing").GetBoolean());
    }

    [Fact]
    public async Task Getting_without_an_id_and_unknown_ops_are_refused_without_ending_the_channel()
    {
        var ch = await OpenAsync();

        var noId = await ch.RequestAsync(Ops.SessionGet, new { });
        Assert.False(noId.GetProperty("ok").GetBoolean());
        var unknown = await ch.RequestAsync("session.stop", new { id = Id });
        Assert.False(unknown.GetProperty("ok").GetBoolean());

        Assert.True(ch.IsOpen);
        Result(await ch.RequestAsync(Ops.Hello, new { }));
    }

    [Fact]
    public async Task Subscribing_sends_the_snapshot_then_synced_then_each_live_change_with_consecutive_seq()
    {
        var ch = await OpenAsync();

        var result = Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { }));
        Assert.Equal(1, result.GetProperty("count").GetInt32());
        var snapshot = await SnapshotAsync(ch);
        Assert.Equal([Events.SessionUpsert, Events.SessionsSynced], snapshot.Select(e => e.Name));
        Assert.Equal(Id, snapshot[0].Data.GetProperty("id").GetString());
        Assert.Equal(result.GetProperty("revision").GetInt64(), snapshot[1].Data.GetProperty("revision").GetInt64());

        await ChangeAsync("listed the folder", 4);
        var live = await NextAsync(ch);
        Assert.Equal(Events.SessionUpsert, live.Name);
        Assert.Equal("listed the folder", live.Data.GetProperty("activity").GetProperty("response").GetString());
        Assert.Equal([1L, 2L, 3L], snapshot.Append(live).Select(e => e.Seq));

        f.Processes.Exit(AgentKind.Copilot, 100);
        await source.RefreshAsync();
        var removed = await NextAsync(ch);
        Assert.Equal((Events.SessionRemove, Id, 4L), (removed.Name, removed.Data.GetProperty("id").GetString(), removed.Seq));
    }

    [Fact]
    public async Task Subscribing_again_replaces_the_subscription_resends_the_snapshot_and_continues_seq()
    {
        var ch = await OpenAsync();
        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { }));
        var first = await SnapshotAsync(ch);

        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { }));
        var second = await SnapshotAsync(ch);
        Assert.Equal([Events.SessionUpsert, Events.SessionsSynced], second.Select(e => e.Name));
        Assert.Equal(first[^1].Seq + 1, second[0].Seq);
        Assert.Equal(1, service.Subscribers);

        // One subscription, so one upsert for one change: a second running alongside would
        // send it twice.
        await ChangeAsync("only once", 4);
        var live = await NextAsync(ch);
        Assert.Equal("only once", live.Data.GetProperty("activity").GetProperty("response").GetString());
        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ch.Events.ReadAsync(quiet.Token));
    }

    [Fact]
    public async Task Two_subscribes_sent_at_once_leave_one_subscription()
    {
        var ch = await OpenAsync();

        var both = await Task.WhenAll(
            ch.RequestAsync(Ops.SessionsSubscribe, new { }),
            ch.RequestAsync(Ops.SessionsSubscribe, new { }));
        Assert.All(both, r => Result(r));
        await HostWait.UntilAsync(() => service.Subscribers == 1, "one subscription");

        // Drain both snapshots (the first may be cut short by its replacement), then one
        // change must arrive exactly once.
        var names = new List<string>();
        using (var settle = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            try { while (true) { names.Add((await ch.Events.ReadAsync(settle.Token)).Name); } }
            catch (OperationCanceledException) { }
        }
        Assert.Equal(Events.SessionsSynced, names[^1]);
        await ChangeAsync("after both", 4);
        Assert.Equal("after both", (await NextAsync(ch)).Data.GetProperty("activity").GetProperty("response").GetString());
        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ch.Events.ReadAsync(quiet.Token));
    }

    [Fact]
    public async Task Subscriptions_replaced_on_a_long_lived_channel_are_let_go()
    {
        var ch = await OpenAsync();
        var made = new List<WeakReference>();
        service.FeedCreated += feed => made.Add(new WeakReference(feed));

        for (var i = 0; i < 5; i++)
        {
            Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { }));
            await SnapshotAsync(ch);
        }
        Assert.Equal(5, made.Count);

        // Every replaced feed is collectable while the channel stays open; only the current
        // one is still held.
        await HostWait.UntilAsync(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return made.Take(4).All(w => !w.IsAlive);
        }, "the replaced subscriptions to be collected", Patience);
        Assert.True(made[4].IsAlive);
        Assert.True(ch.IsOpen);
    }

    [Fact]
    public async Task A_subscription_ends_when_its_channel_does()
    {
        var ch = await OpenAsync();
        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { }));
        await SnapshotAsync(ch);
        Assert.Equal(1, service.Subscribers);

        await client.DisposeAsync();

        await HostWait.UntilAsync(() => service.Subscribers == 0, "the subscription to end with its channel", Patience);
    }

    [Fact]
    public async Task Separate_channels_each_get_their_own_subscription_and_seq()
    {
        var a = await OpenAsync();
        var b = await OpenAsync();
        Result(await a.RequestAsync(Ops.SessionsSubscribe, new { }));
        await SnapshotAsync(a);
        Result(await b.RequestAsync(Ops.SessionsSubscribe, new { }));
        var bSnapshot = await SnapshotAsync(b);
        Assert.Equal(1L, bSnapshot[0].Seq);

        await ChangeAsync("to both", 4);
        Assert.Equal("to both", (await NextAsync(a)).Data.GetProperty("activity").GetProperty("response").GetString());
        Assert.Equal("to both", (await NextAsync(b)).Data.GetProperty("activity").GetProperty("response").GetString());
        Assert.Equal(2, service.Subscribers);
    }

    // A service for a run of the host named by its instance, as the real host's hello says.
    void Run(string instance)
    {
        service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0", Instance: instance));
        host.Hub.Handler = service.HandleAsync;
    }

    static JsonObject Json(JsonElement e) => JsonNode.Parse(e.GetRawText())!.AsObject();

    [Fact]
    public async Task A_client_that_asks_for_patches_gets_each_live_change_as_a_patch_on_the_upsert_it_holds()
    {
        var ch = await OpenAsync();
        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { patch = true }));
        var snapshot = await SnapshotAsync(ch);
        var held = Json(snapshot[0].Data);

        await ChangeAsync("listed the folder", 4);
        var live = await NextAsync(ch);

        Assert.Equal(Events.SessionPatch, live.Name);
        var patch = Received.Read<SessionPatch>(live.Data);
        Assert.Equal(snapshot[0].Seq, patch.Base);
        var applied = SessionPatches.Apply(held, snapshot[0].Seq, patch);
        Assert.Equal("listed the folder", applied["activity"]!["response"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(SessionPatches.Json(source.Snapshot().Single()), applied));
        Assert.True(live.Data.GetRawText().Length * 2 < snapshot[0].Data.GetRawText().Length, live.Data.GetRawText());
        Assert.True(patch.Revision <= source.Revision);

        f.Processes.Exit(AgentKind.Copilot, 100);
        await source.RefreshAsync();
        var removed = await NextAsync(ch);
        Assert.Equal((Events.SessionRemove, source.Revision), (removed.Name, removed.Data.GetProperty("revision").GetInt64()));
    }

    [Fact]
    public async Task Hello_offers_patches_and_a_client_that_does_not_ask_still_gets_whole_upserts()
    {
        var ch = await OpenAsync();
        Assert.Contains(Capabilities.SessionsPatch, Result(await ch.RequestAsync(Ops.Hello, new { })).GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { }));
        await SnapshotAsync(ch);

        await ChangeAsync("whole", 4);

        var live = await NextAsync(ch);
        Assert.Equal(Events.SessionUpsert, live.Name);
        Assert.Equal("Served session", live.Data.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Resuming_on_a_new_channel_to_the_same_run_sends_only_the_sessions_changed_since()
    {
        Run("run-1");
        var otherEvents = f.CopilotSession(Other, 101, name: "Quiet session");
        f.Processes.Run(AgentKind.Copilot, 101);
        await source.RefreshAsync();
        var first = await OpenAsync();
        Result(await first.RequestAsync(Ops.SessionsSubscribe, new { patch = true }));
        var revision = (await SnapshotAsync(first))[^1].Data.GetProperty("revision").GetInt64();

        await ChangeAsync("while away", 4);
        var second = await OpenAsync();
        var result = Result(await second.RequestAsync(Ops.SessionsSubscribe, new { patch = true, since = new { instance = "run-1", revision } }));

        Assert.True(result.GetProperty("resumed").GetBoolean());
        Assert.Equal(1, result.GetProperty("count").GetInt32());
        var resumed = await SnapshotAsync(second);
        Assert.Equal([Events.SessionUpsert, Events.SessionsSynced], resumed.Select(e => e.Name));
        Assert.Equal("while away", resumed[0].Data.GetProperty("activity").GetProperty("response").GetString());
        Assert.Equal([Id, Other], resumed[1].Data.GetProperty("live").EnumerateArray().Select(e => e.GetString()));

        // Nothing of the quiet session was sent on this channel, so its first change comes whole.
        Fixture.Append(otherEvents, Copilot.User("wake", 5));
        await source.RefreshAsync();
        var woke = Received.Read<SessionPatch>((await NextAsync(second)).Data);
        Assert.Equal((Other, (long?)null), (woke.Id, woke.Base));
    }

    // A client subscribed for patches that shows the fixture's session, beside a second one it
    // does not; the quiet interval on a fake clock, the source's own 250 ms on the real one. The
    // second session's first change after a quiet spell goes at once, and its next is held.
    async Task<(SecureChannel Channel, FakeTimeProvider Time, SessionService.Feed Feed, long Before)> FocusedWithAChangeHeldAsync()
    {
        var otherEvents = f.CopilotSession(Other, 101, name: "Quiet session");
        f.Processes.Run(AgentKind.Copilot, 101);
        await source.RefreshAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        SessionService.Feed? feed = null;
        service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0")) { Time = time };
        service.FeedCreated += created => feed = (SessionService.Feed)created;
        host.Hub.Handler = service.HandleAsync;
        var ch = await OpenAsync();
        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { patch = true }));
        await SnapshotAsync(ch);
        Assert.Equal("{}", Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Id })).GetRawText());

        Fixture.Append(otherEvents, Copilot.Assistant("first", 4));
        await source.RefreshAsync();
        Assert.Equal(Other, Received.Read<SessionPatch>((await NextAsync(ch)).Data).Id);
        var before = source.Revision;
        Fixture.Append(otherEvents, Copilot.Assistant("second", 5));
        await source.RefreshAsync();
        await HostWait.UntilAsync(() => feed!.Held == 1, "the quiet session's next change held", Patience);
        return (ch, time, feed!, before);
    }

    [Fact]
    public async Task Once_a_client_says_what_it_shows_other_sessions_changes_wait_for_the_quiet_interval_and_no_revision_passes_them()
    {
        var (ch, time, feed, before) = await FocusedWithAChangeHeldAsync();

        await ChangeAsync("shown at once", 6);
        var shown = Received.Read<SessionPatch>((await NextAsync(ch)).Data);
        Assert.Equal(Id, shown.Id);
        // The held change was made after `before`: a client resuming from a later revision would
        // never be sent it.
        Assert.True(shown.Revision <= before, $"revision {shown.Revision} passes the change held since {before}");
        Assert.False(ch.Events.TryRead(out _));

        time.Advance(service.QuietInterval);
        var released = Received.Read<SessionPatch>((await NextAsync(ch)).Data);
        Assert.Equal(Other, released.Id);
        Assert.Equal(source.Revision, released.Revision);
        Assert.Equal(0, feed.Held);
    }

    [Fact]
    public async Task A_new_question_on_a_session_not_open_goes_at_once_while_another_sessions_change_stays_held()
    {
        var thirdEvents = f.CopilotSession(Third, 102, name: "Asking session");
        f.Processes.Run(AgentKind.Copilot, 102);
        var (ch, _, feed, before) = await FocusedWithAChangeHeldAsync();

        Fixture.Append(thirdEvents, Copilot.User("go", 4), Copilot.TurnStart(5), Copilot.ToolStart("q1", "ask_user", new
        {
            message = "Which one?",
            requestedSchema = new { properties = new { pick = new { type = "string", @enum = new[] { "a", "b" } } }, required = new[] { "pick" } },
        }, 6));
        await source.RefreshAsync();

        var asked = await NextAsync(ch);
        Assert.Equal(Third, asked.Data.GetProperty("id").GetString());
        Assert.Contains("Which one?", asked.Data.GetRawText());
        Assert.True(Received.Read<SessionPatch>(asked.Data).Revision <= before);
        Assert.Equal(1, feed.Held);
    }

    // Queue exact states so a question disappearing can be tested independently of status,
    // and a field changing independently of the parser's other changes to the session.
    async Task AssertUrgentChangeAsync(Func<Session, Session> change, SessionStatus status = SessionStatus.Waiting,
        bool question = true, bool answerAtMachine = false)
    {
        var (ch, _, feed, floor) = await FocusedWithAChangeHeldAsync();
        var before = source.Snapshot().Single(s => s.Id == Id) with
        {
            Id = Third,
            Status = status,
            Question = question ? new Question
            {
                Id = "q1",
                Kind = QuestionKind.Form,
                Message = "Which one?",
                AskedAt = Fixture.T0,
                AnswerAtMachine = answerAtMachine,
                Fields = [new Field { Name = "pick", Type = FieldType.Choice, Options = [new("a"), new("b")] }],
            } : null,
        };
        Assert.True(feed.Subscription.Channel.Writer.TryWrite(new SessionEvent(Events.SessionUpsert, before, source.Revision + 1)));
        var sent = await NextAsync(ch);
        Assert.Equal(Third, sent.Data.GetProperty("id").GetString());
        var heldJson = SessionPatches.Apply(null, null, Received.Read<SessionPatch>(sent.Data));

        // Make this session quiet too: its urgent change must replace its held state without
        // releasing the other session or advancing the watermark past that session's floor.
        var held = before with { Title = "A held title" };
        Assert.True(feed.Subscription.Channel.Writer.TryWrite(new SessionEvent(Events.SessionUpsert, held, source.Revision + 2)));
        await HostWait.UntilAsync(() => feed.Held == 2, "both quiet sessions' changes held", Patience);
        var after = change(held);
        Assert.True(feed.Subscription.Channel.Writer.TryWrite(new SessionEvent(Events.SessionUpsert, after, source.Revision + 3)));

        // No clock advance: removing the matching Urgent branch leaves this event held.
        var live = await NextAsync(ch);
        Assert.Equal(Events.SessionPatch, live.Name);
        var patch = Received.Read<SessionPatch>(live.Data);
        Assert.Equal(Third, patch.Id);
        Assert.Equal(sent.Seq, patch.Base);
        Assert.True(patch.Revision <= floor);
        Assert.True(JsonNode.DeepEquals(SessionPatches.Json(after), SessionPatches.Apply(heldJson, sent.Seq, patch)));
        Assert.Equal(1, feed.Held);
        Assert.False(ch.Events.TryRead(out _));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("fields")]
    public async Task Replacing_a_question_while_still_waiting_goes_at_once_even_when_the_session_is_not_open(string changed) =>
        await AssertUrgentChangeAsync(session => session with
        {
            Question = changed == "id" ? session.Question! with { Id = "q2" } : session.Question! with
            {
                Fields = [new Field { Name = "pick", Type = FieldType.Choice, Options = [new("a"), new("c")] }],
            },
        });

    [Fact]
    public async Task Removing_a_question_while_still_waiting_goes_at_once_even_when_the_session_is_not_open() =>
        await AssertUrgentChangeAsync(session => session with { Question = null });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_only_answer_at_machine_while_still_waiting_goes_at_once_even_when_the_session_is_not_open(bool before) =>
        await AssertUrgentChangeAsync(session => session with
        {
            Question = session.Question! with { AnswerAtMachine = !before },
        }, answerAtMachine: before);

    [Theory]
    [InlineData(SessionStatus.Waiting)]
    [InlineData(SessionStatus.Ending)]
    public async Task Changing_only_status_to_waiting_or_ending_goes_at_once_even_when_the_session_is_not_open(SessionStatus status) =>
        await AssertUrgentChangeAsync(session => session with { Status = status }, status: SessionStatus.Working, question: false);

    // A resumed snapshot leaves out what did not change, which the client still holds, a pending
    // question included: with nothing sent to compare with, a session's first change goes at once.
    [Fact]
    public async Task After_a_resume_the_first_change_of_a_session_not_sent_again_goes_at_once_while_another_stays_held()
    {
        var otherEvents = f.CopilotSession(Other, 101, name: "Quiet session");
        f.Processes.Run(AgentKind.Copilot, 101);
        await source.RefreshAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        SessionService.Feed? feed = null;
        service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0", Instance: "run-1")) { Time = time };
        service.FeedCreated += created => feed = (SessionService.Feed)created;
        host.Hub.Handler = service.HandleAsync;
        var first = await OpenAsync();
        Result(await first.RequestAsync(Ops.SessionsSubscribe, new { patch = true }));
        var revision = (await SnapshotAsync(first))[^1].Data.GetProperty("revision").GetInt64();
        var ch = await OpenAsync();
        Assert.True(Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { patch = true, since = new { instance = "run-1", revision } })).GetProperty("resumed").GetBoolean());
        Assert.Equal([Events.SessionsSynced], (await SnapshotAsync(ch)).Select(e => e.Name));
        Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = false }));

        // The quiet session's first change goes for want of anything to compare with, its second
        // after a quiet spell, its third is held.
        for (var i = 0; i < 2; i++)
        {
            Fixture.Append(otherEvents, Copilot.Assistant("change " + i, 4 + i));
            await source.RefreshAsync();
            Assert.Equal(Other, Received.Read<SessionPatch>((await NextAsync(ch)).Data).Id);
        }
        Fixture.Append(otherEvents, Copilot.Assistant("held", 6));
        await source.RefreshAsync();
        await HostWait.UntilAsync(() => feed!.Held == 1, "the quiet session's third change held", Patience);

        await ChangeAsync("not sent since the resume", 7);
        Assert.Equal(Id, Received.Read<SessionPatch>((await NextAsync(ch)).Data).Id);
        Assert.Equal(1, feed!.Held);
    }

    [Fact]
    public async Task The_focus_rate_window_resets_after_ten_seconds_and_a_refusal_does_not_end_the_channel_or_feed()
    {
        var (ch, time, feed, _) = await FocusedWithAChangeHeldAsync();
        // The helper already sent the first focus request in this window.
        for (var i = 1; i < 20; i++) { Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Id })); }

        time.Advance(TimeSpan.FromMilliseconds(9_999));
        var refused = await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Other });
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Result(await ch.RequestAsync(Ops.Hello, new { }));
        Assert.Equal(1, feed.Held);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Other }));
        var released = Received.Read<SessionPatch>((await NextAsync(ch)).Data);
        Assert.Equal(Other, released.Id);
        Assert.Equal(0, feed.Held);

        Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Id }));
        await ChangeAsync("still delivering after a refusal", 6);
        var live = Received.Read<SessionPatch>((await NextAsync(ch)).Data);
        Assert.Equal(Id, live.Id);
        Assert.True(ch.IsOpen);
        Assert.Equal(1, service.Subscribers);
    }

    // #1190: idleMs is an age, so the host keeps its own clock minus it; without it, or with an
    // openQuestion but no open session, the host knows nothing it could hold a push back for.
    [Fact]
    public async Task A_focus_with_idleMs_gives_the_host_the_last_input_by_its_own_clock_and_one_without_it_leaves_it_unknown()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0")) { Time = time };
        host.Hub.Handler = service.HandleAsync;
        var ch = await OpenAsync();
        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { patch = true }));
        await SnapshotAsync(ch);

        Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Id, openQuestion = "q1", idleMs = 1500 }));
        var said = Assert.Single(service.Presences());
        Assert.Equal((true, Id, "q1", (DateTimeOffset?)(time.GetUtcNow() - TimeSpan.FromMilliseconds(1500))), (said.Visible, said.Open, said.OpenQuestion, said.LastInput));

        Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, openQuestion = "q1", idleMs = -5 }));
        said = Assert.Single(service.Presences());
        Assert.Equal((true, (string?)null, (string?)null, (DateTimeOffset?)null), (said.Visible, said.Open, said.OpenQuestion, said.LastInput));

        Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Id }));
        Assert.Null(Assert.Single(service.Presences()).LastInput);
    }

    [Fact]
    public async Task An_idleMs_of_a_day_is_kept_one_past_a_day_is_unknown_and_one_not_whole_fails_the_request_and_keeps_what_was_said()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0")) { Time = time };
        host.Hub.Handler = service.HandleAsync;
        var ch = await OpenAsync();
        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { patch = true }));
        await SnapshotAsync(ch);

        Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Id, idleMs = 86_400_000 }));
        Assert.Equal(time.GetUtcNow() - TimeSpan.FromDays(1), Assert.Single(service.Presences()).LastInput);

        Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Id, idleMs = 86_400_001 }));
        Assert.Null(Assert.Single(service.Presences()).LastInput);

        Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Id, idleMs = 1500 }));
        var refused = await ch.RequestAsync(Ops.SessionsFocus, new { visible = false, idleMs = 1.5 });
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("idleMs only as a whole number", refused.GetRawText());
        var kept = Assert.Single(service.Presences());
        Assert.Equal((true, (string?)Id, (DateTimeOffset?)(time.GetUtcNow() - TimeSpan.FromMilliseconds(1500))), (kept.Visible, kept.Open, kept.LastInput));
    }

    [Fact]
    public async Task What_a_device_said_it_shows_stops_counting_when_its_channel_closes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0")) { Time = time };
        host.Hub.Handler = service.HandleAsync;
        var ch = await OpenAsync();
        Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { patch = true }));
        await SnapshotAsync(ch);
        Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Id, openQuestion = "q1", idleMs = 0 }));
        Assert.Single(service.Presences());

        await client.DisposeAsync();

        await HostWait.UntilAsync(() => service.Presences().Count == 0, "the device's presence to end with its channel", Patience);
    }

    [Fact]
    public async Task A_session_the_client_opens_has_its_held_change_sent_at_once()
    {
        var (ch, _, feed, _) = await FocusedWithAChangeHeldAsync();

        Result(await ch.RequestAsync(Ops.SessionsFocus, new { visible = true, open = Other }));

        var released = Received.Read<SessionPatch>((await NextAsync(ch)).Data);
        Assert.Equal(Other, released.Id);
        Assert.Equal(source.Revision, released.Revision);
        Assert.Equal(0, feed.Held);
    }

    [Theory]
    [InlineData("run-0", 0L)]
    [InlineData("run-1", 1_000_000L)]
    public async Task A_resume_from_another_run_or_a_revision_not_reached_gets_the_whole_snapshot(string instance, long ahead)
    {
        Run("run-1");
        var ch = await OpenAsync();

        var result = Result(await ch.RequestAsync(Ops.SessionsSubscribe, new { patch = true, since = new { instance, revision = source.Revision + ahead } }));

        Assert.False(result.TryGetProperty("resumed", out _));
        var snapshot = await SnapshotAsync(ch);
        Assert.Equal([Events.SessionUpsert, Events.SessionsSynced], snapshot.Select(e => e.Name));
        Assert.False(snapshot[1].Data.TryGetProperty("live", out _));
    }
}
