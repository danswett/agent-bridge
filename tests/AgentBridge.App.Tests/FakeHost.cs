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
    // Sent with sessions.synced.
    public DiscoveryHeld? Held { get; set; }
    // While set, a subscribe's snapshot is sent but its sessions.synced waits for it.
    public TaskCompletionSource? HoldSynced { get; set; }
    public HostStatus Status { get; set; } = StatusOf();

    public FakeHost()
    {
        Directory.CreateDirectory(folder);
        On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("host-id", "Test PC", "windows", "0.1.0"), [.. Capabilities]));
        On(HostOps.Subscribe, _ => Status);
        On(HostOps.Status, _ => Status);
        On(UsageProtocol.Subscribe, _ => UsageReport.Empty);
        On(UsageProtocol.Get, _ => UsageReport.Empty);
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
        // Taken before stopping it: a host started while this one stops (a failed update's
        // restart) must not be forgotten when this one has gone, and left running.
        if (Interlocked.Exchange(ref server, null) is { } s) { await s.DisposeAsync(); }
    }

    Task<object?> HandleAsync(IProtocolChannel ch, string op, JsonElement data)
    {
        Received.Enqueue((op, data.ValueKind == JsonValueKind.Undefined ? default : data.Clone()));
        Channels.Add(ch);
        if (op == Ops.SessionsSubscribe)
        {
            var snapshot = Sessions.ToList();
            var hold = HoldSynced;
            return Task.FromResult<object?>(new ChannelReply(new SubscribeResult(1, snapshot.Count), async c =>
            {
                foreach (var s in snapshot) { await c.SendEventAsync(Events.SessionUpsert, s); }
                if (hold is not null) { await hold.Task; }
                await c.SendEventAsync(Events.SessionsSynced, new SessionsSynced(1, Held));
            }));
        }
        return Task.FromResult(ops.TryGetValue(op, out var answer) ? answer(data) : null);
    }

    public async Task SendAsync(string name, object data)
    {
        foreach (var ch in Channels.Distinct().Where(c => c.IsOpen))
        {
            // One that closes meanwhile is let go, as a host lets go of a client that hangs up. A
            // connection the app gave up on (a hello that took too long under load) or a quick look
            // at the host closed between IsOpen and the write, and the test failed with a broken
            // pipe on Linux (#101).
            try { await ch.SendEventAsync(name, data); }
            catch (IOException) when (!ch.IsOpen) { }
        }
    }

    public IReadOnlyList<JsonElement> Sent(string op) => [.. Received.Where(r => r.Op == op).Select(r => r.Data)];

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    // Started at once, unless the test has handlers to attach first: the app constructs its
    // view models before it starts the connection, so none of them misses the first snapshot.
    public HostConnection Connect(IUiDispatcher? ui = null, bool start = true)
    {
        var c = new HostConnection(ct => LocalClient.ConnectAsync(Endpoint, TimeSpan.FromSeconds(1), ct), ui ?? new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        if (start) { c.Start(); }
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
    // The last run's instance (agent-bridge-hostw on its way out after its host) reads Running for
    // this many more looks, and a Run meanwhile is ignored, as the task's IgnoreNew policy does.
    public int StillRunningLooks;
    // Or this, asked at each look and run, when set.
    public Func<bool>? StillRunningWhile;
    public List<string> Ignored { get; } = [];
    // Instances ended by Stop (IRegisteredTask.Stop), after which that task reads not Running until run again.
    public List<string> Ended { get; } = [];
    readonly HashSet<string> ended = [];
    public void Stop(string name)
    {
        Ended.Add(name);
        ended.Add(name);
    }
    public bool Exists(string name) => Registered.ContainsKey(name);
    public void Register(string name, string xml) => Registered[name] = xml;
    public void Delete(string name) => Registered.Remove(name);
    public void Run(string name)
    {
        if (StillRunningWhile?.Invoke() ?? StillRunningLooks > 0) { Ignored.Add(name); return; }
        ended.Remove(name);
        Ran.Add(name);
        OnRun?.Invoke();
    }
    public bool IsRunning(string name)
    {
        if (ended.Contains(name)) { return false; }
        if (StillRunningWhile is { } running) { return running(); }
        if (StillRunningLooks <= 0) { return false; }
        if (StillRunningLooks != int.MaxValue) { StillRunningLooks--; }
        return true;
    }
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

// The test's UI thread, as a dispatcher: actions run one at a time on it.
sealed class LockedDispatcher : IUiDispatcher
{
    readonly TestUi ui = TestUi.ForTest;

    public void Post(Action action) => ui.Invoke(action);
}

// A test's UI thread, as the app has one: the test's own steps (UiTestFramework runs each test
// on it), every action its dispatchers post, and every await any of them makes, one at a time
// under one gate, with this as the SynchronizationContext, as WinUI's is on the app's.
// First each dispatcher had a gate of its own, and the test thread read none, so it could see a
// posted action half done: a launch's State already "started" while its StatusText still said
// "Starting…" (#126), a list a PropertyChanged handler was adding to (#100), the card list
// mid-move (#101). Then one gate, but only around posted actions: a view model's await came
// back on whichever thread finished the call, and a test's own steps ran on xunit's, both beside
// the posted actions. So Settings redrew a box from a status while the test ticked it, and
// showed it unticked while its change was on its way; a launch page refreshed while the delivery
// that started it was being handled; MainViewModel was built while the host's first status was
// coming in (#101). The app never does that: its continuations come back to its UI thread.
// One per test, not one for all: the tests are separate apps, and one test's step would
// otherwise wait for every other's.
// "The UI thread" is a turn, not an OS thread: what is queued here (the test's steps, every
// continuation) runs on this context's own thread, while a dispatcher's Post from a pipe's thread
// takes its turn on that thread, under the gate, and returns once done. The view models never ask
// which thread they are on, only that nothing else runs meanwhile, and the tests rely on Post
// being done when it returns (a Post then a read of what it set), as LockedDispatcher always was.
// Two posts from different threads run in whichever order they take the gate: in the app too,
// they reach the dispatcher's queue in whichever order their threads get there.
sealed class TestUi : SynchronizationContext
{
    static readonly AsyncLocal<TestUi?> ambient = new();
    // Code that runs outside any test case (none does now) still gets one UI thread.
    static readonly TestUi shared = new();

    readonly object gate = new();
    readonly BlockingCollection<(SendOrPostCallback Run, object? State, ExecutionContext? Context)> queue = new();

    // A thread of its own, as the app's UI thread is, rather than the thread pool's: the fakes
    // block pool threads (a host held mid-request, a dispatcher waiting for its turn), and under
    // load a test's steps then waited for the pool to grow before they could take theirs.
    TestUi() => new Thread(() => { foreach (var item in queue.GetConsumingEnumerable()) { Run(item); } }) { IsBackground = true, Name = "test UI" }.Start();

    // The UI thread of the test this runs for.
    public static TestUi ForTest => ambient.Value ?? shared;

    // Whether this thread has the UI's turn now: it is the UI thread for as long as it holds it.
    public bool OnIt => Monitor.IsEntered(gate);

    // What the view models say once no posted action is part-way through.
    public static T Read<T>(Func<T> read) => ForTest.Invoke(read);

    // A test case, run on a UI thread of its own from start to end.
    public static Task<T> RunAsync<T>(Func<Task<T>> run)
    {
        var ui = new TestUi();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(_ =>
        {
            ambient.Value = ui;
            Task<T> task;
            try { task = run(); }
            catch (Exception e) { ui.queue.CompleteAdding(); done.SetException(e); return; }
            task.ContinueWith(t =>
            {
                // Its thread goes once what is queued has run; anything still posting after the
                // test (a connection being let go) takes its turns on the pool.
                ui.queue.CompleteAdding();
                if (t.IsFaulted) { done.SetException(t.Exception!.InnerExceptions); }
                else if (t.IsCanceled) { done.SetCanceled(); }
                else { done.SetResult(t.Result); }
            }, TaskScheduler.Default);
        }, null);
        return done.Task;
    }

    public void Invoke(Action action) => Invoke(() => { action(); return 0; });

    public T Invoke<T>(Func<T> action)
    {
        // Already on it, the context stays: in a test's step that is xunit's, which hands what is
        // posted to it on to this one and fails the test for an async void that throws.
        if (OnIt) { return action(); }
        lock (gate)
        {
            var was = SynchronizationContext.Current;
            SetSynchronizationContext(this);
            try { return action(); }
            finally { SetSynchronizationContext(was); }
        }
    }

    // In order, as a dispatcher's queue: its thread takes them one at a time, each under the gate,
    // so a dispatcher's action from another thread can come between two but never into one.
    public override void Post(SendOrPostCallback d, object? state)
    {
        var item = (d, state, ExecutionContext.Capture());
        if (!queue.IsAddingCompleted)
        {
            try { queue.Add(item); return; }
            catch (InvalidOperationException) { }
        }
        ThreadPool.UnsafeQueueUserWorkItem(_ => Run(item), null);
    }

    public override void Send(SendOrPostCallback d, object? state) => Invoke(() => d(state));

    public override SynchronizationContext CreateCopy() => this;

    void Run((SendOrPostCallback Run, object? State, ExecutionContext? Context) item)
    {
        if (item.Context is { } ec) { ExecutionContext.Run(ec, s => Invoke(() => item.Run(s)), item.State); }
        else { Invoke(() => item.Run(item.State)); }
    }
}

// Runs each post at once, as LockedDispatcher does, except while held: then they wait, as they
// would in a busy UI thread's queue, until released.
sealed class HoldingDispatcher : IUiDispatcher
{
    readonly TestUi ui = TestUi.ForTest;
    readonly Queue<Action> held = new();
    bool holding;

    public void Hold() => ui.Invoke(() => holding = true);

    public void Release()
    {
        var queued = ui.Invoke(() => { holding = false; List<Action> q = [.. held]; held.Clear(); return q; });
        foreach (var a in queued) { Post(a); }
    }

    public void Post(Action action) => ui.Invoke(() =>
    {
        if (holding) { held.Enqueue(action); return; }
        action();
    });
}

static class Wait
{
    // The condition is read under the UI gate, so it never sees a posted action part-way through.
    public static async Task UntilAsync(Func<bool> done, string what, TimeSpan? patience = null)
    {
        var deadline = DateTime.UtcNow + (patience ?? FakeHost.Patience);
        while (!TestUi.Read(done))
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
