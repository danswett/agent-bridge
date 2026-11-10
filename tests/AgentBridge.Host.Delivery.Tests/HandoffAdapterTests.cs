using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Handoff;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

public sealed class HandoffAdapterTests : IDisposable
{
    const string Id = "55555555-5555-4555-8555-555555555555";
    readonly Fixture f = new();
    AgentHomes Homes => new(f.CopilotHome, f.ClaudeHome, f.CodexHome);
    public void Dispose() => f.Dispose();
    static AgentKind Kind(string launcher) => launcher is "agency" or "copilot" ? AgentKind.Copilot
        : launcher == "claude" ? AgentKind.Claude : AgentKind.Codex;

    string Transcript(string launcher)
    {
        var path = launcher is "agency" or "copilot" ? f.CopilotSession(Id, null)
            : launcher == "claude" ? f.ClaudeTranscript(Id) : f.CodexTranscript(Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string[] lines = launcher switch
        {
            "copilot" or "agency" => [Copilot.User("Finish the parser", 1),
                Copilot.ToolStart("edit1", "edit", new { path = "Parser.cs" }, 2), Copilot.ToolDone("edit1", 3),
                Copilot.Assistant("Parser added.", 4), Copilot.TurnEnd(5)],
            "claude" => [Claude.User("Finish the parser", 1),
                Claude.Assistant(2, "tool_use", Claude.ToolUse("edit1", "Edit", new JsonObject { ["file_path"] = "Parser.cs" })),
                Claude.ToolResult("edit1", 3), Claude.Assistant(4, "end_turn", Claude.Text("Parser added."))],
            _ => [Codex.Line("session_meta", new { id = Id, cwd = f.Root }, 0),
                Codex.Event(new { type = "user_message", message = "Finish the parser" }, 1),
                Codex.Event(new { type = "patch_apply_begin", call_id = "edit1", changes = new { Parser = new { } } }, 2)
                    .Replace("\"Parser\":", "\"Parser.cs\":", StringComparison.Ordinal),
                Codex.Event(new { type = "patch_apply_end", call_id = "edit1", success = true }, 3),
                Codex.Event(new { type = "agent_message", message = "Parser added." }, 4),
                Codex.Event(new { type = "task_complete" }, 5)],
        };
        Fixture.Append(path, lines);
        return path;
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("agency")]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task An_ended_agents_real_transcript_builds_a_note_without_changing_its_files(string launcher)
    {
        var path = Transcript(launcher);
        var before = File.ReadAllBytes(path);
        var folder = Path.Combine(f.Root, "handoffs");
        var adapter = new FileHandoffTranscript(Kind(launcher), Homes);
        var result = await new HandoffService(new NeverAsk(), adapter, new FileHandoffStore(folder), f.Time)
            .RunAsync(Id, HandoffSessionState.Ended);

        Assert.True(result.Confirmed, result.Reason);
        var note = File.ReadAllText(Path.Combine(folder, Id + ".handoff.md"));
        Assert.Contains("Parser added.", note);
        Assert.Contains("Finish the parser", note);
        Assert.Contains("Parser.cs", note);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(["handoffRequested", "handoffConfirmed"], result.Steps.Select(s => s.Name));
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("agency")]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task An_idle_agent_uses_the_existing_reply_path_and_waits_for_its_own_new_turn(string launcher)
    {
        var source = new Source(Kind(launcher));
        var replier = new Replier(request =>
        {
            Assert.Equal(Id, request.Id);
            Assert.Equal(HandoffService.Prompt, request.Text);
            Assert.True(Guid.TryParse(request.Request, out _));
            source.Set(source.Current with { Activity = new Activity { Turn = "new", ResponseTurn = "new",
                UserMessage = request.Text, Response = "A delegates words", Source = new ActivitySource { Response = "helper" },
                Own = new OwnActivity { Response = "Done: parser. Open: tests. Files: Parser.cs." } } });
            return new ReplyResult(AnswerOutcome.Delivered);
        });

        var note = await new ReplyHandoffAgent(source, replier).AskForNoteAsync(Id, HandoffService.Prompt, default);

        Assert.Equal("Done: parser. Open: tests. Files: Parser.cs.", note);
        Assert.Equal(1, replier.Calls);
    }

    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Waiting)]
    [InlineData(SessionStatus.Ending)]
    public async Task A_session_not_idle_is_never_prompted_or_its_permission_answered(SessionStatus status)
    {
        var source = new Source(AgentKind.Copilot);
        source.Set(source.Current with { Status = status });
        var replier = new Replier(_ => new ReplyResult(AnswerOutcome.Delivered));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReplyHandoffAgent(source, replier).AskForNoteAsync(Id, HandoffService.Prompt, default));
        Assert.Equal(0, replier.Calls);
    }

    [Fact]
    public async Task An_old_response_is_not_a_handoff_and_waiting_cancels_without_retrying_delivery()
    {
        var source = new Source(AgentKind.Copilot);
        source.Set(source.Current with { Activity = new Activity { Turn = "old", ResponseTurn = "old",
            UserMessage = HandoffService.Prompt, Response = "Old note" } });
        var replier = new Replier(_ => new ReplyResult(AnswerOutcome.Delivered));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ReplyHandoffAgent(source, replier).AskForNoteAsync(Id, HandoffService.Prompt, stop.Token));
        Assert.Equal(1, replier.Calls);
    }

    [Fact]
    public async Task A_later_response_event_completes_the_handoff_only_when_the_main_agent_answers()
    {
        var source = new Source(AgentKind.Copilot);
        var replier = new Replier(_ => new ReplyResult(AnswerOutcome.Delivered));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = new ReplyHandoffAgent(source, replier).AskForNoteAsync(Id, HandoffService.Prompt, stop.Token);
        Assert.False(pending.IsCompleted);

        source.Set(source.Current with { Activity = new Activity { Turn = "new", ResponseTurn = "new",
            UserMessage = HandoffService.Prompt, Response = "Delegate only", Source = new ActivitySource { Response = "helper" } } });
        await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromMilliseconds(50)));
        source.Set(source.Current with { Activity = source.Current.Activity with { Own = new OwnActivity { Response = "Main note" } } });

        Assert.Equal("Main note", await pending.WaitAsync(stop.Token));
    }

    [Fact]
    public async Task Cancellation_bounds_delivery_even_when_the_existing_replier_ignores_the_token()
    {
        var replier = new StalledReplier();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ReplyHandoffAgent(new Source(AgentKind.Copilot), replier).AskForNoteAsync(Id, HandoffService.Prompt, stop.Token));
        Assert.Equal(1, replier.Calls);
    }

    [Fact]
    public async Task A_note_trimmed_only_for_the_protocol_is_read_on_its_unbudgeted_change_event()
    {
        var source = new Source(AgentKind.Copilot);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = new ReplyHandoffAgent(source, new Replier(_ => new ReplyResult(AnswerOutcome.Delivered)))
            .AskForNoteAsync(Id, HandoffService.Prompt, stop.Token);
        source.Current = source.Current with { Activity = new Activity { Turn = "new", ResponseTurn = "new",
            Response = "Cut", ResponseTruncated = true } };
        source.SetUnbudgeted(source.Current with { Activity = source.Current.Activity with {
            UserMessage = HandoffService.Prompt, Response = "Complete note", ResponseTruncated = null } });

        Assert.Equal("Complete note", await pending.WaitAsync(stop.Token));
    }

    [Fact]
    public async Task A_missing_or_unreadable_transcript_never_confirms_an_empty_handoff()
    {
        var adapter = new FileHandoffTranscript(AgentKind.Copilot, Homes);
        Assert.Null(await adapter.ReadAsync(Id, default));
        File.WriteAllText(Transcript("copilot"), "not-json\n");
        Assert.Null(await adapter.ReadAsync(Id, default));
        Assert.Null(await adapter.ReadAsync("..", default));
    }

    sealed class NeverAsk : IHandoffAgent
    {
        public Task<string?> AskForNoteAsync(string sessionId, string prompt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An ended session must not be prompted.");
    }

    sealed class Replier(Func<ReplyRequest, ReplyResult> answer) : ISessionReplier
    {
        public int Calls;
        public bool Available => true;
        public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null)
        {
            Calls++;
            return Task.FromResult(answer(request));
        }
    }

    sealed class StalledReplier : ISessionReplier
    {
        public int Calls;
        public bool Available => true;
        public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null)
        { Calls++; return new TaskCompletionSource<ReplyResult>().Task; }
    }

    sealed class Source(AgentKind kind) : ISessionSource
    {
        public Session Current = new() { Id = Id, Agent = kind, Status = SessionStatus.Idle,
            StatusSince = Fixture.T0, UpdatedAt = Fixture.T0, Activity = new Activity() };
        public event EventHandler<SessionChange>? Changed;
        public event EventHandler<string>? UnbudgetedChanged;
        Session? unbudgeted;
        public void SetUnbudgeted(Session session) { unbudgeted = session; UnbudgetedChanged?.Invoke(this, Id); }
        public Session? GetUnbudgeted(string id) => id == Id ? unbudgeted ?? Current : null;
        public void Set(Session session) { Current = session; Changed?.Invoke(this, new(SessionChangeKind.Upserted, Id, session, ++revision)); }
        long revision;
        public long Revision => revision;
        public Session? Get(string id) => id == Id ? Current : null;
        public IReadOnlyList<Session> Snapshot() => [Current];
        public SessionList List(string? cursor = null) => new([Current], revision);
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
