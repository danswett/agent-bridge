using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.App.Core;

// A pretend host inside the app (--demo): the protocol's example sessions served on a pipe of
// its own, answering every op as a cooperative host would. For trying the app without a host
// or agents, for screenshots, and for CI's check that the published app starts and draws
// cards. It never touches the real host, its profile or any session.
public sealed partial class DemoHost : IAsyncDisposable
{
    readonly LocalServer server;
    readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    readonly List<IProtocolChannel> subscribers = [];
    readonly object gate = new();
    readonly Dictionary<string, (string Token, DateTimeOffset At)> armed = [];
    readonly Dictionary<string, long> attachments = [];
    readonly Dictionary<string, LaunchResult> launches = [];
    readonly List<ResumableSession> resumable = [];
    // #800: each session's attention as the host sends it (Session.attention), and this demo's
    // sign-in flows, each with the channels in it: a code goes to those alone.
    readonly Dictionary<string, JsonObject> attention = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<IProtocolChannel>> flows = new(StringComparer.Ordinal);
    int flowCount;
    long revision = 118;

    public string Folder { get; }
    public LocalEndpoint Endpoint => server.Endpoint;

    readonly string version;
    readonly bool remote;

    // The machine the demo pretends to be: this one, or another of the account's (Remote),
    // which the app reaches as it would through the relay, without host management.
    public string MachineId => remote ? RemoteId : "demo";
    public string MachineName => remote ? "Build server" : "Demo machine";
    public const string RemoteId = "demo-remote";

