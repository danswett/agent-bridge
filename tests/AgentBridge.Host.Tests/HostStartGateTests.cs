using System.Collections.Concurrent;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Restore;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// #986 (Trust's finding 3 on #837): a host the logon task's launcher starts restores nothing, and
// resumes no agent, when the person pressed Stop in the app, whether before it started or while it
// was starting. A real restore recorder, with a session an earlier boot left live, which a start
// restores at once.
public sealed class HostStartGateTests : IDisposable
{
    readonly string profile = Path.Combine(Path.GetTempPath(), "abh-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Resumer resumer = new();

    public HostStartGateTests()
    {
        Directory.CreateDirectory(profile);
        RestoreFiles.Write(Record, new RestoreFile
        {
            Boot = DateTimeOffset.UnixEpoch,
            Instance = "run1",
            Written = DateTimeOffset.UtcNow.AddMinutes(-5),
            Sessions = [new RestoreEntry { Id = "a", Agent = AgentKind.Copilot, Title = "T a", Cwd = profile, Seen = DateTimeOffset.UtcNow.AddMinutes(-10) }],
        });
    }

    public void Dispose() => Directory.Delete(profile, recursive: true);

    string Record => Path.Combine(profile, RestoreFiles.FileName);

    RestoreRecorder Recorder() => new(new Source(), new RestoreOptions { Path = Record, Instance = "run2", Ends = new NotEnded(), Boot = new Booted() });

    void PressStop() => File.WriteAllText(Path.Combine(profile, HostLauncherContract.StopMarker), "");

    // As Program runs it, in a host the logon task's launcher started.
    static Task Supervised(Func<HostHandover, Task> serve) =>
        HostHandover.RunAsync(async h => { await serve(h); return 0; }, () => { }, supervised: true);

    [Fact]
    public async Task A_host_started_after_the_apps_Stop_starts_no_restore_and_resumes_no_agent()
    {
        PressStop();
        var restore = Recorder();
        bool? started = null;

        await Supervised(h =>
        {
            started = new HostStartGate(h, profile).StartRestore(restore, resumer);
            return Task.CompletedTask;
        });
        await restore.DisposeAsync();

        Assert.False(started);
        Assert.Empty(resumer.Asked);
        // Never started: nothing judged, and the record left as the earlier run wrote it, for the
        // next start to judge.
        Assert.Null(restore.Notice);
        Assert.Equal("run1", RestoreFiles.Read(Record).File!.Instance);
    }

    // The race at its worst, every run: the restore's resume is already under way when Stop is pressed,
    // not left to the scheduler to start before or after it. Without the hold, the agent is resumed.
    [Fact]
    public async Task A_Stop_pressed_while_the_host_is_starting_resumes_no_agent_and_leaves_the_session_offered()
    {
        var restore = Recorder();
        var resuming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Supervised(async h =>
        {
            var gate = new HostStartGate(h, profile) { Resuming = () => resuming.TrySetResult() };
            Assert.True(gate.StartRestore(restore, resumer));
            await resuming.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // Before the pipe opens: only the look Program takes with it open sees this, and stops.
            PressStop();
            Assert.True(h.StoppedFromTheApp(profile));
            await restore.DisposeAsync();
        });

        Assert.Empty(resumer.Asked);
        var owed = Assert.Single(RestoreFiles.Read(Record).File!.Owed);
        Assert.Equal(("a", (DateTimeOffset?)null), (owed.Entry.Id, owed.Restored));
    }

    [Fact]
    public async Task Once_the_host_serves_what_died_is_restored()
    {
        var restore = Recorder();

        await Supervised(async h =>
        {
            var gate = new HostStartGate(h, profile);
            Assert.True(gate.StartRestore(restore, resumer));
            gate.Serving();
            for (var i = 0; i < 500 && resumer.Asked.IsEmpty; i++) { await Task.Delay(10); }
            await restore.DisposeAsync();
        });

        Assert.Equal(["a"], resumer.Asked.Select(a => a.Id));
    }

    sealed class Resumer : ISessionResumer
    {
        public readonly ConcurrentQueue<ResumeAsk> Asked = new();

        public Task<LaunchResult> ResumeAsync(ResumeAsk ask, CancellationToken cancellationToken = default)
        {
            Asked.Enqueue(ask);
            return Task.FromResult(new LaunchResult(LaunchOutcome.Started) { Session = ask.Id });
        }
    }

    // A boot after the record's: what was live then died with it.
    sealed class Booted : IBootClock
    {
        public DateTimeOffset Boot() => DateTimeOffset.UtcNow;
    }

    sealed class NotEnded : ISessionEnds
    {
        public bool? Ended(AgentKind agent, string id) => false;
    }

    sealed class Source : ISessionSource
    {
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public IReadOnlyList<Session> Snapshot() => [];
        public SessionList List(string? cursor = null) => new([], 0);
        public Session? Get(string id) => null;
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
