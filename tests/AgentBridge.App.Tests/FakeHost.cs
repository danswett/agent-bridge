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
    readonly ConcurrentDictionary<string, Func<JsonElement, Task<object?>>> ops = new();
    LocalServer? server;

    public ConcurrentQueue<(string Op, JsonElement Data)> Received { get; } = new();
    public ConcurrentBag<IProtocolChannel> Channels { get; } = [];
    public List<string> Capabilities { get; } = [AgentBridge.Protocol.Capabilities.SessionsRead, AgentBridge.Protocol.Capabilities.SessionsAnswer, AgentBridge.Protocol.Capabilities.SessionsReply, AgentBridge.Protocol.Capabilities.SessionsAttach, AgentBridge.Protocol.Capabilities.SessionsStop, UsageProtocol.Capability, HostOps.Capability, HostOps.AgentReadinessCapability];
    public List<Session> Sessions { get; } = [];
    // Sent with sessions.synced.
    public DiscoveryHeld? Held { get; set; }
    // While set, a subscribe's snapshot is sent but its sessions.synced waits for it.
    public TaskCompletionSource? HoldSynced { get; set; }
    // What sessions.subscribe answers in place of its result, when set.
    public object? SubscribeReply { get; set; }
    // Called with each sessions.subscribe's number (from 1) before it is answered: a test can send
    // what an earlier subscription still had on its way.
    public Action<int>? OnSubscribe { get; set; }
    int subscribes;
    // How many sessions.synced have been sent.
    public int SyncedSent => Volatile.Read(ref syncedSent);
    int syncedSent;
    public HostStatus Status { get; set; } = StatusOf();
    // hello's machine.instance: the run of the host a list is resumed in (#601).
    public string? Instance { get; set; }
    // As a host that offers sessions.patch keeps it (SessionService): the revision of its last
    // change, each session's, and what it sent each channel that asked for patches.
    long revision;
    readonly Dictionary<string, long> changedAt = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<IProtocolChannel, Feed> feeds = new();
    // Added once to a resumed sessions.synced's live set: an id the app neither holds nor was sent.
    public string? Ghost { get; set; }
    public long Revision { get { lock (Sessions) { return revision; } } }
    // Each event sent, by name, in order.
    public ConcurrentQueue<(string Name, JsonElement Data)> EventsSent { get; } = new();

    sealed class Feed
    {
        public bool Live;
        public readonly Dictionary<string, (long Seq, System.Text.Json.Nodes.JsonObject Json)> Sent = new(StringComparer.Ordinal);
    }

    public FakeHost()
    {
        Directory.CreateDirectory(folder);
        On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("host-id", "Test PC", "windows", "0.1.0", Instance: Instance), [.. Capabilities]));
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

    public void On(string op, Func<JsonElement, object?> answer) => ops[op] = d => Task.FromResult(answer(d));

    // A request the host takes its time over, held by awaiting. Held by blocking (.Wait, .Result,
    // SpinWait) each one kept a pool thread for up to Patience, and the tests side by side then
    // starved: an 8.4 s gap with 11 pool threads and 33 items queued on the Arm runner (#511).
    public void OnAsync(string op, Func<JsonElement, Task<object?>> answer) => ops[op] = answer;

    // A handler's hold, up to Patience: awaited, so it holds no pool thread. It resumes on the
    // pool, as the blocked handler did, rather than inline on whichever thread opened the gate.
    public static async Task Until(Task gate) =>
        await Task.WhenAny(gate, Task.Delay(Patience)).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

    // Until every connection there is has closed, up to Patience.
    public Task UntilClosed() => Until(Task.WhenAll(Channels.Distinct().Select(c => c.Closed)));

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
        // Answered as a host does, unless the test answers it itself (a host that refuses it).
        if (op == Ops.SessionsSubscribe && !ops.ContainsKey(op))
        {
            var n = Interlocked.Increment(ref subscribes);
            OnSubscribe?.Invoke(n);
            // A client asks for patches, and to resume, only of a host that offers them.
            var patch = Capabilities.Contains(AgentBridge.Protocol.Capabilities.SessionsPatch)
                && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("patch", out var p) && p.ValueKind == JsonValueKind.True;
            long? since = null;
            if (patch && Instance is not null && data.TryGetProperty("since", out var sv) && sv.ValueKind == JsonValueKind.Object
                && sv.TryGetProperty("instance", out var si) && si.ValueKind == JsonValueKind.String && si.GetString() == Instance
                && sv.TryGetProperty("revision", out var sr) && sr.ValueKind == JsonValueKind.Number && sr.TryGetInt64(out var r))
            {
                since = r;
            }
            List<Session> snapshot;
            List<string> live;
            long at;
            lock (Sessions)
            {
                at = revision;
                if (since > at) { since = null; }
                snapshot = since is { } after ? [.. Sessions.Where(s => changedAt.GetValueOrDefault(s.Id) > after)] : [.. Sessions];
                live = [.. Sessions.Select(s => s.Id)];
            }
            string? ghost = null;
            if (since is not null) { ghost = Ghost; Ghost = null; }
            if (ghost is not null) { live.Add(ghost); }
            Feed? feed = patch ? new Feed() : null;
            if (feed is not null) { feeds[ch] = feed; } else { feeds.TryRemove(ch, out _); }
            var hold = HoldSynced;
            object result = since is null ? new SubscribeResult(patch ? at : 1, snapshot.Count) : new SubscribeResult(at, snapshot.Count, Resumed: true);
            return Task.FromResult<object?>(new ChannelReply(SubscribeReply ?? result, async c =>
            {
                foreach (var s in snapshot)
                {
                    var seq = await Send(c, Events.SessionUpsert, s);
                    if (feed is not null) { lock (feed) { feed.Sent[s.Id] = (seq, SessionPatches.Json(s)); } }
                }
                if (hold is not null) { await hold.Task; }
                // Live before the synced goes, as SessionService's feed is: the app may take the synced,
                // and the test send its next change, before this write returns.
                if (feed is not null) { lock (feed) { feed.Live = true; } }
                await Send(c, Events.SessionsSynced, feed is null ? new SessionsSynced(1, Held) : new SessionsSynced(at, Held, since is null ? null : live));
                Interlocked.Increment(ref syncedSent);
            }));
        }
        return ops.TryGetValue(op, out var answer) ? answer(data) : Task.FromResult<object?>(null);
    }

    async Task<long> Send(IProtocolChannel ch, string name, object data)
    {
        var seq = await ch.SendEventAsync(name, data);
        EventsSent.Enqueue((name, JsonSerializer.SerializeToElement(data, ProtocolJson.Options)));
        return seq;
    }

    public async Task SendAsync(string name, object data)
    {
        // Listed as it is announced, as a host lists its sessions: the next subscribe's snapshot
        // (a reconnect, or a gap in the events) has it too. Announced only as an event, a session
        // was gone again from any snapshot after it, and its card with it. TrustTests' card to
        // open was never seen on Linux CI that way, ten times (#101).
        long at = 0;
        if (name == Events.SessionUpsert && data is Session upserted)
        {
            lock (Sessions)
            {
                var i = Sessions.FindIndex(s => s.Id == upserted.Id);
                if (i >= 0) { Sessions[i] = upserted; } else { Sessions.Add(upserted); }
                at = ++revision;
                changedAt[upserted.Id] = at;
            }
        }
        else if (name == Events.SessionRemove && data is SessionRemoved removed)
        {
            lock (Sessions)
            {
                Sessions.RemoveAll(s => s.Id == removed.Id);
                at = ++revision;
                changedAt.Remove(removed.Id);
            }
        }
        foreach (var ch in Channels.Distinct().Where(c => c.IsOpen))
        {
            // One that closes meanwhile is let go, as a host lets go of a client that hangs up. A
            // connection the app gave up on (a hello that took too long under load) or a quick look
            // at the host closed between IsOpen and the write, and the test failed with a broken
            // pipe on Linux (#101).
            try
            {
                if (at > 0 && feeds.TryGetValue(ch, out var feed)) { await Fed(ch, feed, name, data, at); }
                else { await Send(ch, name, data); }
            }
            catch (IOException) when (!ch.IsOpen) { }
        }
    }

    // To a channel that asked for patches, as SessionService sends them: once its snapshot is
    // whole, each change as a patch on what that channel was last sent, nothing for no change, and
    // removals with the revision. The tests send one event at a time.
    async Task Fed(IProtocolChannel ch, Feed feed, string name, object data, long at)
    {
        bool live;
        lock (feed) { live = feed.Live; }
        if (data is Session s)
        {
            var json = SessionPatches.Json(s);
            (long Seq, System.Text.Json.Nodes.JsonObject Json) had;
            bool sent;
            lock (feed) { sent = feed.Sent.TryGetValue(s.Id, out had); }
            var next = live ? SessionPatches.Next(sent ? had.Json : null, sent ? had.Seq : 0, json, at) : null;
            if (live && next is null) { return; }
            var seq = next is null ? await Send(ch, name, data) : await Send(ch, Events.SessionPatch, next);
            lock (feed) { feed.Sent[s.Id] = (seq, json); }
            return;
        }
        var gone = (SessionRemoved)data;
        lock (feed) { feed.Sent.Remove(gone.Id); }
        await Send(ch, name, live ? gone with { Revision = at } : gone);
    }

    public IReadOnlyList<JsonElement> Sent(string op) => [.. Received.Where(r => r.Op == op).Select(r => r.Data)];

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    // Started at once, unless the test has handlers to attach first: the app constructs its
    // view models before it starts the connection, so none of them misses the first snapshot.
    public HostConnection Connect(IUiDispatcher? ui = null, bool start = true, Action<string>? log = null)
    {
        var c = new HostConnection(ct => LocalClient.ConnectAsync(Endpoint, TimeSpan.FromSeconds(1), ct), ui ?? new LockedDispatcher(), log) { RetryDelay = TimeSpan.FromMilliseconds(100) };
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
    // Task Scheduler that cannot be reached: asking whether a task runs fails.
    public bool Unavailable;
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
        if (Unavailable) { throw new InvalidOperationException("Task Scheduler is not available"); }
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
    // The processes that run: a launcher the test says is still there.
    public HashSet<int> Alive { get; } = [];
    public int Start(string path, IReadOnlyList<string> arguments) { Started.Add((path, arguments)); OnStart?.Invoke(); return 99; }
    public bool IsRunning(int pid) => Alive.Contains(pid);
    // Ending a process ends it; and what else happens then, as a test says.
    public Action<int>? OnKill;
    public void Kill(int pid)
    {
        Killed.Add(pid);
        Alive.Remove(pid);
        OnKill?.Invoke(pid);
    }
    // Processes an earlier run of the app started, by pid and start time, that this can take on.
    public HashSet<(int Pid, long StartedTicks)> Others { get; } = [];
    public bool Adopt(int pid, long startedTicks) => Others.Contains((pid, startedTicks)) && Alive.Contains(pid);
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

    public int QueuedCount => ui.Invoke(() => held.Count);

    public void Hold() => ui.Invoke(() => holding = true);

    public void Release()
    {
        foreach (var a in TakeHeld()) { Post(a); }
    }

    public List<Action> TakeHeld() => ui.Invoke(() => { holding = false; List<Action> q = [.. held]; held.Clear(); return q; });

    public void Post(Action action) => ui.Invoke(() =>
    {
        if (holding) { held.Enqueue(action); return; }
        action();
    });
}