    DemoHost(string folder, string version, bool remote = false)
    {
        Folder = folder;
        this.version = version;
        this.remote = remote;
        foreach (var s in remote ? RemoteExamples() : Examples()) { sessions[s.Id] = s; }
        resumable.Add(new ResumableSession
        {
            Id = remote ? "demo-remote-ended" : "demo-ended",
            Agent = AgentKind.Copilot,
            Launcher = "copilot",
            Title = remote ? "Release notes for 0.2" : "Tidy the usage panel",
            Cwd = remote ? "/srv/agent-bridge" : @"C:\src\agent-bridge",
            Workspace = "agent-bridge",
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-3),
        });
        server = LocalServer.Start(LocalEndpoint.For(folder), HandleAsync);
    }

    // version: what the demo reports as its host version; the app's own, so it shows no skew.
    public static DemoHost Start(string version = "0.0.0-demo", bool remote = false) => new(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agent-bridge-demo-" + Guid.NewGuid().ToString("N")[..8])).FullName, version, remote);

    public int Count { get { lock (gate) { return sessions.Count; } } }

    // The self-test's progress gallery (ui.md §6.5): an answer or a reply to a scripted session
    // gets what the script returns, not the demo's own handling, so each receipt state (held on its
    // way, unconfirmed, busy, refused, failed) is drawn from a real request on a real channel. A
    // script that throws fails the request, as a host's error does.
    readonly Dictionary<string, Func<Task<object?>>> scripted = new(StringComparer.Ordinal);

    public void Script(string sessionId, Func<Task<object?>>? outcome)
    {
        lock (gate)
        {
            if (outcome is null) { scripted.Remove(sessionId); }
            else { scripted[sessionId] = outcome; }
        }
    }

    Func<Task<object?>>? ScriptFor(string sessionId) { lock (gate) { return scripted.GetValueOrDefault(sessionId); } }

    // A session added or changed, and one removed, as the host would send them (the gallery's).
    public Task PutAsync(Session s) => Change(s);
    public Task RemoveSessionAsync(string id) => RemoveAsync(id);

    // The protocol's example sessions, and three more: a question answered with one click, a
    // Copilot permission prompt, and a main agent with delegates, so every kind of card is on screen.
    public static IReadOnlyList<Session> Examples()
    {
        using var stream = typeof(DemoHost).Assembly.GetManifestResourceStream("AgentBridge.App.Core.sessions-list.json")
            ?? throw new InvalidOperationException("the demo sessions are missing from the build");
        var list = JsonSerializer.Deserialize<SessionList>(stream, ProtocolJson.Options)!;
        var now = DateTimeOffset.UtcNow;
        var shifted = list.Sessions.Select((s, i) => s with { UpdatedAt = now.AddMinutes(-i * 3), StatusSince = now.AddMinutes(-i * 3 - 2) }).ToList();
        shifted.Add(new Session
        {
            Id = "demo-approval",
            Agent = AgentKind.Copilot,
            Title = "Run the app's tests",
            Repository = "danswett/agent-bridge",
            Branch = "p3/winui-app",
            Model = "claude-opus-5.5",
            Status = SessionStatus.Waiting,
            StatusSince = now.AddSeconds(-40),
            UpdatedAt = now.AddSeconds(-40),
            Activity = new Activity { Response = "I'll run the suite before pushing.", TurnCount = 9 },
            Question = new Question
            {
                Id = "perm-1#demo",
                Kind = QuestionKind.Approval,
                // As CopilotPermission writes it: the ask, the agent's intention, then the command.
                Message = "Allow **shell**?\n\nRun the tests before pushing.\n\n```\ndotnet test AgentBridge.slnx -c Release\n```",
                AskedAt = now.AddSeconds(-40),
                Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = [new Option("approve", "Approve once"), new Option("deny", "Deny")] }],
            },
        });
        shifted.Add(new Session
        {
            Id = "demo-choice",
            Agent = AgentKind.Copilot,
            Title = "Pick the session store",
            Status = SessionStatus.Waiting,
            StatusSince = now.AddMinutes(-1),
            UpdatedAt = now.AddMinutes(-1),
            Activity = new Activity { Response = "Two options fit.", TurnCount = 2 },
            Question = new Question
            {
                Id = "toolu_demo_choice",
                Kind = QuestionKind.Form,
                Message = "Which database should the session store use?",
                AskedAt = now.AddMinutes(-1),
                Fields = [new Field { Name = "answer", Type = FieldType.Choice, AllowOther = true, Options = [new Option("pg", "PostgreSQL (Recommended)"), new Option("sqlite", "SQLite")] }],
            },
        });
        shifted.Add(Delegating(now));
        return shifted;
    }

    // A main agent that started two background delegates and went idle (pwa/js/demo.js,
    // demoDelegating): one running with a nested task under it and a finished one, one running a
    // command, and a task that failed. The top-level response and reasoning are the delegates'
    // (source names them); the main agent's own are in own. For the strip, the Delegates tab and
    // the inspector (ui.md §6.3), in the self-test too.
    public static Session Delegating(DateTimeOffset now) => new()
    {
        Id = "demo-delegates",
        Agent = AgentKind.Copilot,
        Title = "Fix RestartAfterResume",
        Repository = "danswett/agent-bridge",
        Branch = "fix/restart-after-resume",
        Model = "claude-opus-5.5",
        Status = SessionStatus.Idle,
        StatusSince = now.AddMinutes(-17),
        StartedAt = now.AddMinutes(-20),
        UpdatedAt = now.AddSeconds(-30),
        Activity = new Activity
        {
            Response = "Bisecting over the last 10 commits: 4 left to try.",
            Reasoning = "Check CI before saying so.",
            UserMessage = "Run the suite and check the witness logic, both in the background.",
            TurnCount = 1,
            Background = Background.Of(2, 1),
            Source = new ActivitySource { Response = "demo-agent-suite", Reasoning = "demo-agent-witness" },
            Own = new OwnActivity { Response = "Started 2 background agents: one runs the full suite, one reads the witness logic. I'll report back when both are done.", Reasoning = "Two independent jobs: delegate both.", At = now.AddMinutes(-17) },
        },
        Subagents =
        [
            new Subagent { Id = "demo-agent-witness", Kind = SubagentKind.Background, Name = "Read witness shell logic", Task = "Read the witness's shell logic and check it follows the latest reply of a turn.", Model = "gpt-5.6-luna", Status = SubagentStatus.Working, StartedAt = now.AddMinutes(-17), Line = "Check CI before saying so.", CurrentTool = new ToolCall { Id = "demo-t1", Name = "powershell", Summary = "Watch the checks", Status = ToolStatus.Running, StartedAt = now.AddSeconds(-40) } },
            new Subagent { Id = "demo-agent-suite", Kind = SubagentKind.Background, Name = "Run full test suite", Task = "Run the full test suite on HEAD and report any failure with its first error line.", Model = "claude-haiku-5.5", Status = SubagentStatus.Working, StartedAt = now.AddMinutes(-16.9), Line = "Bisecting over the last 10 commits: 4 left to try.", Shells = 1 },
            new Subagent { Id = "demo-agent-grep", Parent = "demo-agent-witness", Kind = SubagentKind.Task, Name = "Find the timer reset", Model = "gpt-5.6-luna", Status = SubagentStatus.Working, StartedAt = now.AddMinutes(-2), Line = "Two candidates in RestartWitness.cs." },
            new Subagent { Id = "demo-agent-review", Parent = "demo-agent-witness", Kind = SubagentKind.Task, Name = "Review the witness change", Model = "gpt-5.6-luna", Status = SubagentStatus.Ended, Outcome = SubagentOutcome.Done, StartedAt = now.AddMinutes(-12), EndedAt = now.AddMinutes(-11), Line = "No regressions: the flag now follows the latest reply." },
            new Subagent { Id = "demo-agent-lint", Kind = SubagentKind.Task, Name = "Lint the PWA", Model = "claude-haiku-5.5", Status = SubagentStatus.Ended, Outcome = SubagentOutcome.Failed, StartedAt = now.AddMinutes(-15), EndedAt = now.AddMinutes(-14), Line = "eslint could not start: no config found." },
            // Five that reported back, minutes apart so each has a line of its own: with the three
            // above, the conversation's delegate lines are a long run, which fold into one row (#1063).
            Reported(now, "demo-agent-changelog", "Tidy the changelog", -19.8, -19.2, "Two entries merged, one reworded."),
            Reported(now, "demo-agent-shots", "Update the screenshots", -18.7, -18.2, "Eleven screenshots retaken in both themes."),
            Reported(now, "demo-agent-logging", "Audit the logging", -13, -12.5, "No secrets in the logs; two noisy lines trimmed."),
            Reported(now, "demo-agent-docs", "Format the docs", -10, -9.2, "Tables realigned in four files."),
            Reported(now, "demo-agent-settings", "Read the settings code", -7, -6.4, "Nothing writes a setting twice."),
        ],
    };

    static Subagent Reported(DateTimeOffset now, string id, string name, double started, double ended, string line) => new()
    {
        Id = id,
        Kind = SubagentKind.Task,
        Name = name,
        Model = "claude-haiku-5.5",
        Status = SubagentStatus.Ended,
        Outcome = SubagentOutcome.Done,
        StartedAt = now.AddMinutes(started),
        EndedAt = now.AddMinutes(ended),
        Line = line,
    };

    // What the demo's delegates said in full, beyond the latest line a list carries.
    static readonly Dictionary<string, (string Response, string? Reasoning)> DelegateWords = new(StringComparer.Ordinal)
    {
        ["demo-agent-suite"] = ("The suite fails on HEAD: **RestartAfterResume** times out.\n\nBisecting over the last 10 commits: 4 left to try.", "A timing failure that only shows after a resume. Bisect first, then read the timer."),
        ["demo-agent-witness"] = ("The witness follows the first reply of a turn, not the latest. That is why it misses a resume.", "Check CI before saying so."),
    };

    // session.get { id, agent }: the delegate's entry with its own words, as a host since sub-agents
    // sends it, or missing when the session lists no such delegate.
    static object DelegateDetail(Session s, string agent, DateTimeOffset now)
    {
        if (s.Subagents?.FirstOrDefault(a => a.Id == agent) is not { } entry) { return new { missing = true }; }
        var (response, reasoning) = DelegateWords.GetValueOrDefault(agent, (entry.Line ?? "", null));
        var start = entry.StartedAt ?? now;
        return entry with
        {
            Response = response,
            Reasoning = reasoning,
            History =
            [
                new ToolCall { Id = $"{agent}-1", Name = "view", Summary = "src/AgentBridge.Host/RestartWitness.cs", Status = ToolStatus.Done, StartedAt = start.AddSeconds(5), EndedAt = start.AddSeconds(9) },
                new ToolCall { Id = $"{agent}-2", Name = "powershell", Summary = "dotnet test --filter RestartAfterResume", Status = ToolStatus.Failed, StartedAt = start.AddSeconds(20), EndedAt = start.AddSeconds(262) },
                .. entry.CurrentTool is { } current ? [current] : Array.Empty<ToolCall>(),
            ],
        };
    }

    Task<object?> HandleAsync(IProtocolChannel ch, string op, JsonElement data)
    {
        var now = DateTimeOffset.UtcNow;
        switch (op)
        {
            case Ops.Hello:
                {
                    var hello = new HelloResult(1, new MachineInfo(MachineId, MachineName, "windows", version, Dev: true, Sessions: Count), remote
                        ? [Capabilities.SessionsRead, Capabilities.SessionsAnswer, Capabilities.SessionsApprove, Capabilities.SessionsReply, Capabilities.SessionsAttach, Capabilities.SessionsStop, Capabilities.SessionsProgress, UsageProtocol.Capability, LaunchProtocol.Capability, AuthState.Capability]
                        : [Capabilities.SessionsRead, Capabilities.SessionsAnswer, Capabilities.SessionsApprove, Capabilities.SessionsReply, Capabilities.SessionsAttach, Capabilities.SessionsAttachDirect, Capabilities.SessionsStop, Capabilities.SessionsProgress, UsageProtocol.Capability, LaunchProtocol.Capability, HostOps.Capability, HostOps.AgentReadinessCapability, HostOps.AgentSetupCapability, HostOps.AgentInstallCapability]);
                    // Held for a test: the reply a recovery reads before it subscribes again.
                    if (HoldHello is { } hold) { HoldHello = null; return After(hold.Task, hello); }
                    return Done(hello);
                }
            case HostOps.Status or HostOps.Subscribe when !remote:
                return Done(Status());
            case UsageProtocol.Get or UsageProtocol.Subscribe:
                return Done(remote ? RemoteUsage(now) : Usage(now));
            case Ops.SessionsSubscribe:
                {
                    List<Session> snapshot;
                    long rev;
                    DiscoveryHeld? holds;
                    lock (gate)
                    {
                        snapshot = [.. sessions.Values];
                        rev = revision;
                        holds = held;
                        if (!subscribers.Contains(ch)) { subscribers.Add(ch); }
                    }
                    return Done(new ChannelReply(new SubscribeResult(rev, snapshot.Count), async c =>
                    {
                        foreach (var s in snapshot) { await c.SendEventAsync(Events.SessionUpsert, Wire(s)); }
                        await c.SendEventAsync(Events.SessionsSynced, new SessionsSynced(rev, holds));
                    }));
                }
            case Ops.SessionGet:
                {
                    var id = data.GetProperty("id").GetString()!;
                    lock (gate)
                    {
                        if (!sessions.TryGetValue(id, out var s)) { return Done(new { missing = true }); }
                        if (data.TryGetProperty("agent", out var agent) && agent.GetString() is { } a) { return Done(DelegateDetail(s, a, now)); }
                        return Done(s with { Activity = s.Activity with { History = s.Activity.History ?? History(now) } });
                    }
                }
            case Ops.SessionAnswer:
                {
                    var r = data.Deserialize<AnswerRequest>(ProtocolJson.Options)!;
                    if (ScriptFor(r.Id) is { } script) { return script(); }
                    // As a host that offers sessions.progress says how far it has got (#598, #600).
                    var progress = r.Progress == true ? ServedRequest.Current : null;
                    progress?.Progress(new DeliveryProgress(DeliveryProgress.Received));
                    Session? s;
                    lock (gate) { s = sessions.GetValueOrDefault(r.Id); }
                    if (s?.Question is not { } q || q.Id != r.Question) { return Done(new AnswerResult(AnswerOutcome.Stale, "the session is no longer asking that question")); }
                    if (Answers.Check(q, r) is { } problem) { return Done(new AnswerResult(AnswerOutcome.Refused, problem)); }
                    progress?.Progress(new DeliveryProgress(DeliveryProgress.Typed, s.Status == SessionStatus.Working));
                    _ = Change(s with { Question = null, Status = SessionStatus.Working, StatusSince = now, UpdatedAt = now });
                    return Done(new AnswerResult(AnswerOutcome.Delivered));
                }
            case Ops.SessionReply:
                {
                    var r = data.Deserialize<ReplyRequest>(ProtocolJson.Options)!;
                    if (ScriptFor(r.Id) is { } script) { return script(); }
                    var progress = r.Progress == true ? ServedRequest.Current : null;
                    progress?.Progress(new DeliveryProgress(DeliveryProgress.Received));
                    if (Replies.Check(r) is { } problem) { return Done(new ReplyResult(AnswerOutcome.Refused, problem)); }
                    Session? s;
                    lock (gate) { s = sessions.GetValueOrDefault(r.Id); }
                    if (s is null) { return Done(new ReplyResult(AnswerOutcome.Stale, "the session has ended")); }
                    progress?.Progress(new DeliveryProgress(DeliveryProgress.Typed, s.Status == SessionStatus.Working));
                    _ = Change(s with { Status = SessionStatus.Working, StatusSince = now, UpdatedAt = now, Activity = s.Activity with { UserMessage = r.Text } });
                    return Done(new ReplyResult(AnswerOutcome.Delivered));
                }
            case HostOps.AgentsRefresh when !remote:
                return Done(Agents());
            case HostOps.AgentsSetup when !remote:
                return SetUpAsync(data.Deserialize<AgentSetupRequest>(ProtocolJson.Options)!);
            // The demo installs nothing: Install CLI is offered only on the rows of its pictures.
            case HostOps.AgentsInstall when !remote:
                return Done(new AgentInstallResult(false, "Windows Package Manager is unavailable. Install or update Microsoft's App Installer, then try again. No CLI was installed."));
            case LaunchProtocol.Options:
                lock (gate) { return Done(LaunchChoices()); }
            case LaunchProtocol.Launch:
                return Done(Launch(data.Deserialize<LaunchRequest>(ProtocolJson.Options)!, now));
            case Ops.SessionStop:
                {
                    var r = data.Deserialize<StopRequest>(ProtocolJson.Options)!;
                    Session? s;
                    lock (gate) { s = sessions.GetValueOrDefault(r.Id); }
                    if (s is null) { return Done(new StopResult(StopOutcome.Stale, "the session has already ended")); }
                    var busy = s.Status != SessionStatus.Idle || s.Activity.Background is not null;
                    lock (gate)
                    {
                        if (busy && !(r.Confirm is { } c && armed.TryGetValue(r.Id, out var a) && a.Token == c && now - a.At < TimeSpan.FromSeconds(Stops.ConfirmSeconds)))
                        {
                            var token = Guid.NewGuid().ToString("N")[..16];
                            armed[r.Id] = (token, now);
                            return Done(new StopResult(StopOutcome.Confirm, $"this session is {(s.Status == SessionStatus.Idle ? "waiting for background work" : s.Status.ToString().ToLowerInvariant())}; the confirmation lapses in {Stops.ConfirmSeconds} s")
                            {
                                Confirm = token,
                                ConfirmSeconds = Stops.ConfirmSeconds,
                            });
                        }
                        armed.Remove(r.Id);
                    }
                    _ = RemoveAsync(r.Id);
                    return Done(new StopResult(StopOutcome.Ended));
                }
            case Ops.AttachmentStart:
                {
                    var a = data.Deserialize<AttachmentStart>(ProtocolJson.Options)!;
                    lock (gate) { attachments.TryAdd(a.Attachment, 0); return Done(new AttachmentState(attachments[a.Attachment], false)); }
                }
            case Ops.AttachmentChunk:
                {
                    var c = data.Deserialize<AttachmentChunk>(ProtocolJson.Options)!;
                    var bytes = Convert.FromBase64String(c.Data).Length;
                    lock (gate)
                    {
                        attachments[c.Attachment] = c.Offset + bytes;
                        return Done(new AttachmentState(c.Offset + bytes, bytes < AttachmentLimits.Chunk));
                    }
                }
            case Ops.AttachmentDiscard:
                return Done(new AttachmentDiscarded(true));
            case HostOps.SignIn:
                throw new InvalidOperationException("the demo has no relay");
            case HostOps.RelayAccount:
                throw new InvalidOperationException("this host is not signed in to the relay");
            case HostOps.Settings or HostOps.Restart or HostOps.Stop or HostOps.SignInCancel:
                throw new InvalidOperationException("the demo host cannot do that");
            case AuthState.Start when remote:
                return StartSignInAsync(ch);
            case AuthState.Cancel when remote:
                return CancelSignInAsync(data);
            default:
                return Done(null);
        }
    }

    static Task<object?> Done(object? result) => Task.FromResult(result);

    // A session as sent: without its history, and with its attention (#800) when it has one.
    JsonObject Wire(Session s)
    {
        var json = JsonSerializer.SerializeToNode(s.WithoutHistory(), ProtocolJson.Options)!.AsObject();
        lock (gate) { if (attention.TryGetValue(s.Id, out var a)) { json["attention"] = a.DeepClone(); } }
        return json;
    }

    // #800's scenario, on the other machine (Remote): three Copilot sessions whose agent needs
    // sign-in for one account, a Claude turn that ended on its quota, and a Codex session that has
    // stopped answering. For the self-test, and for trying the banner and sheet.
    public const string SignInAccount = "demo-user";
    public static readonly IReadOnlyList<string> SignInSessions = ["demo-signin-1", "demo-signin-2", "demo-signin-3"];

    public async Task NeedSignInAsync()
    {
        var now = DateTimeOffset.UtcNow;
        string[] titles = ["Coordinate the release", "Fix the relay reconnect", "Review the usage panel"];
        for (var i = 0; i < SignInSessions.Count; i++)
        {
            var since = now.AddMinutes(-4 - i);
            SetAttention(SignInSessions[i], new JsonObject { ["kind"] = AuthState.SignInNeeded, ["since"] = since.ToString("O"), ["agent"] = "copilot", ["provider"] = "github", ["account"] = SignInAccount, ["message"] = "Copilot needs you to sign in again (401).", ["signIn"] = "phone" });
            await Change(Example(SignInSessions[i], AgentKind.Copilot, titles[i], SessionStatus.Idle, since));
        }
        SetAttention("demo-error-quota", new JsonObject { ["kind"] = AuthState.Error, ["since"] = now.AddMinutes(-2).ToString("O"), ["agent"] = "claude", ["errorClass"] = "quota", ["message"] = "Claude stopped with an error.", ["retryAt"] = now.AddHours(2).ToString("O") });
        // This one and the next are driven by an agent: their rows still say what is wrong.
        await Change(Example("demo-error-quota", AgentKind.Claude, "Summarise the incident", SessionStatus.Idle, now.AddMinutes(-2)) with { Driver = DriverKind.Agent, DriverPrincipal = "homeassistant:demo" });
        SetAttention("demo-stalled", new JsonObject { ["kind"] = AuthState.NotResponding, ["since"] = now.AddMinutes(-1).ToString("O"), ["agent"] = "codex", ["lastEventAt"] = now.AddMinutes(-12).ToString("O") });
        await Change(Example("demo-stalled", AgentKind.Codex, "Migrate the settings store", SessionStatus.Working, now.AddMinutes(-15)) with { Driver = DriverKind.Agent, DriverPrincipal = "homeassistant:demo" }, finishes: false);
    }

    // The scenario's sessions gone, as they came.
    public async Task EndSignInScenarioAsync()
    {
        foreach (var id in SignInSessions.Append("demo-error-quota").Append("demo-stalled")) { lock (gate) { attention.Remove(id); } await RemoveAsync(id); }
        lock (gate) { flows.Clear(); }
    }

    // What the next signin.code says: GitHub's, unless a test sets a spoof.
    public string NextUserCode { get; set; } = "WDJB-MJHT";
    public string NextVerificationUri { get; set; } = "https://github.com/login/device";
    // How long the next code lives, and how long the next start's result takes (its code comes first).
    public TimeSpan NextCodeLife { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan NextStartDelay { get; set; }
    // The next start fails, with these words (which the app never shows).
    public string? NextStartFailure { get; set; }
    // How long the next cancel takes to finish.
    public TimeSpan NextCancelDelay { get; set; }
    // The next start is refused, with this reason and no flow.
    public string? NextStartRefusal { get; set; }
    // How long after a start its code is sent: late, it comes after a Cancel (the demo sends it anyway).
    public TimeSpan NextCodeDelay { get; set; } = TimeSpan.FromMilliseconds(100);
    // The ops a person's presses sent, in order: what the self-test reads to know nothing was sent unpressed.
    public List<string> SignInLog { get; } = [];

    void SetAttention(string id, JsonObject? a) { lock (gate) { if (a is null) { attention.Remove(id); } else { attention[id] = a; } } }

    static Session Example(string id, AgentKind agent, string title, SessionStatus status, DateTimeOffset since) => new()
    {
        Id = id,
        Agent = agent,
        Title = title,
        Repository = "danswett/agent-bridge",
        Branch = "main",
        Status = status,
        StatusSince = since,
        UpdatedAt = since,
        Activity = new Activity { TurnCount = 3 },
    };

    // signin.start: a flow that this channel is in, joined if one runs; its code a moment later,
    // to the channels in it alone, never in a session or a snapshot.
    async Task<object?> StartSignInAsync(IProtocolChannel ch)
    {
        var delay = NextStartDelay;
        NextStartDelay = TimeSpan.Zero;
        if (NextStartFailure is { } failure) { NextStartFailure = null; throw new InvalidOperationException(failure); }
        if (NextStartRefusal is { } refusal)
        {
            NextStartRefusal = null;
            lock (gate) { SignInLog.Add(AuthState.Start); }
            if (delay > TimeSpan.Zero) { await Task.Delay(delay); }
            return new { outcome = "refused", reason = refusal };
        }
        var result = StartSignIn(ch);
        if (delay > TimeSpan.Zero) { await Task.Delay(delay); }
        return result;
    }

    object StartSignIn(IProtocolChannel ch)
    {
        string flow;
        bool busy;
        lock (gate)
        {
            SignInLog.Add(AuthState.Start);
            busy = flows.Count > 0;
            flow = busy ? flows.Keys.First() : $"flow-{++flowCount}";
            if (!flows.TryGetValue(flow, out var chans)) { flows[flow] = chans = []; }
            if (!chans.Contains(ch)) { chans.Add(ch); }
        }
        var after = NextCodeDelay;
        NextCodeDelay = TimeSpan.FromMilliseconds(100);
        _ = SendCodeAsync(ch, flow, after);
        return new { outcome = busy ? "busy" : "started", flow };
    }

    async Task SendCodeAsync(IProtocolChannel ch, string flow, TimeSpan after)
    {
        await Task.Delay(after);
        try { await ch.SendEventAsync(AuthState.CodeEvent, new { flow, agent = "copilot", userCode = NextUserCode, verificationUri = NextVerificationUri, expiresAt = DateTimeOffset.UtcNow.Add(NextCodeLife).ToString("O") }); }
        catch (IOException) { }
    }

    // A cancel slow to finish: its reply waits, and "cancelled" is logged once it has.
    async Task<object?> CancelSignInAsync(JsonElement data)
    {
        var result = CancelSignIn(data);
        var delay = NextCancelDelay;
        NextCancelDelay = TimeSpan.Zero;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
            lock (gate) { SignInLog.Add("cancelled"); }
        }
        return result;
    }

    object CancelSignIn(JsonElement data)
    {
        var flow = data.TryGetProperty("flow", out var f) ? f.GetString() : null;
        lock (gate)
        {
            SignInLog.Add(AuthState.Cancel);
            if (flow is not null) { flows.Remove(flow); }
        }
        return new { outcome = "cancelled" };
    }

    // The person entered the code at github.com (or didn't): signin.done to the flow's channels.
    // Signed in doesn't clear the sessions: under #800's clear gate the host only marks them pending
    // (signed in, waiting for the session to confirm) until the model answers (ConfirmSignInAsync).
    // Signed in with pendingFirst false sends signin.done before the host's pending, which
    // SignedInPendingAsync then sends: the order a slow host may give.
    public async Task FinishSignInAsync(string outcome, string? reason = null, bool pendingFirst = true)
    {
        List<(string Flow, List<IProtocolChannel> Channels)> done;
        lock (gate)
        {
            done = [.. flows.Select(p => (Flow: p.Key, Channels: p.Value))];
            flows.Clear();
        }
        IReadOnlyList<string> resumed = outcome == "signedIn" ? SignInSessions : [];
        if (outcome == "signedIn" && pendingFirst) { await MarkSignInAsync("pending"); }
        foreach (var (flow, chans) in done)
        {
            foreach (var ch in chans.Where(c => c.IsOpen))
            {
                try { await ch.SendEventAsync(AuthState.DoneEvent, new { flow, outcome, reason, resumed, @continue = Array.Empty<string>() }); }
                catch (IOException) { }
            }
        }
    }

    public Task SignedInPendingAsync() => MarkSignInAsync("pending");

    // The model answered after the sign-in: the clear gate passes and the host clears the sessions.
    public Task ConfirmSignInAsync() => ClearSignInAsync();

    // The next turn failed sign-in again: back to needing it, with a new since.
    public Task FailSignInAgainAsync() => MarkSignInAsync("failed", again: true);

    // A sign-in under way from another device (recovery signingIn): the banner offers Show code. Off,
    // the sessions just need sign-in again.
    public async Task SignInUnderWayAsync(bool on)
    {
        if (on) { await MarkSignInAsync("signingIn"); return; }
        foreach (var id in SignInSessions)
        {
            Session? s;
            lock (gate)
            {
                if (!attention.TryGetValue(id, out var a)) { continue; }
                a.Remove("recovery");
                s = sessions.GetValueOrDefault(id);
            }
            if (s is not null) { await Change(s with { UpdatedAt = DateTimeOffset.UtcNow }); }
        }
    }

    async Task MarkSignInAsync(string recovery, bool again = false)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in SignInSessions)
        {
            Session? s;
            lock (gate)
            {
                if (!attention.TryGetValue(id, out var a)) { continue; }
                a["recovery"] = recovery;
                if (again) { a["since"] = now.ToString("O"); }
                s = sessions.GetValueOrDefault(id);
            }
            if (s is not null) { await Change(s with { UpdatedAt = now }); }
        }
    }

    // The next hello's reply waits for this.
    public TaskCompletionSource? HoldHello { get; set; }

    static async Task<object?> After(Task wait, object value)
    {
        await wait;
        return value;
    }

    // The channels subscribed to its sessions, for a test to lose events on.
    public IReadOnlyList<IProtocolChannel> Subscribers { get { lock (gate) { return [.. subscribers]; } } }

    // A sign-in another device started: a press here joins it (busy).
    public void SignInElsewhere() { lock (gate) { flows[$"flow-{++flowCount}"] = []; } }

    // How the host says the sessions can be signed in: phone, machine, or null for no claim.
    public async Task SignInWayAsync(string? way)
    {
        foreach (var id in SignInSessions)
        {
            Session? s;
            lock (gate)
            {
                if (!attention.TryGetValue(id, out var a)) { continue; }
                if (way is null) { a.Remove("signIn"); } else { a["signIn"] = way; }
                s = sessions.GetValueOrDefault(id);
            }
            if (s is not null) { await Change(s with { UpdatedAt = DateTimeOffset.UtcNow }); }
        }
    }

    // A sign-in session renamed while it still needs sign-in: its banner's list is drawn again.
    public async Task RetitleAsync(string id, string title)
    {
        Session? s;
        lock (gate) { s = sessions.GetValueOrDefault(id); }
        if (s is not null) { await Change(s with { Title = title, UpdatedAt = DateTimeOffset.UtcNow }); }
    }

    // The sessions' attention cleared: a stored sign-in that still worked, or a person's once the
    // session confirmed.
    public async Task ClearSignInAsync()
    {
        foreach (var id in SignInSessions)
        {
            Session? s;
            lock (gate) { attention.Remove(id); s = sessions.GetValueOrDefault(id); }
            if (s is not null) { await Change(s with { UpdatedAt = DateTimeOffset.UtcNow }); }
        }
    }

    // What the demo offers to launch: three agents (Codex needs a first message), one approved
    // workspace that starts each session in a worktree of its own, and one ended session.
    LaunchOptions LaunchChoices() => new()
    {
        Agents =
        [
            new LaunchAgent { Id = "copilot", Label = "Copilot", Kind = AgentKind.Copilot, Models = ["gpt-6.1-sol", "claude-opus-5.5"], Efforts = ["low", "medium", "high"], Defaults = new LaunchTuning { Model = "gpt-6.1-sol" }, Attachments = true },
            new LaunchAgent { Id = "claude", Label = "Claude", Kind = AgentKind.Claude, Models = ["opus", "sonnet"] },
            new LaunchAgent { Id = "codex", Label = "Codex", Kind = AgentKind.Codex, Models = ["gpt-6.1-sol"], Efforts = ["low", "high"], NeedsPrompt = true },
        ],
        Workspaces = [new LaunchWorkspace { Label = "agent-bridge", Path = remote ? "/srv/agent-bridge" : @"C:\src\agent-bridge", Isolate = true }],
        Resumable = [.. resumable],
        Defaults = new LaunchDefaults { Agent = "copilot", Workspace = "agent-bridge", Permissions = LaunchProtocol.Ask },
    };

    // A launch as a cooperative host carries it out: the session appears at once, working on its
    // first message, and the same request again gets the same outcome.
    LaunchResult Launch(LaunchRequest r, DateTimeOffset now)
    {
        Session started;
        LaunchResult result;
        lock (gate)
        {
            if (launches.TryGetValue(r.Request, out var again)) { return again; }
            var choices = LaunchChoices();
            if (LaunchProtocol.Check(r) is { } problem) { return launches[r.Request] = new LaunchResult(LaunchOutcome.Refused, problem); }
            var agent = choices.Agents.FirstOrDefault(a => a.Id == (r.Agent ?? "copilot"));
            if (agent is null) { return launches[r.Request] = new LaunchResult(LaunchOutcome.Refused, $"{r.Agent} is not installed on this machine"); }
            var resumed = r.Resume is { } id ? resumable.FirstOrDefault(x => x.Id == id) : null;
            if (r.Resume is not null && resumed is null) { return launches[r.Request] = new LaunchResult(LaunchOutcome.Refused, "that session is not one this machine can resume"); }
            if (resumed is not null) { resumable.Remove(resumed); }
            var prompt = LaunchProtocol.Flatten(r.Prompt);
            var sessionId = resumed?.Id ?? Guid.NewGuid().ToString();
            started = new Session
            {
                Id = sessionId,
                Agent = agent.Kind,
                Title = resumed?.Title ?? (prompt.Length > 0 ? Limits.KeepStart(prompt, 60) : null),
                Repository = "danswett/agent-bridge",
                Branch = resumed is null ? $"wt-{sessionId[..8]}" : "main",
                Model = string.IsNullOrEmpty(r.Model) ? agent.Defaults?.Model : r.Model,
                Cwd = resumed?.Cwd ?? (remote ? $"/srv/wt/agent-bridge-{sessionId[..8]}" : $@"C:\src\wt\agent-bridge-{sessionId[..8]}"),
                Status = prompt.Length > 0 ? SessionStatus.Working : SessionStatus.Idle,
                StatusSince = now,
                UpdatedAt = now,
                Activity = new Activity { UserMessage = prompt.Length > 0 ? prompt : null, TurnCount = 1 },
            };
            result = launches[r.Request] = new LaunchResult(LaunchOutcome.Started)
            {
                Session = sessionId,
                Launched = new LaunchedWith
                {
                    Agent = agent.Id,
                    Workspace = resumed is null ? r.Workspace ?? "agent-bridge" : null,
                    Resume = resumed?.Id,
                    Cwd = started.Cwd,
                    Worktree = resumed is null ? true : null,
                    Model = started.Model,
                    Permissions = r.Permissions ?? LaunchProtocol.Ask,
                },
            };
        }
        _ = Change(started);
        return result;
    }

    // A session as a host would send it next, for the self-test: a schedule's or an agent's prompt
    // arriving (#489), which nothing in the demo sends on its own.
    public Task Upsert(Session next) => Change(next);

    // The pauses a change and a removal take, as a host's would; a test holds them to order a
    // removal against a turn finishing.
    internal Func<TimeSpan, Task> Pause { get; set; } = d => Task.Delay(d);

    // finishes: a turn started here ends a moment later, as an agent's would; not a session that has
    // stopped answering (#800). onlyIfPresent: the finishing update, which must not bring back a
    // session removed while it waited to be written (#625, Copilot 4485774643): the gallery's, removed
    // in its own 400 ms pause, came back and was sent again as an upsert.
    async Task Change(Session next, bool finishes = true, bool onlyIfPresent = false)
    {
        await Pause(TimeSpan.FromMilliseconds(400));
        IProtocolChannel[] to;
        lock (gate)
        {
            if (onlyIfPresent && !sessions.ContainsKey(next.Id)) { return; }
            sessions[next.Id] = next;
            revision++;
            to = [.. subscribers.Where(s => s.IsOpen)];
        }
        foreach (var ch in to) { try { await ch.SendEventAsync(Events.SessionUpsert, Wire(next)); } catch (IOException) { } }
        // The agent takes the answer or reply and finishes its turn a little later.
        if (finishes && next.Status == SessionStatus.Working)
        {
            await Pause(TimeSpan.FromMilliseconds(3000));
            // Removed meanwhile (the self-test's gallery cleans up after itself): it stays gone.
            lock (gate) { if (!sessions.ContainsKey(next.Id)) { return; } }
            var done = DateTimeOffset.UtcNow;
            // The main agent wrote last: the top-level text is its own again. A few short blocks, so a
            // conversation's pictures show the space between them (#765).
            await Change(next with { Status = SessionStatus.Idle, StatusSince = done, UpdatedAt = done, Activity = next.Activity with { Response = "Done: that's taken care of.\n\nThe branch is pushed.\n\n- the suite passes\n- nothing else is open", TurnCount = (next.Activity.TurnCount ?? 0) + 1, Reasoning = null, Source = null, Own = null } }, onlyIfPresent: true);
        }
    }

    async Task RemoveAsync(string id)
    {
        await Pause(TimeSpan.FromMilliseconds(400));
        IProtocolChannel[] to;
        lock (gate)
        {
            sessions.Remove(id);
            revision++;
            to = [.. subscribers.Where(s => s.IsOpen)];
        }
        foreach (var ch in to) { try { await ch.SendEventAsync(Events.SessionRemove, new SessionRemoved(id)); } catch (IOException) { } }
    }

    // What the demo says holds its discovery, if anything: the self-test's, for the Discovery held
    // banner (#293). The demo has no discovery to hold; nothing else changes with it.
    DiscoveryHeld? held;

    public async Task HoldAsync(DiscoveryHeld? next)
    {
        IProtocolChannel[] to;
        lock (gate)
        {
            held = next;
            to = [.. subscribers.Where(s => s.IsOpen)];
        }
        foreach (var ch in to) { try { await ch.SendEventAsync(Events.DiscoveryChanged, new DiscoveryChanged(next)); } catch (IOException) { } }
    }

    static IReadOnlyList<ToolCall> History(DateTimeOffset now) =>
    [
        new ToolCall { Id = "t1", Name = "view", Summary = "src/AgentBridge.Host/Program.cs", Status = ToolStatus.Done, StartedAt = now.AddMinutes(-9), EndedAt = now.AddMinutes(-9) },
        new ToolCall { Id = "t2", Name = "powershell", Summary = "dotnet build", Status = ToolStatus.Failed, StartedAt = now.AddMinutes(-7), EndedAt = now.AddMinutes(-6) },
        new ToolCall { Id = "t3", Name = "edit", Summary = "src/AgentBridge.Host/HostControl.cs", Status = ToolStatus.Done, StartedAt = now.AddMinutes(-4), EndedAt = now.AddMinutes(-4) },
    ];

    HostStatus Status() => new()
    {
        Version = version,
        Dev = true,
        Profile = "demo",
        Folder = Folder,
        ProcessId = Environment.ProcessId,
        StartedAt = DateTimeOffset.UtcNow,
        Machine = new MachineInfo("demo", "Demo machine", "windows", version),
        Relay = new RelayStatus(RelayState.SignedOut),
        SignIn = new SignInStatus(SignInState.Idle),
        Settings = new HostSettingsView(true, "Demo machine"),
        Agents = Agents(),
        AgentInstallation = CantActOn ? new AgentInstallProgress("claude", false, FailedInstall) : null,
    };

    // Settings › Agents on this machine (#678): one agent per hook state, so the page shows each
    // verb: Copilot CLI to set up, Claude Code to repair, and Codex set up but not tested yet, as a
    // healthy agent reads on a real host (Unknown, its setup completed), with none. Each carries its
    // `hookSetup` as a current host sends it. The demo writes no files, but its readiness moves as a
    // real host's does after a setup (#705 review): one that completes leaves the setup completed
    // with intake on, so the row offers no Set up beside "Hooks registered…"; a removal leaves
    // intake on without it. Claude Code's setup didn't finish, the Failed a host can repair; its
    // repair fails.
    readonly Dictionary<string, (ReadinessCheck Check, HookSetupState Setup)> hooks = new(StringComparer.Ordinal)
    {
        ["copilot"] = (new(ReadinessState.NeedsAction, "Hook intake is off for this profile. Explicit setup or migration is required."), HookSetupState.NotSetUp),
        ["claude"] = (new(ReadinessState.Failed, "Hook setup for this profile did not complete; its registrations may be partial. Repair setup."), HookSetupState.Incomplete),
        ["codex"] = (new(ReadinessState.Unknown, SetUpHooks), HookSetupState.Completed),
    };
    const string SetUpHooks = "An owned setup record exists and setup completed; registration and activation still need verification.";
    const string RemovedHooks = "Intake is enabled; this agent's registration and ownership have not been verified.";
    bool cantActOn;

    // For the self-test's pictures of the rows the app can't act on (Creative's ruling on #705): Copilot
    // CLI not installed beside agent-ha-bridge (Read-only, no Install), Claude Code's installation
    // failed and its hook ownership unreadable, and Codex on a host from before `hookSetup` (Can't
    // check). The host's raw words are in them, as a real host sends them, for the rows to word.
    public void ShowAgentsTheAppCantActOn(bool on) { lock (gate) { cantActOn = on; } }
    bool CantActOn { get { lock (gate) { return cantActOn; } } }
    const string FailedInstall = "Windows Package Manager exited with code 0x8A15000F. Check network access, package policy and required runtime approvals, then check agents again. Partial installations were not removed.";

    AgentReadinessReport Agents()
    {
        static AgentReadiness Agent(string id, string label, string version, (ReadinessCheck Check, HookSetupState Setup) hooks, bool installed = true) => new(id, label, $"C:\\Users\\demo\\.{id}",
            installed ? $"C:\\Program Files\\{label}\\{id}.exe" : null, installed ? version : null,
            installed ? new(ReadinessState.Working, "Executable found using the launch configuration.") : new(ReadinessState.Missing, "Not installed. Install this CLI, then check again."),
            installed ? new(ReadinessState.Working, $"Version {version}") : new(ReadinessState.Unknown, "Version not checked."),
            hooks.Check,
            new(ReadinessState.Unknown, "CLI sign-in and entitlement have not been verified."),
            new(ReadinessState.Unknown, "Folder trust, hook activation and a new session have not been verified."),
            HookSetup: hooks.Setup);
        lock (gate)
        {
            return new(DateTimeOffset.UtcNow, cantActOn
            ? [
                Agent("copilot", "Copilot CLI", "1.0.105", (new(ReadinessState.Blocked, "Hook setup is unavailable while this host is read-only: agent-ha-bridge is running for this user; answer at the machine"), HookSetupState.Unknown), installed: false),
                Agent("claude", "Claude Code", "2.1.4", (new(ReadinessState.Failed, "Hook ownership could not be checked: Access to the path 'C:\\Users\\demo\\.claude\\agent-bridge-owner.json' is denied."), HookSetupState.Unknown), installed: false),
                Agent("codex", "Codex", "0.48.0", (new(ReadinessState.Unknown, "An owned setup record exists; registration and activation still need verification."), HookSetupState.Unknown)),
            ]
            : [
                Agent("copilot", "Copilot CLI", "1.0.105", hooks["copilot"]),
                Agent("claude", "Claude Code", "2.1.4", hooks["claude"]),
                Agent("codex", "Codex", "0.48.0", hooks["codex"]),
            ]);
        }
    }

    // As long as a real setup takes to write, so its row says "Setting up…" before its result.
    async Task<object?> SetUpAsync(AgentSetupRequest r)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        var result = SetUp(r);
        if (result.Completed)
        {
            lock (gate) { hooks[r.Agent] = r.Action == "remove" ? (new(ReadinessState.Unknown, RemovedHooks), HookSetupState.NotSetUp) : (new(ReadinessState.Unknown, SetUpHooks), HookSetupState.Completed); }
        }
        return result;
    }
    static AgentSetupResult SetUp(AgentSetupRequest r) => r switch
    {
        { Action: "remove" } => new(true, "Removed this installation's hooks. Existing sessions keep theirs until they end.", true),
        { Agent: "claude" } => new(false, "Repair incomplete: the demo cannot write Claude Code's settings.", false),
        _ => new(true, "Hooks registered. Restart the host, then start a new session to check them.", true),
    };

    // Another machine's sessions: a Claude question and a Codex turn under way, so the demo
    // shows cards from two machines.
    public static IReadOnlyList<Session> RemoteExamples()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new Session
            {
                Id = "demo-remote-claude",
                Agent = AgentKind.Claude,
                Title = "Tune the relay's rate limits",
                Repository = "danswett/agent-bridge",
                Branch = "relay/limits",
                Model = "claude-opus-5.5",
                Status = SessionStatus.Waiting,
                StatusSince = now.AddSeconds(-90),
                UpdatedAt = now.AddSeconds(-90),
                Activity = new Activity { Response = "The limit can go per device or per account.", TurnCount = 4 },
                Question = new Question
                {
                    Id = "toolu_demo_remote",
                    Kind = QuestionKind.Form,
                    Message = "Should the rate limit apply per device or per account?",
                    AskedAt = now.AddSeconds(-90),
                    Fields = [new Field { Name = "answer", Type = FieldType.Choice, AllowOther = true, Options = [new Option("device", "Per device (Recommended)"), new Option("account", "Per account")] }],
                },
            },
            new Session
            {
                Id = "demo-remote-codex",
                Agent = AgentKind.Codex,
                Title = "Nightly build",
                Repository = "danswett/agent-bridge",
                Branch = "main",
                Model = "gpt-5.6-sol",
                Status = SessionStatus.Working,
                StatusSince = now.AddMinutes(-6),
                UpdatedAt = now.AddMinutes(-2),
                // Launched by an agent: its card shows the driven diamond (ui.md §2.2 rule 7).
                Driver = DriverKind.Agent,
                DriverPrincipal = "homeassistant:demo",
                Activity = new Activity { Response = "Building the release packages.", TurnCount = 12, CurrentTool = new ToolCall { Id = "t9", Name = "shell", Summary = "pwsh build/pack-app.ps1", Status = ToolStatus.Running, StartedAt = now.AddMinutes(-2) } },
            },
        ];
    }

    static UsageReport RemoteUsage(DateTimeOffset now) => new(
    [
        new AgentUsage
        {
            Agent = AgentKind.Codex, Label = "Codex", Plan = "Pro", AccountKey = "c0de51a7", Source = UsageSource.Transcript, MeasuredAt = now.AddMinutes(-20),
            Windows = [new UsageWindow { Key = "week", Label = "Week", Percent = 41, StartsAt = now.AddDays(-3), ResetsAt = now.AddDays(4) }],
        },
    ]);

    static UsageReport Usage(DateTimeOffset now) => new(
    [
        new AgentUsage
        {
            Agent = AgentKind.Copilot, Label = "Copilot", Account = "octocat", Plan = "Pro+", Source = UsageSource.Api, MeasuredAt = now.AddMinutes(-2),
            Windows = [new UsageWindow { Key = "premium", Label = "Premium requests", Percent = 78, Used = 1170, Limit = 1500, Unit = "requests", StartsAt = now.AddDays(-20), ResetsAt = now.AddDays(10) }],
        },
        new AgentUsage
        {
            Agent = AgentKind.Claude, Label = "Claude", Plan = "Max", AccountKey = "7f3a91c2", Source = UsageSource.Api, MeasuredAt = now.AddMinutes(-1),
            Windows =
            [
                new UsageWindow { Key = "session", Label = "Session (5 h)", Percent = 34, StartsAt = now.AddHours(-2), ResetsAt = now.AddHours(3) },
                new UsageWindow { Key = "week", Label = "Week", Percent = 92, StartsAt = now.AddDays(-5), ResetsAt = now.AddDays(2) },
            ],
        },
    ]);

    public async ValueTask DisposeAsync()
    {
        await server.DisposeAsync();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { }
    }
}

