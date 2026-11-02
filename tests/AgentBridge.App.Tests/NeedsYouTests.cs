using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Needs you · N (#165, ui.md §6.1; pwa/test/unit/needs-you.test.mjs holds the PWA to the same
// rules): every pending question, oldest first, above the list and never moving it; Approve once
// and Deny pressed through the card's own question; forms opening their session; prompts only the
// machine can answer saying so; delegates named; and the count the tray and badge show.
public sealed class NeedsYouTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    readonly FakeTimeProvider clock = new(Make.T0.AddMinutes(10));
    readonly LockedDispatcher ui = new();
    HostConnection connection = null!;
    MainViewModel main = null!;
    int syncs;

    public Task InitializeAsync()
    {
        host.Sessions.Add(Make.Session("idle", updated: Make.T0));
        host.Sessions.Add(Make.Session("approval", question: Make.Approval() with { AskedAt = Make.T0.AddMinutes(3) }, updated: Make.T0.AddMinutes(3), title: "Push the branch"));
        host.Sessions.Add(Make.Session("form", question: Make.Choice() with { AskedAt = Make.T0.AddMinutes(1) }, updated: Make.T0.AddMinutes(1), title: "Pick a database"));
        host.Start();
        connection = host.Connect(ui, start: false);
        connection.Sessions.Synced += () => Interlocked.Increment(ref syncs);
        main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui, clock), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection, Time = clock }, new UsageViewModel()) { Time = clock };
        connection.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        await host.DisposeAsync();
    }

    Task SnapshotAsync() => Wait.UntilAsync(() => main.Sessions.Count == 3 && Volatile.Read(ref syncs) > 0, "the snapshot");

    string[] Lane() => TestUi.Read<string[]>(() => [.. main.NeedsYou.Select(e => e.Card.Id)]);
    string[] Rows() => TestUi.Read<string[]>(() => [.. main.Sessions.Select(c => c.Id)]);
    NeedsYouEntry Entry(string id) => TestUi.Read(() => main.NeedsYou.Single(e => e.Card.Id == id));
    SessionCardViewModel Card(string id) => TestUi.Read(() => main.AllCards.Single(c => c.Id == id));

    Task AskAsync(string id, Question q, Session? like = null) =>
        host.SendAsync(Events.SessionUpsert, (like ?? Make.Session(id)) with { Id = id, Status = SessionStatus.Waiting, Question = q, UpdatedAt = q.AskedAt });

    [Fact]
    public async Task The_lane_lists_every_pending_question_oldest_first_with_its_title_agent_age_and_first_line()
    {
        await SnapshotAsync();
        Assert.Equal(["form", "approval"], Lane());
        var form = Entry("form");
        Assert.Equal("Pick a database", form.Title);
        Assert.Equal("Which database?", form.Line);
        Assert.Equal("Copilot · 9m", form.Meta);
        Assert.Equal("Needs you · 2", TestUi.Read(() => main.NeedsYouHeader));
        Assert.Equal("Run dotnet test?", Entry("approval").Line);
    }

    [Fact]
    public async Task The_lane_never_moves_a_card_as_a_question_arrives_is_answered_and_goes()
    {
        host.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));
        await SnapshotAsync();
        var rows = Rows();

        await AskAsync("idle", Make.Approval("r9#x") with { AskedAt = Make.T0.AddMinutes(5) });
        await Wait.UntilAsync(() => main.NeedsYou.Any(e => e.Card.Id == "idle"), "the new entry");
        Assert.Equal(["form", "approval", "idle"], Lane());
        Assert.Equal(rows, Rows());

        ui.Post(() => Entry("idle").Approve());
        await Wait.UntilAsync(() => Card("idle").Question!.StatusText.StartsWith("Delivered", StringComparison.Ordinal), "delivered");
        await host.SendAsync(Events.SessionUpsert, Make.Session("idle", status: SessionStatus.Working, updated: Make.T0.AddMinutes(6)));
        await Wait.UntilAsync(() => main.NeedsYou.All(e => e.Card.Id != "idle"), "the entry leaving");
        Assert.Equal(rows, Rows());
    }

    [Fact]
    public async Task Approve_once_in_the_lane_is_the_cards_own_button_one_request_one_progress()
    {
        var reply = new TaskCompletionSource<AnswerResult>();
        host.On(Ops.SessionAnswer, _ => reply.Task.Result);
        await SnapshotAsync();
        var entry = Entry("approval");
        Assert.Equal(NeedsYouMode.Approve, entry.Mode);
        Assert.True(TestUi.Read(() => entry.CanAnswer));

        ui.Post(entry.Approve);
        await Wait.UntilAsync(() => entry.StatusText == "Sending…", "sending");
        var q = Card("approval").Question!;
        TestUi.Read(() =>
        {
            Assert.Equal("Sending…", q.StatusText);
            Assert.False(q.CanAnswer);
            Assert.False(entry.CanAnswer);
            return true;
        });
        // A second press while it goes sends nothing, from the lane or the card.
        ui.Post(entry.Deny);
        reply.SetResult(new AnswerResult(AnswerOutcome.Delivered));
        await Wait.UntilAsync(() => entry.StatusText == "Delivered. The session has your answer.", "delivered");
        var sent = Assert.Single(host.Sent(Ops.SessionAnswer));
        Assert.Equal("approval", sent.GetProperty("id").GetString());
        Assert.Equal("r1#d", sent.GetProperty("question").GetString());
        Assert.Equal("approve", sent.GetProperty("fields").GetProperty("decision").GetString());
        Assert.Equal("approve", TestUi.Read(() => q.SentValue));
        Assert.Equal("✓ Approve once", TestUi.Read(() => entry.ApproveLabel));
    }

    [Fact]
    public async Task Deny_in_the_lane_refused_shows_the_reason_and_can_be_pressed_again_as_a_new_request()
    {
        var refuse = true;
        host.On(Ops.SessionAnswer, _ => refuse ? new AnswerResult(AnswerOutcome.Refused, "the session is running elevated") : new AnswerResult(AnswerOutcome.Delivered));
        await SnapshotAsync();
        var entry = Entry("approval");

        ui.Post(entry.Deny);
        await Wait.UntilAsync(() => entry.StatusText == "The machine refused this answer: the session is running elevated.", "refused");
        Assert.Equal(Tone.Bad, TestUi.Read(() => entry.StatusTone));
        Assert.True(TestUi.Read(() => entry.CanAnswer));

        refuse = false;
        ui.Post(entry.Approve);
        await Wait.UntilAsync(() => entry.StatusText.StartsWith("Delivered", StringComparison.Ordinal), "delivered");
        var sent = host.Sent(Ops.SessionAnswer);
        Assert.Equal(["deny", "approve"], sent.Select(s => s.GetProperty("fields").GetProperty("decision").GetString()));
        Assert.NotEqual(sent[0].GetProperty("request").GetString(), sent[1].GetProperty("request").GetString());
    }

    [Fact]
    public async Task A_send_the_machine_failed_is_sent_again_from_the_lane_with_the_same_request()
    {
        var fail = true;
        host.On(Ops.SessionAnswer, _ => fail ? throw new InvalidOperationException("the host broke") : new AnswerResult(AnswerOutcome.Delivered));
        await SnapshotAsync();
        var entry = Entry("approval");

        ui.Post(entry.Approve);
        await Wait.UntilAsync(() => entry.ShowResend, "the failure");
        Assert.False(TestUi.Read(() => entry.CanAnswer));
        Assert.True(TestUi.Read(() => Card("approval").Question!.ShowResend));

        fail = false;
        ui.Post(entry.Resend);
        await Wait.UntilAsync(() => entry.StatusText.StartsWith("Delivered", StringComparison.Ordinal), "delivered");
        var sent = host.Sent(Ops.SessionAnswer);
        Assert.Equal(2, sent.Count);
        Assert.Equal(sent[0].GetProperty("request").GetString(), sent[1].GetProperty("request").GetString());
    }

    [Fact]
    public async Task Stale_from_the_lane_holds_both_buttons_as_on_the_card()
    {
        host.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Stale, "the session is no longer asking that question"));
        await SnapshotAsync();
        var entry = Entry("approval");

        ui.Post(entry.Approve);
        await Wait.UntilAsync(() => entry.StatusText == "Already answered or gone: the session is no longer asking this.", "stale");
        Assert.False(TestUi.Read(() => entry.CanAnswer));
        Assert.False(TestUi.Read(() => Card("approval").Question!.CanAnswer));
        ui.Post(entry.Deny);
        Assert.Single(host.Sent(Ops.SessionAnswer));
    }

    [Fact]
    public async Task A_form_opens_its_session_rather_than_answering_in_the_lane()
    {
        await SnapshotAsync();
        var entry = Entry("form");
        Assert.Equal(NeedsYouMode.Open, entry.Mode);
        Assert.False(entry.Approvable);
        SessionCardViewModel? opened = null;
        main.OpenRequested += c => opened = c;
        ui.Post(() => Card("form").Folded = true);

        ui.Post(entry.Open);

        Assert.Same(Card("form"), opened);
        Assert.False(TestUi.Read(() => Card("form").Folded));
        Assert.Empty(host.Sent(Ops.SessionAnswer));
    }

    [Fact]
    public async Task A_prompt_only_the_machine_can_answer_says_so_and_offers_nothing_to_press()
    {
        await SnapshotAsync();
        await AskAsync("idle", Make.Approval("r2#m") with { AnswerAtMachine = true, AskedAt = Make.T0.AddMinutes(4) });
        await Wait.UntilAsync(() => main.NeedsYou.Any(e => e.Card.Id == "idle"), "the entry");
        var entry = Entry("idle");
        Assert.Equal(NeedsYouMode.AtMachine, entry.Mode);
        Assert.True(entry.AtMachine);
        Assert.False(entry.Approvable);
        Assert.False(TestUi.Read(() => entry.CanAnswer));
        ui.Post(entry.Approve);
        Assert.Empty(host.Sent(Ops.SessionAnswer));
    }

    [Fact]
    public async Task A_delegates_question_is_labelled_with_the_delegates_name()
    {
        await SnapshotAsync();
        var like = Make.Session("idle") with
        {
            WaitingOn = "agent-7",
            Subagents = [new Subagent { Id = "agent-7", Kind = SubagentKind.Background, Status = SubagentStatus.Waiting, Name = "Read witness shell logic" }],
        };
        await AskAsync("idle", Make.Approval("r3#d") with { Agent = "agent-7", AskedAt = Make.T0.AddMinutes(8) }, like);
        await Wait.UntilAsync(() => main.NeedsYou.Any(e => e.Card.Id == "idle"), "the entry");
        var entry = Entry("idle");
        Assert.Equal("Read witness shell logic", entry.Delegate);
        Assert.Equal("Copilot · Read witness shell logic asks · 2m", entry.Meta);
        Assert.Null(Entry("approval").Delegate);
    }

    [Fact]
    public void A_delegate_the_list_left_out_is_still_a_delegate_just_unnamed()
    {
        var s = Make.Session(question: Make.Approval() with { Agent = "agent-9" }) with { SubagentsMore = 4 };
        Assert.Equal("A delegate", NeedsYouEntry.DelegateOf(s));
        Assert.Null(NeedsYouEntry.DelegateOf(Make.Session(question: Make.Approval())));
    }

    [Fact]
    public void A_long_first_line_is_cut_and_markdown_marks_are_dropped()
    {
        Assert.Equal("Ship the relay now?", NeedsYouEntry.PlainLine("## **Ship** the `relay` _now_?"));
        var cut = NeedsYouEntry.PlainLine(new string('x', 400));
        Assert.Equal(140, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.Equal("Allow shell?", NeedsYouEntry.LineOf(Make.Approval() with { Message = "Allow **shell**?\n\nPush it\n\n```\ngit push origin main\n```" }));
    }

    [Fact]
    public void A_question_that_opens_with_a_fenced_example_shows_the_question_after_it_not_the_examples_code()
    {
        Assert.Equal("Which option?", NeedsYouEntry.LineOf(Make.Form() with { Message = "```\nport: 8080\n```\n\nWhich option?" }));
        Assert.Equal("After the tildes", NeedsYouEntry.LineOf(Make.Form() with { Message = "~~~\na\n```\nb\n~~~\nAfter the tildes" }));
        Assert.Equal("only code", NeedsYouEntry.LineOf(Make.Form() with { Message = "```\nonly code\n```" }));
        Assert.Equal("Which option?", NeedsYouEntry.LineOf(Make.Form() with { Message = "```\rport: 8080\r```\r\rWhich option?" }));
        // A fence in a quote or a list item is skipped too.
        Assert.Equal("Which option?", NeedsYouEntry.LineOf(Make.Form() with { Message = "> ```\n> port: 8080\n> ```\n\nWhich option?" }));
        Assert.Equal("Which option?", NeedsYouEntry.LineOf(Make.Form() with { Message = "- ```\n  port: 8080\n  ```\n\nWhich option?" }));
        // Nested lists, and a quote marker that is code, as the renderer reads them.
        Assert.Equal("Which option?", NeedsYouEntry.LineOf(Make.Form() with { Message = "- - ```\n    example\n    ```\n\nWhich option?" }));
        Assert.Equal("After", NeedsYouEntry.LineOf(Make.Form() with { Message = "~~~\n> ~~~\nexample\n~~~\nAfter" }));
        Assert.Equal("Which database?", NeedsYouEntry.LineOf(Make.Form() with { Message = "Which **database**?\nPick one." }));
    }

    [Fact]
    public async Task A_question_updated_under_the_same_id_shows_what_it_says_now()
    {
        await SnapshotAsync();
        var q = Make.Form() with { Id = "f9", Message = "```\nport: 8080\n```", AskedAt = Make.T0.AddMinutes(4) };
        await AskAsync("idle", q);
        await Wait.UntilAsync(() => main.NeedsYou.Any(e => e.Card.Id == "idle"), "the entry");
        Assert.Equal("port: 8080", Entry("idle").Line);
        // The message a budget had cut, restored under the same id.
        await host.SendAsync(Events.SessionUpsert, Make.Session("idle") with { Status = SessionStatus.Waiting, Question = q with { Message = "```\nport: 8080\n```\n\nWhich option?" }, UpdatedAt = Make.T0.AddMinutes(5) });
        await Wait.UntilAsync(() => Entry("idle").Line == "Which option?", "the entry's new line");
    }

    [Theory]
    // The agent's words open a fence, so the command's fence closes it and the command is drawn as
    // Markdown: the link shows as "safe", the asterisks as emphasis.
    [InlineData("Allow **shell**? Here goes:\n```\n\n````\ncurl [safe](https://other.example) *x*\n```\n````")]
    [InlineData("Send the data to [our backup](https://other.example)?\n\n```\ncurl -T data\n```")]
    [InlineData("Send to [backup][1]?\n\n[1]: https://other.example")]
    [InlineData("Allow **shell**?\n\n```\nls\n```\n\nAnd then more")]
    [InlineData("Allow **shell**?\n\n```\nls")]
    // Words after the opening fence are not drawn: a tool's description could say one thing there
    // and another in the block.
    [InlineData("Allow **mytool**?\n\n``` Deletes all backups\nPrint the current status\n```")]
    // A fence nested in a quote or a list escapes the opening-fence check, info string and all.
    [InlineData("> ```sh Deletes all backups\n> Print the current status\n> ```")]
    [InlineData("Allow **shell**?\n\n- ```sh Deletes all backups\n  a\n  ```\n\n```\nls\n```")]
    // A shortcut reference link defined inside a quote draws its label, not where it goes.
    [InlineData("> Send to [backup]\n>\n> [backup]: https://other.example")]
    // Lines a bare CR breaks count too: thirteen of them are more than the lane shows whole.
    [InlineData("Allow **shell**?\r\r```\r1\r2\r3\r4\r5\r6\r7\r8\r9\r10\r```")]
    public async Task An_approval_whose_prompt_a_fence_or_a_link_could_draw_differently_from_what_it_allows_opens_its_card(string message)
    {
        await SnapshotAsync();
        await AskAsync("idle", Make.Approval("r9#s") with { Message = message, AskedAt = Make.T0.AddMinutes(4) });
        await Wait.UntilAsync(() => main.NeedsYou.Any(e => e.Card.Id == "idle"), "the entry");
        Assert.Equal(NeedsYouMode.Open, Entry("idle").Mode);
        Assert.Null(Entry("idle").Prompt);
    }

    [Theory]
    [InlineData("Allow **shell**?\n\n````\ncat <<EOF\n```\nx\n```\nEOF\n````")]
    [InlineData("Allow the **github** tool `create_issue`?")]
    [InlineData("Allow **shell**?\r\n\r\n```\r\nls -la\r\n```")]
    [InlineData("Allow **mcp**?\n\n```json\n{ \"a\": 1 }\n```")]
    [InlineData("Allow **edit**?\n\n```diff\n+a\n```")]
    public void A_prompt_shaped_as_a_host_writes_a_permission_is_whole_with_inner_fences_no_command_or_CRLF(string message) =>
        Assert.True(NeedsYouEntry.WholePrompt(Make.Approval() with { Message = message }));

    [Fact]
    public void The_demos_permission_prompt_is_shaped_as_a_host_writes_it_so_the_lane_answers_it() =>
        Assert.True(NeedsYouEntry.WholePrompt(DemoHost.Examples().Single(s => s.Id == "demo-approval").Question!));

    [Fact]
    public async Task An_approval_answered_in_the_lane_shows_its_whole_prompt_as_written_so_approve_never_allows_what_is_not_on_screen()
    {
        await SnapshotAsync();
        const string message = "Allow **shell**?\n\n````\nrm -rf build/**/cache && python __init__.py \"a  b\" ```\n````";
        await AskAsync("idle", Make.Approval("r4#w") with { Message = message, AskedAt = Make.T0.AddMinutes(4) });
        await Wait.UntilAsync(() => main.NeedsYou.Any(e => e.Card.Id == "idle"), "the entry");
        var entry = Entry("idle");
        Assert.Equal(NeedsYouMode.Approve, entry.Mode);
        Assert.Equal(message, entry.Prompt);
        Assert.False(entry.ShowLine);
    }

    [Fact]
    public async Task An_approval_too_long_to_show_whole_opens_its_card_rather_than_approving_in_the_lane()
    {
        await SnapshotAsync();
        var diff = string.Join('\n', Enumerable.Range(0, NeedsYouEntry.PromptMaxLines).Select(i => $"+line {i}"));
        await AskAsync("idle", Make.Approval("r5#d") with { Message = $"Allow **edit**?\n\n```diff\n{diff}\n```", AskedAt = Make.T0.AddMinutes(4) });
        await Wait.UntilAsync(() => main.NeedsYou.Any(e => e.Card.Id == "idle"), "the entry");
        var entry = Entry("idle");
        Assert.Equal(NeedsYouMode.Open, entry.Mode);
        Assert.Null(entry.Prompt);
        Assert.Equal("Allow edit?", entry.Line);
        Assert.False(NeedsYouEntry.WholePrompt(Make.Approval() with { Message = new string('x', NeedsYouEntry.PromptMaxChars + 1) }));
        // The card still offers it, with everything on show.
        Assert.True(TestUi.Read(() => Card("idle").Question!.Answerable));
    }

    [Fact]
    public async Task An_approval_with_a_table_from_codex_or_since_marked_truncated_opens_its_card_rather_than_approving_in_the_lane()
    {
        await SnapshotAsync();
        await AskAsync("idle", Make.Approval("r7#t") with { Message = "Allow **sync**?\n\n| path | action |\n|---|---|\n| a | delete |", AskedAt = Make.T0.AddMinutes(4) });
        await Wait.UntilAsync(() => main.NeedsYou.Any(e => e.Card.Id == "idle"), "the table's entry");
        Assert.Equal(NeedsYouMode.Open, Entry("idle").Mode);

        await AskAsync("idle", Make.Approval("r8#c") with { AskedAt = Make.T0.AddMinutes(5) }, Make.Session("idle") with { Agent = AgentKind.Codex });
        await Wait.UntilAsync(() => main.NeedsYou.Any(e => e.Card.Id == "idle" && e.QuestionId == "r8#c"), "the Codex entry");
        Assert.Equal(NeedsYouMode.Open, Entry("idle").Mode);

        // The same question, cut by a later budget-trimmed upsert under the same id.
        Assert.Equal(NeedsYouMode.Approve, Entry("approval").Mode);
        await host.SendAsync(Events.SessionUpsert, Make.Session("approval", question: Make.Approval() with { AskedAt = Make.T0.AddMinutes(3), Truncated = true }, updated: Make.T0.AddMinutes(6), title: "Push the branch"));
        await Wait.UntilAsync(() => Entry("approval").Mode == NeedsYouMode.Open, "the truncated entry");
    }

    [Fact]
    public async Task Opening_an_entry_takes_keyboard_focus_to_its_card()
    {
        await SnapshotAsync();
        ui.Post(Entry("form").Open);
        Assert.True(TestUi.Read(() => Card("form").WantsFocus));
    }

    [Fact]
    public async Task A_delegate_named_by_a_later_update_is_named_in_the_entry_it_already_has()
    {
        await SnapshotAsync();
        var question = Make.Approval("r6#d") with { Agent = "agent-8", AskedAt = Make.T0.AddMinutes(8) };
        await AskAsync("idle", question, Make.Session("idle") with { WaitingOn = "agent-8", SubagentsMore = 1 });
        await Wait.UntilAsync(() => main.NeedsYou.Any(e => e.Card.Id == "idle"), "the entry");
        var entry = Entry("idle");
        Assert.Equal("A delegate", entry.Delegate);

        await AskAsync("idle", question, Make.Session("idle") with
        {
            WaitingOn = "agent-8",
            Subagents = [new Subagent { Id = "agent-8", Kind = SubagentKind.Task, Status = SubagentStatus.Waiting, Name = "Check the fixtures" }],
            UpdatedAt = Make.T0.AddMinutes(9),
        });
        await Wait.UntilAsync(() => entry.Delegate == "Check the fixtures", "the delegate's name");
        Assert.Same(entry, Entry("idle"));
        Assert.Contains("Check the fixtures asks", TestUi.Read(() => entry.Meta));
    }

    [Fact]
    public async Task The_count_the_tray_and_badge_show_is_the_lanes_and_follows_it()
    {
        host.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));
        await SnapshotAsync();
        Assert.Equal(2, TestUi.Read(() => main.Waiting));
        await AskAsync("idle", Make.Free() with { AskedAt = Make.T0.AddMinutes(9) });
        await Wait.UntilAsync(() => main.Waiting == 3, "three");
        Assert.Equal(TestUi.Read(() => main.NeedsYou.Count), TestUi.Read(() => main.Waiting));
        Assert.Equal("Needs you · 3", TestUi.Read(() => main.NeedsYouHeader));

        foreach (var id in new[] { "idle", "form", "approval" })
        {
            await host.SendAsync(Events.SessionUpsert, Make.Session(id, status: SessionStatus.Working, updated: Make.T0.AddMinutes(20)));
        }
        await Wait.UntilAsync(() => main.Waiting == 0, "none");
        Assert.False(TestUi.Read(() => main.HasNeedsYou));
    }
}
