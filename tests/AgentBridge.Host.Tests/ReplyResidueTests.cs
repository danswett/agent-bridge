using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Windows;
using AgentBridge.Protocol;
using Xunit;

// #2144: what is already in Copilot's box when a reply comes, a person's or this host's own,
// is refused as already typed and never touched.
public sealed class ReplyResidueTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    const string Words = "please rebase onto main and report the new head";
    const string Typed = "something is already typed in Copilot's input box";

    [Fact]
    public async Task Words_a_person_typed_are_refused_unchanged()
    {
        await using var h = await Harness.Create();
        h.Draft = "my own unfinished note";
        var result = await h.Replier.ReplyAsync(h.Request("go on"));
        AssertUntouched(h, result, "my own unfinished note");
    }

    [Theory]
    [InlineData(Words + " and also this")]
    [InlineData("first " + Words)]
    [InlineData("please rebase onto main and report the old head")]
    public async Task The_hosts_words_with_any_person_edit_are_refused_unchanged(string edited)
    {
        await using var h = await Harness.Create();
        await h.LeaveUnsent();
        h.Draft = edited;
        AssertUntouched(h, await h.Replier.ReplyAsync(h.Request("go on")), edited);
    }

    [Fact]
    public async Task After_a_host_restart_there_is_no_record_and_the_words_are_refused()
    {
        await using var h = await Harness.Create();
        await h.LeaveUnsent();
        h.Restart();
        AssertUntouched(h, await h.Replier.ReplyAsync(h.Request("go on")), Words);
    }

    [Fact]
    public async Task The_hosts_words_while_a_turn_runs_keep_todays_refusal()
    {
        await using var h = await Harness.Create();
        await h.LeaveUnsent();
        await h.StartTurn();
        AssertUntouched(h, await h.Replier.ReplyAsync(h.Request("go on")), Words);
    }

    [Fact]
    public async Task The_hosts_words_in_shell_mode_are_refused_unchanged()
    {
        await using var h = await Harness.Create();
        await h.LeaveUnsent();
        h.Shell = true;
        var result = await h.Replier.ReplyAsync(h.Request("go on"));
        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.Equal(0, h.Clears);
        Assert.Empty(h.Sent);
        Assert.Equal(Words, h.Draft);
    }

    static void AssertUntouched(Harness h, ReplyResult result, string draft)
    {
        Assert.Equal(AnswerOutcome.Refused, result.Outcome);
        Assert.StartsWith(Typed, result.Reason);
        Assert.Equal(0, h.Clears);
        Assert.Empty(h.Sent);
        Assert.Equal(draft, h.Draft);
    }

    sealed class Harness : IConsoleInjector, IAgentHaBridgeProbe, ITargetGuard, IAsyncDisposable
    {
        readonly Fixture f = new();
        readonly SessionSource source;
        readonly string events;
        public ReplyService Replier = null!;
        public readonly List<string> Log = [];
        public readonly List<string> Sent = [];
        public string Draft = "";
        public bool Shell;
        public int Clears;
        // How the next reply's words are left in the box instead of sent: null sends them.
        (bool Swallowed, bool Wrapped)? unsent;

        Harness()
        {
            events = f.CopilotSession(Id, 100);
            Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
            f.Processes.Run(AgentKind.Copilot, 100);
            source = f.Source();
            Restart();
        }

        public static async Task<Harness> Create()
        {
            var h = new Harness();
            await h.source.RefreshAsync();
            return h;
        }

        public void Restart() => Replier = new ReplyService(new DeliveryGate(), source, f.Processes, this, this, this, new ReplyOptions
        {
            CopilotHome = f.CopilotHome, PlatformSupported = true, PreRead = false,
            ConfirmTimeout = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(10),
        }, Log.Add);

        public ReplyRequest Request(string text) => new() { Id = Id, Request = Guid.NewGuid().ToString(), Text = text };

        public async Task StartTurn()
        {
            Fixture.Append(events, Copilot.TurnStart(20));
            await source.RefreshAsync();
        }

        // A reply whose words reach the box but are never sent: its Enter held back by a box
        // that changed (partway), or pressed and not taken (swallowed).
        public async Task LeaveUnsent(bool swallowed = false, bool wrapped = false)
        {
            unsent = (swallowed, wrapped);
            var first = await Replier.ReplyAsync(Request(Words));
            unsent = null;
            Assert.Equal(AnswerOutcome.Unconfirmed, first.Outcome);
            Assert.Equal(Words.Replace("\n", " ", StringComparison.Ordinal), Draft.Replace("\n", " ", StringComparison.Ordinal));
            Log.Clear();
        }

        public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken ct = default)
        {
            if (plan.Proceed?.Invoke() is { } why) return Task.FromResult(new InjectionResult(InjectionStatus.NothingTyped, why));
            var rows = Rows();
            if (plan.Sends.Any(s => (s.Expect ?? []).Any(r => r.Row >= rows.Count || rows[r.Row].TrimEnd(' ') != r.Text.TrimEnd(' '))
                || (s.Checks ?? []).Any(c => !c.Passes(rows))))
                return Task.FromResult(new InjectionResult(InjectionStatus.NothingTyped, "screen-changed"));
            if (!plan.Sends.Any(s => s.Enter))
            {
                Assert.Equal("\u001b[1;5F\u0015", Assert.Single(plan.Sends).Text);
                Clears++;
                Draft = "";
                return Task.FromResult(new InjectionResult(InjectionStatus.Typed, ""));
            }
            Assert.Equal("", Draft);
            var text = plan.Sends[0].Text;
            if (unsent is { } how)
            {
                // Wrapped as the CLI draws a long line in a narrow box: the space at the wrap goes.
                Draft = how.Wrapped ? text.Insert(text.IndexOf(" report", StringComparison.Ordinal), "\n").Replace("\n ", "\n", StringComparison.Ordinal) : text;
                return Task.FromResult(how.Swallowed
                    ? new InjectionResult(InjectionStatus.Typed, "")
                    : new InjectionResult(InjectionStatus.Interrupted, WorkerInjector.ScreenChangedPartway));
            }
            Sent.Add(text);
            Fixture.Append(events, Copilot.User(text, 10));
            return Task.FromResult(new InjectionResult(InjectionStatus.Typed, ""));
        }

        IReadOnlyList<string> Rows() =>
            ["\u25CF Done.", "", "\u257B\u2584\u2584\u2584\u2584\u2584\u2584\u2584\u2584",
             .. Draft.Split('\n').Select(line => "\u2503 " + line),
             "\u2579\u2580\u2580\u2580\u2580\u2580\u2580\u2580\u2580",
             Shell ? " Shell \u00B7 Allow All" : Draft.Length == 0 ? " Interactive \u00B7 / commands \u00B7 ? help" : " Interactive \u00B7 @ files \u00B7 # issues"];

        public Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken ct = default) => Task.FromResult(new ConsoleScreen(Rows(), null));
        public string? Active() => null;
        public string? Refuse(int processId) => null;
        public async ValueTask DisposeAsync() { await source.DisposeAsync(); f.Dispose(); }
    }
}
