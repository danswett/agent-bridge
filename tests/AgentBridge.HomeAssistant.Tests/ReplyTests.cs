using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Replying from a card with no question on it, against an in-process Home Assistant: the reply
// card's publish (with images from Home Assistant's image store and files inline) and the reply
// box with Send, as the daemon reads them (Fixtures/ha-replies.json, recorded from its own
// functions), and what reaches the host's reply delivery.
public sealed partial class ReplyTests : IDisposable
{
    const string Machine = "DSWETT-DEV-VM1";
    const string A = "6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20";
    static readonly string Node = HaContract.NodeId(A);
    static readonly string[] TransientKeys = ["error", "hint", "waiting_on", "sent", "unsent", "recorded", "answer", "at"];

    static readonly Lazy<JsonObject> Loaded = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-replies.json")))!);

    readonly string folder = Path.Combine(Path.GetTempPath(), "abha-" + Guid.NewGuid().ToString("N"));
    readonly FakeHomeAssistant ha = new();
    readonly FakeSessions sessions = new();
    readonly FakeTimeProvider time = new(FakeSessions.T0);
    readonly List<string> log = [];
    readonly List<HaReply> replied = [];
    readonly List<AnswerRequest> answered = [];
    Func<HaReply, Task<ReplyResult>> outcome = _ => Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
    // agent-ha-bridge, as the connector's G5 check finds it: running when set.
    string? daemon;

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    HaConnector Connector(params string[] agents) => new(new HaConnectorOptions
    {
        MachineName = Machine,
        Version = "0.1.0",
        StateFile = Path.Combine(folder, "ha-connector.json"),
        AgentUserIds = agents,
        FieldPinDelay = TimeSpan.Zero,
        SelectDelay = TimeSpan.Zero,
        ReplyCommitWait = TimeSpan.Zero,
        ReplyStopWait = TimeSpan.FromMilliseconds(200),
    }, ha, sessions, () => daemon, log: m => { lock (log) { log.Add(m); } }, time: time,
        answer: (r, _) =>
        {
            answered.Add(r);
            return Task.FromResult(new AnswerResult(AnswerOutcome.Delivered));
        },
        reply: (r, _) =>
        {
            lock (replied) { replied.Add(r); }
            return outcome(r);
        });

    async Task Pass(HaConnector c, double seconds = 1)
    {
        time.Advance(TimeSpan.FromSeconds(seconds));
        await c.TickAsync();
    }

