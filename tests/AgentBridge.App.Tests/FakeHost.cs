using System.Collections.Concurrent;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

// A host for the app's tests: a real LocalServer on a temporary endpoint, answering each op
// as the test says, recording what it was sent, and able to push events. The real host's
// behaviour over the pipe is tested in AgentBridge.Host.Tests; this is what the app sees.
sealed class FakeHost : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly string folder = Path.Combine(Path.GetTempPath(), "aba-" + Guid.NewGuid().ToString("N")[..8]);
    readonly ConcurrentDictionary<string, Func<JsonElement, object?>> ops = new();
    LocalServer? server;

    public ConcurrentQueue<(string Op, JsonElement Data)> Received { get; } = new();
    public ConcurrentBag<IProtocolChannel> Channels { get; } = [];
    public List<string> Capabilities { get; } = [AgentBridge.Protocol.Capabilities.SessionsRead, AgentBridge.Protocol.Capabilities.SessionsAnswer, AgentBridge.Protocol.Capabilities.SessionsReply, AgentBridge.Protocol.Capabilities.SessionsAttach, AgentBridge.Protocol.Capabilities.SessionsStop, UsageProtocol.Capability, HostOps.Capability];
    public List<Session> Sessions { get; } = [];
    public HostStatus Status { get; set; } = StatusOf();

    public FakeHost()
    {
        Directory.CreateDirectory(folder);
        On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("host-id", "Test PC", "windows", "0.1.0"), [.. Capabilities]));
        On(HostOps.Subscribe, _ => Status);
        On(HostOps.Status, _ => Status);
        On(UsageProtocol.Subscribe, _ => UsageReport.Empty);
    }

    public string Folder => folder;
    public LocalEndpoint Endpoint => LocalEndpoint.For(folder);

    public static HostStatus StatusOf(RelayState relay = RelayState.SignedOut, string? agentHaBridge = null) => new()
    {
        Version = "0.1.0",
        Profile = "test",
        Folder = "x",
        ProcessId = 4242,
        StartedAt = DateTimeOffset.UtcNow,
        Machine = new MachineInfo("host-id", "Test PC", "windows", "0.1.0"),
        Relay = new RelayStatus(relay),
        SignIn = new SignInStatus(SignInState.Idle),
        Settings = new HostSettingsView(true),
        AgentHaBridge = agentHaBridge,
    };

    public void On(string op, Func<JsonElement, object?> answer) => ops[op] = answer;

    public FakeHost Start()
    {
        server = LocalServer.Start(Endpoint, HandleAsync);
        return this;
    }

    public async Task StopAsync()
    {
        if (server is { } s) { await s.DisposeAsync(); }
        server = null;
    }

    Task<object?> HandleAsync(IProtocolChannel ch, string op, JsonElement data)
    {
        Received.Enqueue((op, data.ValueKind == JsonValueKind.Undefined ? default : data.Clone()));
        Channels.Add(ch);
        if (op == Ops.SessionsSubscribe)
        {
            var snapshot = Sessions.ToList();
            return Task.FromResult<object?>(new ChannelReply(new SubscribeResult(1, snapshot.Count), async c =>
            {
                foreach (var s in snapshot) { await c.SendEventAsync(Events.SessionUpsert, s); }
                await c.SendEventAsync(Events.SessionsSynced, new SessionsSynced(1));
            }));
        }
        return Task.FromResult(ops.TryGetValue(op, out var answer) ? answer(data) : null);
    }

    public async Task SendAsync(string name, object data)
    {
        foreach (var ch in Channels.Distinct().Where(c => c.IsOpen)) { await ch.SendEventAsync(name, data); }
    }

    public IReadOnlyList<JsonElement> Sent(string op) => [.. Received.Where(r => r.Op == op).Select(r => r.Data)];

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    public HostConnection Connect(IUiDispatcher? ui = null)
    {
        var c = new HostConnection(ct => LocalClient.ConnectAsync(Endpoint, TimeSpan.FromSeconds(1), ct), ui ?? new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        c.Start();
        return c;
    }
}

// Task Scheduler and processes as the host manager sees them, so no test registers or
// starts anything on the machine it runs on.
sealed class FakeTasks : ILogonTasks
{
    public Dictionary<string, string> Registered { get; } = [];
    public List<string> Ran { get; } = [];
    public Action? OnRun;
    public bool Exists(string name) => Registered.ContainsKey(name);
    public void Register(string name, string xml) => Registered[name] = xml;
    public void Delete(string name) => Registered.Remove(name);
    public void Run(string name) { Ran.Add(name); OnRun?.Invoke(); }
}

sealed class FakeProcesses : IProcesses
{
    public List<(string Path, IReadOnlyList<string> Args)> Started { get; } = [];
    public List<int> Killed { get; } = [];
    public Action? OnStart;
    public int Start(string path, IReadOnlyList<string> arguments) { Started.Add((path, arguments)); OnStart?.Invoke(); return 99; }
    public bool IsRunning(int pid) => false;
    public void Kill(int pid) => Killed.Add(pid);
}

// The tests' UI thread: actions run one at a time, in order, as on a dispatcher.
sealed class LockedDispatcher : IUiDispatcher
{
    readonly object gate = new();
    public void Post(Action action) { lock (gate) { action(); } }
}

static class Wait
{
    public static async Task UntilAsync(Func<bool> done, string what, TimeSpan? patience = null)
    {
        var deadline = DateTime.UtcNow + (patience ?? FakeHost.Patience);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) { throw new TimeoutException($"timed out waiting for {what}"); }
            await Task.Delay(20);
        }
    }
}

static class Make
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);

    public static Session Session(string id = "s1", SessionStatus status = SessionStatus.Idle, Question? question = null, string? response = null,
        DateTimeOffset? updated = null, Background? background = null, string? title = "A session", int? turns = 1) => new()
    {
        Id = id,
        Agent = AgentKind.Copilot,
        Title = title,
        Status = question is not null && status == SessionStatus.Idle ? SessionStatus.Waiting : status,
        StatusSince = T0,
        UpdatedAt = updated ?? T0,
        Repository = "danswett/agent-bridge",
        Branch = "main",
        Model = "claude-opus-5.5",
        Cwd = @"C:\src\agent-bridge",
        ProcessId = 100,
        Activity = new Activity { Response = response, TurnCount = turns, Background = background },
        Question = question,
    };

    public static Question Choice(string id = "q1", bool other = false, params string[] options) => new()
    {
        Id = id,
        Kind = QuestionKind.Form,
        Message = "Which database?",
        AskedAt = T0,
        Fields = [new Field { Name = "answer", Type = FieldType.Choice, AllowOther = other ? true : null, Options = [.. (options.Length > 0 ? options : ["pg", "sqlite"]).Select(o => new Option(o, o == "pg" ? "PostgreSQL (Recommended)" : o))] }],
    };

    public static Question Approval(string id = "r1#d") => new()
    {
        Id = id,
        Kind = QuestionKind.Approval,
        Message = "Run `dotnet test`?",
        AskedAt = T0,
        Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = [new Option("approve", "Approve once"), new Option("deny", "Deny")] }],
    };

    public static Question Form(params Field[] fields) => new() { Id = "f1", Kind = QuestionKind.Form, Message = "A few things", AskedAt = T0, Fields = fields };

    public static Question Free(string id = "t1") => new() { Id = id, Kind = QuestionKind.Form, Message = "What next?", AskedAt = T0 };
}