static class Wait
{
    // The condition is read under the UI gate, so it never sees a posted action part-way through.
    // why: what was seen instead, said when it times out.
    public static async Task UntilAsync(Func<bool> done, string what, TimeSpan? patience = null, Func<string>? why = null)
    {
        var deadline = DateTime.UtcNow + (patience ?? FakeHost.Patience);
        var last = DateTime.UtcNow;
        var longest = TimeSpan.Zero;
        while (!TestUi.Read(done))
        {
            var now = DateTime.UtcNow;
            if (now - last > longest) { longest = now - last; }
            last = now;
            // On Windows CI several tests have timed out within the same few milliseconds (#465):
            // a gap of seconds between two looks says the test itself could not run (the pool or
            // its UI thread held), not that what it waited for never came.
            if (now > deadline)
            {
                throw TimedOut(what, longest, why is null ? null : TestUi.Read(why));
            }
            await Task.Delay(20);
        }
    }

    internal static TimeoutException TimedOut(string what, TimeSpan longest, string? details) =>
        new($"timed out waiting for {what} (longest gap between looks {longest.TotalMilliseconds:F0} ms; "
            + $"pool threads {ThreadPool.ThreadCount}, queued {ThreadPool.PendingWorkItemCount}){(details is null ? "" : $": {details}")}");
}

