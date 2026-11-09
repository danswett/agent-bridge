using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Restore;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Delivery.Tests;

// #687: a Copilot session whose host stopped taking messages is ended and resumed, but only when
// that loses nothing.
public sealed class DeafSessionWatchTests
{
    static readonly string[] EmptyBox = ["\u257B\u2584\u2584\u2584\u2584", "\u2503", "\u2579\u2580\u2580\u2580\u2580", " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help"];
    static readonly string[] DraftBox = ["\u257B\u2584\u2584\u2584\u2584", "\u2503 half a thought", "\u2579\u2580\u2580\u2580\u2580", " Interactive \u00B7 Allow All \u00B7 @ files \u00B7 # issues"];
    const string Id = "11111111-2222-3333-4444-555555555555";

    static Session Idle() => new() { Id = Id, Agent = AgentKind.Copilot, Status = SessionStatus.Idle, StatusSince = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, Activity = new Activity(), ProcessId = 42 };

    [Fact]
    public void Two_slow_typings_in_a_row_are_deaf_but_one_slow_typing_or_a_healthy_reply_between_is_not()
    {
        var policy = new DeafPolicy();

        Assert.False(policy.Note(Id, DeafSignal.SlowTyping));
        Assert.False(policy.Note(Id, DeafSignal.Healthy));
        Assert.False(policy.Note(Id, DeafSignal.SlowTyping));
        Assert.True(policy.Note(Id, DeafSignal.SlowTyping));
    }

    [Fact]
    public void A_session_just_recovered_is_not_recovered_again_until_it_hears_a_reply()
    {
        var policy = new DeafPolicy();
        Assert.True(policy.Note(Id, DeafSignal.HostError));

        policy.Recovering(Id);

        Assert.False(policy.Note(Id, DeafSignal.HostError));
        policy.Note(Id, DeafSignal.Healthy);
        Assert.True(policy.Note(Id, DeafSignal.HostError));
    }

    [Fact]
    public async Task An_unconfirmed_reply_on_an_idle_session_ends_and_resumes_it()
    {
        var rig = new Rig(Idle(), EmptyBox);

        await rig.Watch.NoteReplyAsync(Id, AgentKind.Copilot, false, 1000, AnswerOutcome.Unconfirmed);

        Assert.Equal(["stop", "resume"], rig.Calls);
    }

    [Fact]
    public async Task An_unconfirmed_reply_on_a_working_session_is_not_a_deaf_signal()
    {
        var rig = new Rig(Idle(), EmptyBox);

        await rig.Watch.NoteReplyAsync(Id, AgentKind.Copilot, true, 1000, AnswerOutcome.Unconfirmed);

        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task A_running_tool_background_work_or_a_question_keeps_a_deaf_session_alone()
    {
        foreach (var busy in new[]
        {
            Idle() with { Activity = new Activity { CurrentTool = new ToolCall { Id = "t1", Name = "bash", Status = ToolStatus.Running, StartedAt = DateTimeOffset.UnixEpoch } } },
            Idle() with { Activity = new Activity { Background = Background.Of(1, 0) } },
        })
        {
            var rig = new Rig(busy, EmptyBox);
            await rig.Watch.NoteAsync(Id, DeafSignal.HostError);
            Assert.Empty(rig.Calls);
        }
    }

    [Fact]
    public async Task Something_typed_in_the_input_box_keeps_a_deaf_session_alone()
    {
        var rig = new Rig(Idle(), DraftBox);

        await rig.Watch.NoteAsync(Id, DeafSignal.HostError);

        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task A_session_that_could_not_be_ended_is_not_resumed()
    {
        var rig = new Rig(Idle(), EmptyBox) { Stop = StopOutcome.Failed };

        await rig.Watch.NoteAsync(Id, DeafSignal.HostError);

        Assert.Equal(["stop"], rig.Calls);
    }

    sealed class Rig
    {
        public List<string> Calls { get; } = [];
        public StopOutcome Stop { get; set; } = StopOutcome.Ended;
        public DeafSessionWatch Watch { get; }

        public Rig(Session session, string[] screen) =>
            Watch = new DeafSessionWatch(new Source(session), new Screen(screen), new Stopper(this), new Resumer(this));

        sealed class Stopper(Rig rig) : ISessionStopper
        {
            public bool Available => true;
            public Task<StopResult> StopAsync(StopRequest request, CancellationToken cancellationToken = default)
            {
                rig.Calls.Add("stop");
                return Task.FromResult(new StopResult(rig.Stop));
            }
        }

        sealed class Resumer(Rig rig) : ISessionResumer
        {
            public Task<LaunchResult> ResumeAsync(ResumeAsk ask, CancellationToken cancellationToken = default)
            {
                rig.Calls.Add("resume");
                return Task.FromResult(new LaunchResult(LaunchOutcome.Started, null));
            }
        }

        sealed class Screen(string[] rows) : IConsoleInjector
        {
            public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default) => Task.FromResult(new ConsoleScreen(rows, null));
        }

        sealed class Source(Session session) : ISessionSource
        {
            public long Revision => 0;
            public event EventHandler<SessionChange>? Changed { add { } remove { } }
            public IReadOnlyList<Session> Snapshot() => [session];
            public SessionList List(string? cursor = null) => new([session], 0);
            public Session? Get(string id) => session;
            public SessionSubscription Subscribe() => throw new NotSupportedException();
            public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
