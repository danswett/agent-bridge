using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The cards' view models: the question form, the reply box, End session and the card itself,
// against a host on a real pipe.
public sealed class CardTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    readonly string folds = Path.Combine(Path.GetTempPath(), "abf-" + Guid.NewGuid().ToString("N") + ".json");
    HostConnection connection = null!;
    Deliveries deliveries = null!;
    readonly FakeTimeProvider clock = new(Make.T0.AddMinutes(5));

    public async Task InitializeAsync()
    {
        host.Start();
        connection = host.Connect();
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
        deliveries = new Deliveries(connection, new LockedDispatcher(), clock) { RetryDelay = TimeSpan.FromMilliseconds(50) };
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        await host.DisposeAsync();
        File.Delete(folds);
    }

    CardContext Context(FoldStore? store = null) => new()
    {
        Deliveries = deliveries,
        Folds = store ?? new FoldStore(folds),
        Capabilities = () => connection.Capabilities,
        MachineName = () => "Test PC",
        Host = connection,
        Time = clock,
    };

    QuestionViewModel Question(Question q, IReadOnlyList<string>? caps = null) => new(q, "s1", AgentKind.Copilot, caps ?? connection.Capabilities, deliveries, "Test PC");

    [Fact]
    public async Task A_single_choice_question_answers_on_a_click_with_the_options_value()
    {
        host.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));
        var q = Question(Make.Choice());

        Assert.True(q.Tap);
        Assert.Equal(["PostgreSQL (Recommended)", "sqlite"], q.Fields[0].Options.Select(o => o.Label));
        q.Choose(q.Fields[0].Options.Single(o => o.Value == "sqlite"));

        await Wait.UntilAsync(() => q.StatusText == "Delivered. The session has your answer.", "delivered");
        var sent = Assert.Single(host.Sent(Ops.SessionAnswer));
        Assert.Equal("sqlite", sent.GetProperty("fields").GetProperty("answer").GetString());
        Assert.Equal("q1", sent.GetProperty("question").GetString());
        Assert.False(q.CanAnswer);
        Assert.Equal("sqlite", q.SentValue);
    }

    [Fact]
    public async Task Other_text_is_sent_as_other_and_a_refusal_lets_the_person_answer_again()
    {
        host.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Refused, "the prompt changed"));
        var q = Question(Make.Choice(other: true));

        q.ToggleOther();
        q.Fields[0].OtherText = "DuckDB – 数据库 🚀";
        q.Send();

        await Wait.UntilAsync(() => q.StatusText == "The machine refused this answer: the prompt changed.", "refused");
        var sent = Assert.Single(host.Sent(Ops.SessionAnswer));
        Assert.Equal("DuckDB – 数据库 🚀", sent.GetProperty("fields").GetProperty("answer").GetProperty("other").GetString());
        Assert.True(q.CanAnswer);
        Assert.Equal("DuckDB – 数据库 🚀", q.UnsentText());
    }

    [Fact]
    public void A_form_is_checked_before_it_goes_and_the_field_at_fault_is_marked()
    {
        var q = Question(Make.Form(
            new Field { Name = "name", Title = "Name", Type = FieldType.Text, Required = true },
            new Field { Name = "count", Title = "Count", Type = FieldType.Number }));

        q.Send();
        Assert.Equal("\"Name\" needs an answer.", q.Error);
        Assert.True(q.Fields[0].Invalid);

        q.Fields[0].Text = "x";
        q.Fields[1].Text = "twelve";
        q.Send();
        Assert.Equal("\"Count\": expected a number.", q.Error);
        Assert.True(q.Fields[1].Invalid);
        Assert.Empty(host.Sent(Ops.SessionAnswer));
    }

    [Fact]
    public void A_form_builds_each_type_of_field_as_the_contract_says()
    {
        var q = Question(Make.Form(
            new Field { Name = "pick", Type = FieldType.Multi, Options = [new Option("a"), new Option("b"), new Option("c")], AllowOther = true },
            new Field { Name = "ok", Type = FieldType.Boolean },
            new Field { Name = "n", Type = FieldType.Number },
            new Field { Name = "untouched", Type = FieldType.Multi, Options = [new Option("x")] }));
        q.Fields[0].Options[2].IsSelected = true;
        q.Fields[0].Options[0].IsSelected = true;
        q.Fields[0].OtherText = "d";
        q.Fields[1].ToggleBool(false);
        q.Fields[2].Text = "2.5";

        var (answer, problem, _) = q.Build();

        Assert.Null(problem);
        Assert.Equal("""["a","c",{"other":"d"}]""", answer.Fields!["pick"].GetRawText());
        Assert.False(answer.Fields["ok"].GetBoolean());
        Assert.Equal(2.5, answer.Fields["n"].GetDouble());
        Assert.False(answer.Fields.ContainsKey("untouched"));
        Assert.Null(Answers.Check(Make.Form(
            new Field { Name = "pick", Type = FieldType.Multi, Options = [new Option("a"), new Option("b"), new Option("c")], AllowOther = true },
            new Field { Name = "ok", Type = FieldType.Boolean },
            new Field { Name = "n", Type = FieldType.Number },
            new Field { Name = "untouched", Type = FieldType.Multi, Options = [new Option("x")] }), answer));
    }

    [Fact]
    public void An_approval_offers_approve_once_and_deny_coloured_for_what_they_do()
    {
        var q = Question(Make.Approval(), [.. connection.Capabilities, Capabilities.SessionsApprove]);

        Assert.Equal("Permission requested", q.Heading);
        Assert.Equal([("approve", "allow"), ("deny", "deny")], q.Fields[0].Options.Select(o => (o.Value, o.Tone)));
        Assert.Equal("Send", q.SendLabel);
    }

    [Theory]
    [InlineData(true, false, "approvalOff")]
    [InlineData(true, true, "atMachine")]
    public void A_permission_prompt_held_at_the_machine_says_why(bool answers, bool approves, string why)
    {
        List<string> caps = [Capabilities.SessionsRead];
        if (answers) { caps.Add(Capabilities.SessionsAnswer); }
        if (approves) { caps.Add(Capabilities.SessionsApprove); }
        var q = Question(Make.Approval() with { AnswerAtMachine = true }, caps);

        Assert.False(q.Answerable);
        Assert.Equal(why, q.Why);
        Assert.False(q.ShowReadOnlyFields);
    }

    [Fact]
    public void A_question_cut_to_fit_or_a_host_that_takes_no_answers_is_shown_read_only()
    {
        Assert.Equal("truncated", Question(Make.Choice() with { Truncated = true }).Why);
        var machine = Question(Make.Choice(), [Capabilities.SessionsRead]);
        Assert.Equal("machine", machine.Why);
        Assert.True(machine.ShowReadOnlyFields);
    }

    [Fact]
    public async Task A_reply_goes_with_its_files_sent_first_in_chunks_and_leaves_what_was_typed_meanwhile()
    {
        host.On(Ops.AttachmentStart, _ => new AttachmentState(0, false));
        long received = 0;
        host.On(Ops.AttachmentChunk, d => { received += Convert.FromBase64String(d.GetProperty("data").GetString()!).Length; return new AttachmentState(received, received == 30_000); });
        var reply = new TaskCompletionSource<ReplyResult>();
        host.On(Ops.SessionReply, _ => reply.Task.Result);
        var r = new ReplyViewModel(Make.Session(), attach: true, deliveries, connection, machineName: "Test PC");

        await r.AddAsync("notes.txt", "text/plain", new byte[30_000]);
        r.Text = "Look at this";
        r.Send();
        await Wait.UntilAsync(() => host.Sent(Ops.SessionReply).Count == 1, "the reply");
        r.Text = "Look at this and then this";
        reply.SetResult(new ReplyResult(AnswerOutcome.Delivered));

        await Wait.UntilAsync(() => r.StatusText == "Delivered. The session has your reply.", "delivered");
        Assert.Equal("and then this", r.Text);
        Assert.Empty(r.Attachments);
        Assert.Equal(3, host.Sent(Ops.AttachmentChunk).Count);
        var sent = host.Sent(Ops.SessionReply)[0];
        Assert.Equal(host.Sent(Ops.AttachmentStart)[0].GetProperty("attachment").GetString(), sent.GetProperty("attachments")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void A_reply_the_host_would_refuse_is_refused_here_first()
    {
        var r = new ReplyViewModel(Make.Session(), attach: false, deliveries, connection);

        r.Text = "  /clear";
        r.Send();

        Assert.Equal("A reply cannot start with /, which the agent would run as a command; type commands at the machine.", r.Error);
        Assert.Empty(host.Sent(Ops.SessionReply));
    }

    [Fact]
    public void A_session_asking_a_question_holds_the_reply_box()
    {
        var r = new ReplyViewModel(Make.Session(question: Make.Choice()), attach: false, deliveries, connection);
        r.Text = "hi";

        Assert.True(r.Held);
        Assert.False(r.CanSend);
        Assert.Equal("The session is waiting for an answer to its question. Answer that first, then reply.", r.HeldText);
    }

    [Fact]
    public async Task A_file_over_the_limit_is_refused_unless_it_is_an_image_that_can_be_made_smaller()
    {
        var r = new ReplyViewModel(Make.Session(), attach: true, deliveries, connection, new Shrinker());

        await r.AddAsync("big.zip", "application/zip", new byte[300_000]);
        Assert.Equal("Couldn't attach big.zip: it is 293 KiB, over the 256 KiB limit for an attachment.", r.Error);

        await r.AddAsync(null, "image/png", new byte[300_000]);
        var image = Assert.Single(r.Attachments);
        Assert.Equal(("image.jpg", "image/jpeg", true), (image.Name, image.Type, image.Resized));
        Assert.Equal(1000, image.Size);
    }

    sealed class Shrinker : IImageShrinker
    {
        public Task<byte[]?> ShrinkAsync(byte[] image, int maxBytes) => Task.FromResult<byte[]?>(new byte[1000]);
    }

    [Fact]
    public async Task End_session_on_a_working_session_takes_a_second_press_with_the_hosts_token_inside_the_window()
    {
        var presses = 0;
        host.On(Ops.SessionStop, d => ++presses == 1
            ? new StopResult(StopOutcome.Confirm, "this session is still working") { Confirm = "tok_1", ConfirmSeconds = 10 }
            : new StopResult(StopOutcome.Ended));
        var end = new EndSessionViewModel(Make.Session(status: SessionStatus.Working), deliveries, "Test PC");

        end.Press();
        await Wait.UntilAsync(() => end.SecondsLeft == 10, "the confirmation");
        Assert.Equal("Press End session again to end it: this session is still working. 10 s left.", end.StatusText);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(6, end.SecondsLeft);

        end.Press();
        await Wait.UntilAsync(() => end.StatusText == "Session ended.", "ended");
        var second = host.Sent(Ops.SessionStop)[1];
        Assert.Equal("tok_1", second.GetProperty("confirm").GetString());
        Assert.False(host.Sent(Ops.SessionStop)[0].TryGetProperty("confirm", out _));
    }

    [Fact]
    public async Task A_press_after_the_window_lapses_is_a_first_press_again()
    {
        host.On(Ops.SessionStop, _ => new StopResult(StopOutcome.Confirm, "this session is still working") { Confirm = "tok", ConfirmSeconds = 10 });
        var end = new EndSessionViewModel(Make.Session(status: SessionStatus.Working), deliveries);

        end.Press();
        await Wait.UntilAsync(() => end.SecondsLeft is not null, "armed");
        clock.Advance(TimeSpan.FromSeconds(11));
        end.Refresh();

        Assert.Null(end.SecondsLeft);
        Assert.Equal("End session NOT confirmed: no second press within 10 s. The session is still running.", end.StatusText);
        end.Press();
        await Wait.UntilAsync(() => host.Sent(Ops.SessionStop).Count == 2, "the second press");
        Assert.False(host.Sent(Ops.SessionStop)[1].TryGetProperty("confirm", out _));
    }

    [Fact]
    public void A_card_says_what_the_session_is_doing_as_the_dashboard_did()
    {
        var card = new SessionCardViewModel(Make.Session(background: Background.Of(2, 1), turns: 3), Context());
        Assert.Equal(("delegating", "Waiting for background agents"), card.State);
        Assert.Equal("2 background agents · 1 background command · 3 turns · pid 100 · C:\\src\\agent-bridge", card.Facts);
        Assert.Equal("Copilot · danswett/agent-bridge · main · claude-opus-5.5", card.Meta);
        Assert.Equal("for 5m", card.SinceText);

        card.Update(Make.Session(question: Make.Approval() with { AnswerAtMachine = true }));
        Assert.Equal(("waiting", "Waiting at the machine"), card.State);
        card.Update(Make.Session(status: SessionStatus.Ending));
        Assert.Equal("Ending session…", card.StatusText);
    }

    [Fact]
    public void A_folded_card_stays_folded_across_restarts_until_a_new_question_opens_it_once()
    {
        var card = new SessionCardViewModel(Make.Session(), Context()) { Folded = true };
        var again = new SessionCardViewModel(Make.Session(), Context(new FoldStore(folds)));
        Assert.True(again.Folded);

        again.Update(Make.Session(question: Make.Choice("q9")));
        Assert.False(again.Folded);
        again.Folded = true;
        again.Update(Make.Session(question: Make.Choice("q9"), response: "still asking"));
        Assert.True(again.Folded);
        Assert.True(card.Folded);
    }

    [Fact]
    public void Words_typed_for_a_question_that_changes_are_kept_on_the_card()
    {
        var card = new SessionCardViewModel(Make.Session(question: Make.Free("t1")), Context());
        card.Question!.FreeText = "my careful answer";

        card.Update(Make.Session(question: Make.Free("t2")));

        Assert.Equal("my careful answer", card.OrphanText);
        Assert.Equal("t2", card.Question!.Id);
        card.DismissOrphan();
        Assert.False(card.HasOrphan);
    }

    [Fact]
    public void Without_reply_or_stop_capabilities_a_card_offers_neither()
    {
        host.Capabilities.Clear();
        var card = new SessionCardViewModel(Make.Session(), new CardContext
        {
            Deliveries = deliveries,
            Folds = new FoldStore(null),
            Capabilities = () => [Capabilities.SessionsRead],
        });

        Assert.Null(card.Reply);
        Assert.Null(card.End);
    }
}