// Only counts and phase times cross the pipe/UI boundary; never file contents.
sealed class AttachmentDeliveryEvidence(long expectedBytes, TimeProvider? time = null)
{
    enum Phase { Waiting, Start, Chunks, AllBytes, Reply, Acknowledged, TerminalUi }
    readonly object gate = new();
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly long began = (time ?? TimeProvider.System).GetTimestamp();
    Phase phase;
    long? start, firstChunk, lastChunk, reply, acknowledged, terminal;
    long bytes;
    int chunks;
    string? outcome, error;
    string status = "not observed";
    DeliveryState? uiState;
    Tone uiTone = Tone.None;

    void Reached(Phase next)
    {
        if (next > phase) { phase = next; }
    }

    public void AttachmentStarted()
    {
        lock (gate) { start ??= clock.GetTimestamp(); Reached(Phase.Start); }
    }

    public void ChunkReceived(int count)
    {
        lock (gate)
        {
            firstChunk ??= clock.GetTimestamp();
            lastChunk = clock.GetTimestamp();
            bytes += count;
            chunks++;
            Reached(bytes == expectedBytes ? Phase.AllBytes : Phase.Chunks);
        }
    }

    public void ReplyReceived()
    {
        lock (gate) { reply ??= clock.GetTimestamp(); Reached(Phase.Reply); }
    }

