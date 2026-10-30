using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.App.Core;

// A pretend host inside the app (--demo): the protocol's example sessions served on a pipe of
// its own, answering every op as a cooperative host would. For trying the app without a host
// or agents, for screenshots, and for CI's check that the published app starts and draws
// cards. It never touches the real host, its profile or any session.
public sealed class DemoHost : IAsyncDisposable
{
    readonly LocalServer server;
    readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    readonly List<IProtocolChannel> subscribers = [];
    readonly object gate = new();
    readonly Dictionary<string, (string Token, DateTimeOffset At)> armed = [];
    readonly Dictionary<string, long> attachments = [];
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
        server = LocalServer.Start(LocalEndpoint.For(folder), HandleAsync);
    }

    // version: what the demo reports as its host version; the app's own, so it shows no skew.
    public static DemoHost Start(string version = "0.0.0-demo", bool remote = false) => new(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "agent-bridge-demo-" + Guid.NewGuid().ToString("N")[..8])).FullName, version, remote);

    public int Count { get { lock (gate) { return sessions.Count; } } }

    // The protocol's example sessions, and two more: a question answered with one click and a
    // Copilot permission prompt, so every kind of card is on screen.
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
                Message = "Run a shell command:\n\n```powershell\ndotnet test AgentBridge.slnx -c Release\n```\n\nIntention: run the tests.",
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
        return shifted;
    }

    Task<object?> HandleAsync(IProtocolChannel ch, string op, JsonElement data)
    {
        var now = DateTimeOffset.UtcNow;
        switch (op)
        {
            case Ops.Hello:
                return Done(new HelloResult(1, new MachineInfo(MachineId, MachineName, "windows", version, Dev: true, Sessions: Count), remote
                    ? [Capabilities.SessionsRead, Capabilities.SessionsAnswer, Capabilities.SessionsApprove, Capabilities.SessionsReply, Capabilities.SessionsAttach, Capabilities.SessionsStop, UsageProtocol.Capability]
                    : [Capabilities.SessionsRead, Capabilities.SessionsAnswer, Capabilities.SessionsApprove, Capabilities.SessionsReply, Capabilities.SessionsAttach, Capabilities.SessionsAttachDirect, Capabilities.SessionsStop, UsageProtocol.Capability, HostOps.Capability]));
            case HostOps.Status or HostOps.Subscribe when !remote:
                return Done(Status());
            case UsageProtocol.Get or UsageProtocol.Subscribe:
                return Done(remote ? RemoteUsage(now) : Usage(now));
            case Ops.SessionsSubscribe:
                {
                    List<Session> snapshot;
                    long rev;
                    lock (gate)
                    {
                        snapshot = [.. sessions.Values];
                        rev = revision;
                        if (!subscribers.Contains(ch)) { subscribers.Add(ch); }
                    }
                    return Done(new ChannelReply(new SubscribeResult(rev, snapshot.Count), async c =>
                    {
                        foreach (var s in snapshot) { await c.SendEventAsync(Events.SessionUpsert, s.WithoutHistory()); }
                        await c.SendEventAsync(Events.SessionsSynced, new SessionsSynced(rev));
                    }));
                }
            case Ops.SessionGet:
                {
                    var id = data.GetProperty("id").GetString()!;
                    lock (gate)
                    {
                        if (!sessions.TryGetValue(id, out var s)) { return Done(new { missing = true }); }
                        return Done(s with { Activity = s.Activity with { History = History(now) } });
                    }
                }
            case Ops.SessionAnswer:
                {
                    var r = data.Deserialize<AnswerRequest>(ProtocolJson.Options)!;
                    Session? s;
                    lock (gate) { s = sessions.GetValueOrDefault(r.Id); }
                    if (s?.Question is not { } q || q.Id != r.Question) { return Done(new AnswerResult(AnswerOutcome.Stale, "the session is no longer asking that question")); }
                    if (Answers.Check(q, r) is { } problem) { return Done(new AnswerResult(AnswerOutcome.Refused, problem)); }
                    _ = Change(s with { Question = null, Status = SessionStatus.Working, StatusSince = now, UpdatedAt = now });
                    return Done(new AnswerResult(AnswerOutcome.Delivered));
                }
            case Ops.SessionReply:
                {
                    var r = data.Deserialize<ReplyRequest>(ProtocolJson.Options)!;
                    if (Replies.Check(r) is { } problem) { return Done(new ReplyResult(AnswerOutcome.Refused, problem)); }
                    Session? s;
                    lock (gate) { s = sessions.GetValueOrDefault(r.Id); }
                    if (s is null) { return Done(new ReplyResult(AnswerOutcome.Stale, "the session has ended")); }
                    _ = Change(s with { Status = SessionStatus.Working, StatusSince = now, UpdatedAt = now, Activity = s.Activity with { UserMessage = r.Text } });
                    return Done(new ReplyResult(AnswerOutcome.Delivered));
                }
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
            default:
                return Done(null);
        }
    }

    static Task<object?> Done(object? result) => Task.FromResult(result);

    async Task Change(Session next)
    {
        await Task.Delay(400);
        IProtocolChannel[] to;
        lock (gate)
        {
            sessions[next.Id] = next;
            revision++;
            to = [.. subscribers.Where(s => s.IsOpen)];
        }
        foreach (var ch in to) { try { await ch.SendEventAsync(Events.SessionUpsert, next.WithoutHistory()); } catch (IOException) { } }
        // The agent takes the answer or reply and finishes its turn a little later.
        if (next.Status == SessionStatus.Working)
        {
            await Task.Delay(3000);
            var done = DateTimeOffset.UtcNow;
            await Change(next with { Status = SessionStatus.Idle, StatusSince = done, UpdatedAt = done, Activity = next.Activity with { Response = "Done: that's taken care of.", TurnCount = (next.Activity.TurnCount ?? 0) + 1 } });
        }
    }

    async Task RemoveAsync(string id)
    {
        await Task.Delay(400);
        IProtocolChannel[] to;
        lock (gate)
        {
            sessions.Remove(id);
            revision++;
            to = [.. subscribers.Where(s => s.IsOpen)];
        }
        foreach (var ch in to) { try { await ch.SendEventAsync(Events.SessionRemove, new SessionRemoved(id)); } catch (IOException) { } }
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

    public void Start()
    {
        State = FleetState.Connected;
        Changed?.Invoke();
    }

    public async Task<IClientChannel> OpenAsync(string hostId, CancellationToken ct) =>
        hostId == remote.MachineId ? await LocalClient.ConnectAsync(remote.Endpoint, TimeSpan.FromSeconds(2), ct) : throw new HostNotRunningException("the machine is offline");

    public ValueTask DisposeAsync() => remote.DisposeAsync();
}