// The demo's other machine: a second pretend host on a pipe of its own, which the app reaches
// as it would one through the relay. Never the relay itself.
public sealed class DemoFleetLink(DemoHost remote) : IFleetLink
{
    public FleetState State { get; private set; } = FleetState.Connecting;
    public string? Detail => null;
    public IReadOnlyList<FleetHost> Hosts => State == FleetState.Connected ? [new FleetHost(remote.MachineId, remote.MachineName)] : [];
    public event Action? Changed;

    // A machine the account has had that is offline, as the relay's roster lists one; Forget
    // takes it off, as the relay would.
    volatile IReadOnlyList<KnownMachine> offline = [new KnownMachine("d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f", "Travel laptop", "host", DateTimeOffset.UtcNow.AddDays(-3))];
    public IReadOnlyList<KnownMachine>? Offline => State == FleetState.Connected ? offline : null;

    public Task<string?> ForgetAsync(string machineId, CancellationToken ct)
    {
        offline = [.. offline.Where(m => m.Id != machineId)];
        Changed?.Invoke();
        return Task.FromResult<string?>(null);
    }

    public void Start()
    {
        State = FleetState.Connected;
        Changed?.Invoke();
    }

    public async Task<IClientChannel> OpenAsync(string hostId, CancellationToken ct) =>
        hostId == remote.MachineId ? await LocalClient.ConnectAsync(remote.Endpoint, TimeSpan.FromSeconds(2), ct) : throw new HostNotRunningException("the machine is offline");

    public ValueTask DisposeAsync() => remote.DisposeAsync();
}