    public void ReplyAcknowledged(string? value)
    {
        lock (gate) { acknowledged ??= clock.GetTimestamp(); outcome = value; Reached(Phase.Acknowledged); }
    }

    public void ObserveUi(string value, Tone tone, string? failure, DeliveryState? state)
    {
        lock (gate)
        {
            status = value;
            error = failure;
            uiState = state;
            uiTone = tone;
            // Delivery completion is upstream of the view model settling its presentation.
            if (!string.IsNullOrWhiteSpace(value) && tone is Tone.Ok or Tone.Bad or Tone.Warn)
            {
                terminal ??= clock.GetTimestamp();
                Reached(Phase.TerminalUi);
            }
        }
    }

    public string Describe()
    {
        lock (gate)
        {
            var last = phase switch
            {
                Phase.Start => "attachment start received", Phase.Chunks => "attachment chunk received",
                Phase.AllBytes => "all attachment bytes received", Phase.Reply => "reply received",
                Phase.Acknowledged => "reply acknowledged", Phase.TerminalUi => "terminal UI observed",
                _ => "waiting for attachment start",
            };
            string At(long? stamp) => stamp is { } t
                ? clock.GetElapsedTime(began, t).TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms"
                : "not observed";
            return $"last phase={last}; attachment start={At(start)}; chunks={chunks}, bytes={bytes}/{expectedBytes}, "
                + $"first chunk={At(firstChunk)}, last chunk={At(lastChunk)}; reply received={At(reply)}; "
                + $"reply acknowledgement={At(acknowledged)}, outcome={outcome ?? "none"}; terminal UI={At(terminal)}, "
                + $"state={uiState?.ToString() ?? "not observed"}, tone={uiTone}, status={status}, error={error ?? "none"}";
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