    // What a person (or an agent) does on the card, the pass that reads it, and, once its
    // delivery is done, the pass that says what came of it.
    async Task Do(HaConnector c, HaTrigger hit)
    {
        c.Observe(hit);
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c);
    }

    void Idle(int turn = 1, string? message = "go", Question? question = null) =>
        sessions.Set(FakeSessions.Copilot(A) with
        {
            Question = question,
            Activity = new Activity { Response = "Done", TurnCount = turn, UserMessage = message },
        });

    async Task<HaConnector> Ready(params string[] agents)
    {
        Idle();
        var c = Connector(agents);
        await Pass(c);
        return c;
    }

    string Activity => ha.Retained[HaContract.Topics(Node).ActivityState];
    JsonElement Detail => JsonDocument.Parse(ha.Retained[HaContract.Topics(Node).ActivityAttributes]).RootElement;

    // The status lines said since mark, each with the attributes published with it.
    List<(string Summary, JsonElement Attributes)> NotesSince(int mark)
    {
        var t = HaContract.Topics(Node);
        var notes = new List<(string, JsonElement)>();
        var log = ha.Log.Skip(mark).ToList();
        for (var i = 0; i < log.Count; i++)
        {
            if (log[i].Topic != t.ActivityState) { continue; }
            var attributes = log.Skip(i + 1).First(p => p.Topic == t.ActivityAttributes);
            notes.Add((log[i].Payload, JsonDocument.Parse(attributes.Payload).RootElement));
        }
        return notes;
    }

    static JsonObject Case(string name) =>
        Loaded.Value["cases"]!.AsArray().Select(c => c!.AsObject()).Single(c => c["name"]!.GetValue<string>() == name);

    // What the card was told, as the daemon told it in the same case.
    void AssertNotes(string name, int mark)
    {
        var expected = Case(name)["notes"]!.AsArray().Select(n => n!.AsObject()).ToList();
        var actual = NotesSince(mark);
        Assert.Equal(expected.Select(n => n["summary"]!.GetValue<string>()), actual.Select(n => n.Summary));
        for (var i = 0; i < expected.Count; i++)
        {
            var extra = expected[i]["extra"]!.AsObject();
            foreach (var (key, value) in extra)
            {
                Assert.True(actual[i].Attributes.TryGetProperty(key, out var got), $"{name} note {i}: {key}");
                var want = value!.GetValue<string>();
                if (want == "<time>") { Assert.Matches(Clock(), got.GetString()!); }
                else { Assert.Equal(want, got.GetString()); }
            }
            foreach (var key in TransientKeys.Where(k => !extra.ContainsKey(k)))
            {
                Assert.False(actual[i].Attributes.TryGetProperty(key, out _), $"{name} note {i}: {key} is not the daemon's");
            }
        }
    }

    // The services the reply path called, as the daemon called them in the same case.
    void AssertCalls(string name, int mark)
    {
        var expected = Case(name)["calls"]!.AsArray().Select(n => n!.AsObject())
            .Select(n => (n["service"]!.GetValue<string>(), n["entity_id"]!.GetValue<string>(), n["value"]?.GetValue<string>() ?? "")).ToList();
        Assert.Equal(expected, ha.Services.Skip(mark).Select(s => (s.Service, s.EntityId, s.Value)).ToList());
    }

    [GeneratedRegex("^[0-9]{2}:[0-9]{2}:[0-9]{2}$")]
    private static partial Regex Clock();

    [Fact]
    public void The_fixture_was_recorded_against_the_same_session_and_constants()
    {
        Assert.Equal(A, Loaded.Value["sessionId"]!.GetValue<string>());
        var constants = Loaded.Value["constants"]!.AsObject();
        var defaults = new HaConnectorOptions { MachineName = Machine, Version = "0" };
        Assert.Equal(constants["replyCommitAttempts"]!.GetValue<int>(), defaults.ReplyCommitAttempts);
        Assert.Equal(constants["replyCommitWaitMs"]!.GetValue<int>(), (int)defaults.ReplyCommitWait.TotalMilliseconds);
        Assert.Equal(constants["submitArmSeconds"]!.GetValue<int>(), (int)defaults.SubmitArm.TotalSeconds);
        Assert.Equal(constants["driverArmSeconds"]!.GetValue<int>(), (int)defaults.DriverArm.TotalSeconds);
        Assert.Equal(HaDecision.ReplyBlank, constants["replyBlank"]!.GetValue<string>());
        Assert.Equal(constants["inlineFileMaxBytes"]!.GetValue<int>(), defaults.InlineFileMaxBytes);
    }

    [Theory]
    [InlineData("cardText")]
    [InlineData("cardAgent")]
    [InlineData("cardFailed")]
    public async Task A_reply_card_publish_is_sent_and_said_as_the_daemon_says_it(string name)
    {
        var typed = Case(name)["typed"]!.AsArray().Single()!.GetValue<string>();
        if (name == "cardFailed") { outcome = _ => Task.FromResult(new ReplyResult(AnswerOutcome.Refused, "the console refused the keys")); }
        await using var c = await Ready();
        var mark = ha.Log.Count;
        var services = ha.Services.Count;

        await Do(c, ha.SendFromCard(Node, typed, driver: name == "cardAgent" ? "agent" : null));

        var r = Assert.Single(replied);
        Assert.Equal(A, r.SessionId);
        Assert.Equal(typed, r.Text);
        Assert.Empty(r.Files);
        AssertNotes(name, mark);
        // The reply card does not use the box, so nothing clears it.
        AssertCalls(name, services);
    }

    [Fact]
    public async Task A_reply_card_publish_with_an_image_and_a_file_sends_both_and_deletes_the_image_once_sent()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var notes = Encoding.UTF8.GetBytes("line one\nline two\n");
        var image = ha.Upload(png, "0123abcd");
        await using var c = await Ready();
        var mark = ha.Log.Count;

        await Do(c, ha.SendFromCard(Node, "What is this?",
            images: [new JsonObject { ["id"] = image, ["name"] = "shot.png", ["content_type"] = "image/png" }],
            files: [new JsonObject { ["name"] = "notes.txt", ["content_type"] = "text/plain", ["b64"] = Convert.ToBase64String(notes) }]));

        var r = Assert.Single(replied);
        Assert.Equal("What is this?", r.Text);
        Assert.Equal(["shot.png", "notes.txt"], r.Files.Select(f => f.Name));
        Assert.Equal(["image/png", "text/plain"], r.Files.Select(f => f.Type));
        Assert.Equal(png, r.Files[0].Bytes);
        Assert.Equal(notes, r.Files[1].Bytes);
        AssertNotes("cardAttachments", mark);
        Assert.Equal(Case("cardAttachments")["deletedImages"]!.AsArray().Select(n => n!.GetValue<string>()), ha.DeletedImages);
    }

    [Fact]
    public async Task An_image_that_cannot_be_fetched_sends_nothing_and_keeps_the_images()
    {
        var kept = ha.Upload([1, 2, 3], "aaaa1111");
        await using var c = await Ready();
        var mark = ha.Log.Count;

        await Do(c, ha.SendFromCard(Node, "Look",
            images: [new JsonObject { ["id"] = kept, ["name"] = "a.png" }, new JsonObject { ["id"] = "gone2222", ["name"] = "b.png" }]));

        Assert.Empty(replied);
        AssertNotes("cardImageFails", mark);
        Assert.Empty(ha.DeletedImages);
        Assert.True(ha.Images.ContainsKey(kept));
    }

    [Fact]
    public async Task A_refused_reply_keeps_its_images_in_home_assistant()
    {
        outcome = _ => Task.FromResult(new ReplyResult(AnswerOutcome.Refused, "the console refused the keys"));
        var image = ha.Upload([1, 2, 3], "bbbb2222");
        await using var c = await Ready();

        await Do(c, ha.SendFromCard(Node, "Look", images: [new JsonObject { ["id"] = image, ["name"] = "a.png" }]));

        Assert.Single(replied);
        Assert.Equal("Reply NOT sent", Activity);
        Assert.Empty(ha.DeletedImages);
    }

    [Fact]
    public async Task Attachment_metadata_that_is_incomplete_sends_nothing()
    {
        await using var c = await Ready();
        var mark = ha.Log.Count;

        await Do(c, ha.SendFromCard(Node, "Look", images: [new JsonObject { ["name"] = "no-id.png" }]));

        Assert.Empty(replied);
        AssertNotes("cardIncomplete", mark);
    }

    [Fact]
    public async Task An_inline_file_over_the_daemons_limit_is_refused_before_anything_is_sent()
    {
        await using var c = await Ready();

        await Do(c, ha.SendFromCard(Node, "Big",
            files: [new JsonObject { ["name"] = "big.bin", ["b64"] = Convert.ToBase64String(new byte[262145]) }]));

        Assert.Empty(replied);
        Assert.Equal("Reply NOT sent", Activity);
        Assert.Equal("A file could not be staged privately; no reply was sent and source images were kept.", Detail.GetProperty("error").GetString());
        Assert.False(Detail.TryGetProperty("unsent", out _));
    }

    [Fact]
    public async Task Words_for_a_question_blank_publishes_and_a_publish_read_again_are_never_replies()
    {
        await using var c = await Ready();
        var mark = ha.Log.Count;

        await Do(c, ha.SendFromCard(Node, "for the question", decisionId: "q9"));
        AssertNotes("cardTagged", mark);
        await Do(c, ha.SendFromCard(Node, "   "));
        AssertNotes("cardBlank", mark);
        Assert.Empty(replied);

        var sent = ha.SendFromCard(Node, "Ship it");
        await Do(c, sent);
        Assert.Single(replied);
        mark = ha.Log.Count;
        await Do(c, sent);
        await Pass(c, 20);
        Assert.Single(replied);
        AssertNotes("cardRepeat", mark);
    }

    [Theory]
    [InlineData("boxText")]
    [InlineData("boxFailed")]
    public async Task The_reply_box_and_send_deliver_the_words_and_clear_the_box(string name)
    {
        var typed = Case(name)["typed"]!.AsArray().Single()!.GetValue<string>();
        if (name == "boxFailed") { outcome = _ => Task.FromResult(new ReplyResult(AnswerOutcome.Refused, "the console refused the keys")); }
        await using var c = await Ready();
        await Do(c, ha.Type(SessionEntities.Reply(Node), typed));
        Assert.Empty(replied);
        var mark = ha.Log.Count;
        var services = ha.Services.Count;

        await Do(c, ha.Press(SessionEntities.Submit(Node)));

        Assert.Equal(typed, Assert.Single(replied).Text);
        AssertNotes(name, mark);
        // Cleared either way, so a failed reply is not sent again by itself.
        AssertCalls(name, services);
    }

    [Fact]
    public async Task Words_typed_while_a_reply_is_delivering_are_kept_in_the_box()
    {
        await using var c = await Ready();
        outcome = _ =>
        {
            ha.Type(SessionEntities.Reply(Node), "and then this");
            return Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
        };
        await Do(c, ha.Type(SessionEntities.Reply(Node), "Ship it"));
        var mark = ha.Log.Count;
        var services = ha.Services.Count;

        await Do(c, ha.Press(SessionEntities.Submit(Node)));

        AssertNotes("boxFollowUpKept", mark);
        AssertCalls("boxFollowUpKept", services);
        Assert.Equal("and then this", (await ha.GetStateAsync(SessionEntities.Reply(Node)))!.State);
    }

    [Fact]
    public async Task A_box_that_cannot_be_read_after_a_reply_is_left_as_it_is_and_its_press_is_not_sent_again()
    {
        await using var c = await Ready();
        outcome = _ =>
        {
            ha.Type(SessionEntities.Reply(Node), "and then this");
            ha.GetFailsFor = e => e == SessionEntities.Reply(Node);
            return Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
        };
        await Do(c, ha.Type(SessionEntities.Reply(Node), "Ship it"));
        var services = ha.Services.Count;

        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal("Reply sent", Activity);
        Assert.Empty(ha.Services.Skip(services));

        ha.GetFailsFor = null;
        await Pass(c, 20);
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Single(replied);
        Assert.Equal("and then this", (await ha.GetStateAsync(SessionEntities.Reply(Node)))!.State);
    }

    [Fact]
    public async Task A_persons_send_at_an_empty_box_waits_for_the_words_and_sends_them_when_they_arrive()
    {
        await using var c = await Ready();
        var mark = ha.Log.Count;

        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Empty(replied);
        AssertNotes("boxBlank", mark);

        // Said once, not on every pass.
        mark = ha.Log.Count;
        await Pass(c, 20);
        Assert.Empty(NotesSince(mark));

        await Do(c, ha.Type(SessionEntities.Reply(Node), "Here it is"));
        Assert.Equal("Here it is", Assert.Single(replied).Text);
        Assert.Equal("Reply sent", Activity);

        // That press is spent: the next words need a press of their own.
        await Do(c, ha.Type(SessionEntities.Reply(Node), "And more"));
        await Pass(c, 20);
        Assert.Single(replied);
    }

    [Fact]
    public async Task A_send_at_an_empty_box_gives_up_after_ten_minutes()
    {
        await using var c = await Ready();
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        var mark = ha.Log.Count;
        await Pass(c, 590);
        Assert.Empty(NotesSince(mark));

        await Pass(c, 20);
        AssertNotes("boxBlankGivenUp", mark);

        // Given up: words typed now wait for a press of their own.
        await Do(c, ha.Type(SessionEntities.Reply(Node), "late"));
        await Pass(c, 20);
        Assert.Empty(replied);
    }

    [Fact]
    public async Task An_agents_send_at_an_empty_box_is_nothing_to_send_and_arms_nothing()
    {
        await using var c = await Ready("agent-user");
        var mark = ha.Log.Count;

        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));

        Assert.Empty(replied);
        AssertNotes("boxBlankAgent", mark);
        Idle(turn: 2, message: "typed in the terminal");
        await Pass(c);
        Assert.Equal("human", Detail.GetProperty("driver").GetString());
    }

    [Fact]
    public async Task An_agents_send_after_its_publish_went_keeps_the_agent_driving_the_turn()
    {
        await using var c = await Ready("agent-user");
        await Do(c, ha.SendFromCard(Node, "Carry on", driver: "agent"));
        var mark = ha.Log.Count;

        // The card's publish went on its own; the agent's Send that follows finds the box empty.
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        Assert.Empty(NotesSince(mark));

        Idle(turn: 2, message: "Carry on");
        await Pass(c);
        Assert.Equal("agent", Detail.GetProperty("driver").GetString());
    }

    [Fact]
    public async Task A_reply_that_reached_nothing_hands_the_next_turn_back_to_the_person()
    {
        outcome = _ => Task.FromResult(new ReplyResult(AnswerOutcome.Refused, "the console refused the keys"));
        await using var c = await Ready("agent-user");
        await Do(c, ha.SendFromCard(Node, "Carry on", driver: "agent"));

        Idle(turn: 2, message: "typed in the terminal");
        await Pass(c);
        Assert.Equal("human", Detail.GetProperty("driver").GetString());
    }

    [Fact]
    public async Task A_reply_already_on_the_card_when_the_host_starts_is_never_sent()
    {
        var c1 = await Ready();
        await Do(c1, ha.SendFromCard(Node, "Ship it"));
        await Do(c1, ha.Type(SessionEntities.Reply(Node), "box words"));
        await Do(c1, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal(2, replied.Count);
        await c1.DisposeAsync();

        // Another publish and press while nothing was reading the card: what agent-ha-bridge, or
        // this host before a restart, may already have sent.
        ha.SendFromCard(Node, "while away");
        ha.Type(SessionEntities.Reply(Node), "typed while away");
        ha.Press(SessionEntities.Submit(Node));

        await using var c2 = Connector();
        await Pass(c2);
        await c2.DeliveredAsync();
        await Pass(c2, 20);
        Assert.Equal(2, replied.Count);

        // What comes after is.
        await Do(c2, ha.SendFromCard(Node, "now"));
        Assert.Equal("now", replied[^1].Text);
    }

    [Fact]
    public async Task Words_given_while_a_question_is_open_answer_it_and_are_not_sent_again_as_a_reply()
    {
        await using var c = await Ready();
        Idle(question: new Question { Id = "q1", Kind = QuestionKind.Form, Message = "What next?", AskedAt = FakeSessions.T0 });
        await Pass(c);

        await Do(c, ha.SendFromCard(Node, "Cut it"));
        Assert.Equal("Cut it", Assert.Single(answered).Text);

        Idle(turn: 2);
        await Pass(c);
        await Pass(c, 20);
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Empty(replied);

        await Do(c, ha.SendFromCard(Node, "Thanks"));
        Assert.Equal("Thanks", Assert.Single(replied).Text);
    }

    [Fact]
    public async Task A_reply_while_a_question_waits_at_the_machine_is_refused_and_not_sent_once_it_goes()
    {
        await using var c = await Ready();
        Idle(question: new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Answer here", AskedAt = FakeSessions.T0, AnswerAtMachine = true });
        await Pass(c);

        await Do(c, ha.SendFromCard(Node, "too soon"));
        Assert.Equal("Reply NOT sent", Activity);
        Assert.Equal("the session is waiting on a question", Detail.GetProperty("error").GetString());

        Idle(turn: 2);
        await Pass(c);
        await Pass(c, 20);
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Empty(replied);
        Assert.Empty(answered);
    }

    [Fact]
    public async Task A_slow_delivery_holds_neither_the_card_nor_the_next_reply()
    {
        var release = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ =>
        {
            reached.TrySetResult();
            return release.Task;
        };
        await using var c = await Ready();
        c.Observe(ha.SendFromCard(Node, "first"));
        await Pass(c);
        Assert.Equal("Sending...", Activity);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The session's own changes still reach the card while the reply is going in.
        sessions.Set(sessions.Get(A)! with { Title = "Renamed while sending" });
        await Pass(c);
        Assert.Contains(ha.Log.Skip(1), p => p.Payload.Contains("Renamed while sending", StringComparison.Ordinal));

        // A second publish waits for the first to finish rather than racing it.
        c.Observe(ha.SendFromCard(Node, "second"));
        await Pass(c);
        Assert.Single(replied);

        release.SetResult(new ReplyResult(AnswerOutcome.Delivered));
        await c.DeliveredAsync();
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Equal(["first", "second"], replied.Select(r => r.Text));
    }

    [Fact]
    public async Task Stopping_does_not_wait_forever_on_a_delivery_that_never_finishes()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<ReplyResult>();
        outcome = _ =>
        {
            reached.TrySetResult();
            return never.Task;
        };
        var c = await Ready();
        c.Observe(ha.SendFromCard(Node, "stuck"));
        await Pass(c);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await c.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(log, l => l.Contains("stopped with a reply still being delivered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pause_while_a_reply_is_delivering_neither_overlaps_it_nor_loses_what_is_sent_after()
    {
        var release = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = r =>
        {
            if (r.Text != "first") { return Task.FromResult(new ReplyResult(AnswerOutcome.Delivered)); }
            reached.TrySetResult();
            return release.Task;
        };
        await using var c = await Ready();
        c.Observe(ha.SendFromCard(Node, "first"));
        await Pass(c);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // agent-ha-bridge runs for a moment: the connector pauses, and starts over once it stops.
        daemon = "agent-ha-bridge is running";
        await Pass(c);
        daemon = null;
        await Pass(c);
        Assert.True(c.Started);

        c.Observe(ha.SendFromCard(Node, "second"));
        await Pass(c);
        await Pass(c, 20);
        Assert.Single(replied);

        release.SetResult(new ReplyResult(AnswerOutcome.Delivered));
        await c.DeliveredAsync();
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Equal(["first", "second"], replied.Select(r => r.Text));
    }

    [Fact]
    public async Task A_reply_that_fails_after_an_agents_answer_leaves_the_answers_arm_alone()
    {
        var release = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ =>
        {
            reached.TrySetResult();
            return release.Task;
        };
        await using var c = await Ready("agent-user");
        c.Observe(ha.SendFromCard(Node, "a reply"));
        await Pass(c);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A question opens meanwhile, and an agent answers it from the card.
        Idle(question: new Question { Id = "q1", Kind = QuestionKind.Form, Message = "What next?", AskedAt = FakeSessions.T0 });
        await Pass(c);
        // (Not Do: the reply is still being delivered, and Do waits for every delivery.)
        c.Observe(ha.SendFromCard(Node, "Cut it", decisionId: "q1", driver: "agent"));
        await Pass(c);
        Assert.Single(answered);

        release.SetResult(new ReplyResult(AnswerOutcome.Refused, "the console refused the keys"));
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Equal("Reply NOT sent", Activity);

        Idle(turn: 2, message: "Cut it");
        await Pass(c);
        Assert.Equal("agent", Detail.GetProperty("driver").GetString());
    }

    [Fact]
    public async Task More_attachments_than_a_reply_carries_are_refused_before_any_is_fetched()
    {
        await using var c = await Ready();
        var images = new JsonArray([.. Enumerable.Range(0, AttachmentLimits.PerReply + 1).Select(i => (JsonNode)new JsonObject { ["id"] = $"img{i}", ["name"] = $"{i}.png" })]);

        await Do(c, ha.SendFromCard(Node, "Too many", images: images));

        Assert.Empty(replied);
        Assert.Equal(0, ha.ImageFetches);
        Assert.Equal("Reply NOT sent", Activity);
        Assert.Equal($"A reply carries at most {AttachmentLimits.PerReply} attachments; no reply was sent.", Detail.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_image_fetch_that_times_out_sends_nothing_and_keeps_the_images()
    {
        var image = ha.Upload([1, 2, 3], "cccc3333");
        ha.ImageFails = _ => true;
        ha.ImageError = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.");
        await using var c = await Ready();

        await Do(c, ha.SendFromCard(Node, "Look", images: [new JsonObject { ["id"] = image, ["name"] = "a.png" }]));

        Assert.Empty(replied);
        Assert.Equal("Reply NOT sent", Activity);
        Assert.Equal("An image could not be staged privately; no reply was sent and source images were kept.", Detail.GetProperty("error").GetString());
        Assert.Empty(ha.DeletedImages);
    }

    [Fact]
    public async Task A_press_made_while_send_read_unavailable_is_not_taken_for_new_once_it_is_back()
    {
        Idle();
        await using var c = Connector();
        var submit = SessionEntities.Submit(Node);
        // Send reads unavailable when the connector first looks, with a press already made on it.
        ha.States[submit] = new HaState(submit, "unavailable", default);
        await Pass(c);
        ha.Type(SessionEntities.Reply(Node), "already sent by agent-ha-bridge");
        ha.Press(submit);
        await Pass(c);
        await Pass(c, 20);

        ha.States.Remove(submit);
        await Pass(c);
        await Pass(c, 20);
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Empty(replied);

        await Do(c, ha.Press(submit));
        Assert.Equal("already sent by agent-ha-bridge", Assert.Single(replied).Text);
    }

    [Fact]
    public async Task Publishes_sent_while_a_reply_is_delivering_are_each_sent_in_the_order_they_came()
    {
        var release = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = r =>
        {
            if (r.Text != "first") { return Task.FromResult(new ReplyResult(AnswerOutcome.Delivered)); }
            reached.TrySetResult();
            return release.Task;
        };
        await using var c = await Ready();
        c.Observe(ha.SendFromCard(Node, "first"));
        await Pass(c);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The sensor ends up showing only the third; the watch reported all of them.
        c.Observe(ha.SendFromCard(Node, "second"));
        c.Observe(ha.SendFromCard(Node, "third"));
        await Pass(c);
        c.Observe(ha.SendFromCard(Node, "fourth"));
        await Pass(c);
        Assert.Single(replied);

        release.SetResult(new ReplyResult(AnswerOutcome.Delivered));
        for (var i = 0; i < 4; i++)
        {
            await c.DeliveredAsync();
            await Pass(c);
        }
        Assert.Equal(["first", "second", "third", "fourth"], replied.Select(r => r.Text));
        await Pass(c, 20);
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Equal(4, replied.Count);
    }

    [Fact]
    public async Task Replies_still_waiting_when_a_question_comes_are_said_to_be_not_sent()
    {
        var release = new TaskCompletionSource<ReplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = r =>
        {
            reached.TrySetResult();
            return release.Task;
        };
        await using var c = await Ready();
        c.Observe(ha.SendFromCard(Node, "first"));
        await Pass(c);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        c.Observe(ha.SendFromCard(Node, "second"));
        await Pass(c);

        Idle(question: new Question { Id = "q1", Kind = QuestionKind.Form, Message = "What next?", AskedAt = FakeSessions.T0 });
        await Pass(c);
        Assert.Equal("Reply NOT sent", Activity);
        Assert.Equal("the session asked a question before it could be sent", Detail.GetProperty("error").GetString());

        release.SetResult(new ReplyResult(AnswerOutcome.Delivered));
        await c.DeliveredAsync();
        await Pass(c);
        Idle(turn: 2);
        await Pass(c);
        await Pass(c, 20);
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Equal(["first"], replied.Select(r => r.Text));
        Assert.Empty(answered);
    }

    [Fact]
    public async Task Attachment_metadata_that_is_not_a_list_sends_nothing()
    {
        await using var c = await Ready();

        // An object where the card puts a list of images, read by the sweep.
        var payload = new JsonObject { ["at"] = $"{ha.Now:O}", ["text"] = "Look", ["images"] = new JsonObject { ["name"] = "shot.png" } };
        await ha.PublishAsync(HaContract.Topics(Node).ReplyPayload, HaContract.Serialize(payload), true);
        await Pass(c, 20);
        await c.DeliveredAsync();
        await Pass(c);

        Assert.Empty(replied);
        Assert.Equal("Reply NOT sent", Activity);
        Assert.Equal("The attachment metadata is incomplete; no reply was sent.", Detail.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Words_for_a_question_do_not_hold_up_a_send_pressed_in_the_same_pass()
    {
        await using var c = await Ready();
        await Do(c, ha.Type(SessionEntities.Reply(Node), "Ship it"));

        // The publish read from the sensor (its own report not yet in), the press reported.
        ha.SendFromCard(Node, "for a question", decisionId: "q9");
        c.Observe(ha.Press(SessionEntities.Submit(Node)));
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c);

        Assert.Equal("Ship it", Assert.Single(replied).Text);
    }

    [Fact]
    public async Task An_image_delete_that_times_out_on_its_own_holds_up_nothing()
    {
        var first = ha.Upload([1, 2, 3], "dddd4444");
        ha.DeleteFails = id => id == first ? new TaskCanceledException("image/delete: no answer in 15s") : null;
        await using var c = await Ready();

        await Do(c, ha.SendFromCard(Node, "one", images: [new JsonObject { ["id"] = first, ["name"] = "a.png" }]));
        Assert.Equal("Reply sent", Activity);
        await Do(c, ha.SendFromCard(Node, "two"));

        Assert.Equal(["one", "two"], replied.Select(r => r.Text));
        Assert.Equal("Reply sent", Activity);
        Assert.Equal("two", Detail.GetProperty("sent").GetString());
        Assert.Contains(log, l => l.Contains("could not delete image dddd4444", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publishes_reported_for_a_question_that_closes_before_they_are_read_are_not_replies()
    {
        await using var c = await Ready();
        Idle(question: new Question { Id = "q1", Kind = QuestionKind.Form, Message = "What next?", AskedAt = FakeSessions.T0, AnswerAtMachine = true });
        await Pass(c);

        // Two publishes while the question is up, and the question answered at the machine before
        // the next pass reads either.
        c.Observe(ha.SendFromCard(Node, "too soon"));
        c.Observe(ha.SendFromCard(Node, "also too soon"));
        Idle(turn: 2);
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c, 20);
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Empty(replied);

        await Do(c, ha.SendFromCard(Node, "now"));
        Assert.Equal("now", Assert.Single(replied).Text);
    }

    [Fact]
    public async Task A_persons_send_that_waited_for_its_words_is_the_persons_even_after_an_agents_publish()
    {
        await using var c = await Ready("agent-user");
        await Do(c, ha.Press(SessionEntities.Submit(Node), "person"));
        Assert.Equal("Waiting for your text", Activity);

        // An agent's publish goes in meanwhile, then the person's words arrive.
        await Do(c, ha.SendFromCard(Node, "Carry on", driver: "agent"));
        await Do(c, ha.Type(SessionEntities.Reply(Node), "Stop and check the logs"));
        Assert.Equal(["Carry on", "Stop and check the logs"], replied.Select(r => r.Text));

        Idle(turn: 2, message: "Stop and check the logs");
        await Pass(c);
        Assert.Equal("human", Detail.GetProperty("driver").GetString());
    }

    [Fact]
    public async Task Each_press_and_publish_is_its_own_request_and_the_same_one_is_the_same_request()
    {
        await using var c = await Ready();
        await Do(c, ha.SendFromCard(Node, "one"));
        await Do(c, ha.SendFromCard(Node, "one"));
        Assert.Equal(2, replied.Count);
        Assert.NotEqual(replied[0].Request, replied[1].Request);
        Assert.True(Guid.TryParse(replied[0].Request, out _));
    }
}
