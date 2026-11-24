using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

// design/agent-principals.md §3.4, "Session job": a caller whose parent chain no longer reaches the session's root (a shell whose parent
// exited) is still accepted when it is a member of the job the host made for that launch. The ancestor rule is unchanged, and a job
// is never trusted by name: the host asks about this credential's own job, for this credential's own root, for this caller.
public sealed class AgentCredentialJobTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abjob-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly DateTimeOffset Started = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset CallerStarted = Started.AddMinutes(5);

    public AgentCredentialJobTests() => Directory.CreateDirectory(root);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    // The job service as a test sees it: what was enrolled, what is a member, and every question asked.
    sealed class FakeJobs : ISessionJobs
    {
        public readonly List<(string Key, int Pid, DateTimeOffset Started)> Enrolled = [];
        public readonly HashSet<(string Key, int Pid)> Members = [];
        public readonly List<(string Key, int RootPid, DateTimeOffset RootStarted, int Pid, DateTimeOffset? Started)> Asked = [];
        public readonly List<string> Released = [];
        // What the host would still hold: like the real service, a release before the job exists releases nothing.
        public readonly HashSet<string> Held = [];
        public Exception? EnrollFails, ReleaseFails, ContainsFails;
        public Action? DuringEnroll;

        public void Enroll(string key, int processId, DateTimeOffset startedAt)
        {
            DuringEnroll?.Invoke();
            if (EnrollFails is not null) { throw EnrollFails; }
            Enrolled.Add((key, processId, startedAt));
            Held.Add(key);
        }

        public void Release(string key) { Released.Add(key); Held.Remove(key); if (ReleaseFails is not null) { throw ReleaseFails; } }

        public bool Contains(string key, int rootPid, DateTimeOffset rootStartedAt, int processId, DateTimeOffset? startedAt)
        {
            Asked.Add((key, rootPid, rootStartedAt, processId, startedAt));
            if (ContainsFails is not null) { throw ContainsFails; }
            return Enrolled.Any(e => e.Key == key && e.Pid == rootPid && e.Started == rootStartedAt) && Members.Contains((key, processId));
        }
    }

    // A tool shell whose parent has exited: the chain stops at it and never reaches the session root.
    static ProcessAncestry Orphan(int pid = 300, bool complete = true) => new([new PeerProcess(pid, CallerStarted, "pwsh.exe")], complete);

    static ProcessAncestry Inside() => new([new PeerProcess(300, CallerStarted, "pwsh.exe"), new PeerProcess(100, Started, "agent.exe")], true);

    AgentCredentials Store(ISessionJobs? jobs, List<string>? lines = null, Func<int, DateTimeOffset, bool>? alive = null) =>
        new(root, "machine1", log: lines is null ? null : lines.Add, rootAlive: alive, jobs: jobs);

    // A launch the host bound to its process, enrolled in a job whose member is the orphan caller (pid 300).
    static (string Token, string Key) Launched(AgentCredentials store, FakeJobs jobs, string session = "sess-1", string launch = "launch-1", int rootPid = 100)
    {
        var token = store.Prepare(session, launch)!;
        store.Bind(launch, rootPid, Started);
        var key = jobs.Enrolled.Single(e => e.Pid == rootPid).Key;
        jobs.Members.Add((key, 300));
        return (token, key);
    }

    [Fact]
    public void A_caller_whose_chain_does_not_reach_the_root_but_who_is_in_the_sessions_job_is_accepted()
    {
        var jobs = new FakeJobs();
        var store = Store(jobs);
        var (token, key) = Launched(store, jobs);
        var decision = store.Check(token, Orphan());
        Assert.NotNull(decision.Accepted);
        Assert.Equal("sess-1", decision.Accepted!.Session);
        // It asked about this credential's own job, for this credential's recorded root, for this exact caller (pid and start time).
        Assert.Equal((key, 100, Started, 300, (DateTimeOffset?)CallerStarted), Assert.Single(jobs.Asked));
        Assert.NotEqual(token, key);
    }

    [Fact]
    public void A_caller_in_neither_the_chain_nor_the_job_is_refused_and_logged()
    {
        var jobs = new FakeJobs();
        var lines = new List<string>();
        var store = Store(jobs, lines);
        var (token, _) = Launched(store, jobs);
        Assert.Null(store.Check(token, Orphan(pid: 999)).Accepted);
        Assert.Contains(lines, l => l.Contains("credential presented from outside its session"));
    }

    [Fact]
    public void A_child_that_broke_away_from_the_job_has_only_the_ancestor_rule()
    {
        var jobs = new FakeJobs();
        var store = Store(jobs);
        var (token, key) = Launched(store, jobs);
        jobs.Members.Remove((key, 300));
        Assert.Null(store.Check(token, Orphan()).Accepted);
        Assert.NotNull(store.Check(token, Inside()).Accepted);
    }

    [Fact]
    public void Another_sessions_job_never_admits_a_caller_to_this_one()
    {
        var jobs = new FakeJobs();
        var store = Store(jobs);
        var (first, _) = Launched(store, jobs, "sess-1", "launch-1", rootPid: 100);
        var second = store.Prepare("sess-2", "launch-2")!;
        store.Bind("launch-2", 200, Started);
        var keyOfSecond = jobs.Enrolled.Single(e => e.Pid == 200).Key;
        jobs.Members.Remove((jobs.Enrolled.Single(e => e.Pid == 100).Key, 300));
        jobs.Members.Add((keyOfSecond, 300));
        Assert.Null(store.Check(first, Orphan()).Accepted);
        Assert.Equal("sess-2", store.Check(second, Orphan()).Accepted!.Session);
    }

    [Fact]
    public void A_job_never_makes_up_for_a_partial_ancestry_or_a_dead_root()
    {
        var jobs = new FakeJobs();
        var alive = true;
        var store = Store(jobs, alive: (_, _) => alive);
        var (token, _) = Launched(store, jobs);
        Assert.Null(store.Check(token, Orphan(complete: false)).Accepted);
        Assert.Empty(jobs.Asked);
        alive = false;
        Assert.Null(store.Check(token, Orphan()).Accepted);
    }

    [Fact]
    public void A_caller_without_a_start_time_is_not_asked_about_by_pid_alone()
    {
        var jobs = new FakeJobs();
        var store = Store(jobs);
        var (token, _) = Launched(store, jobs);
        Assert.Null(store.Check(token, new ProcessAncestry([new PeerProcess(300, null, "pwsh.exe")], true)).Accepted);
        Assert.Empty(jobs.Asked);
    }

    [Fact]
    public void A_launch_that_never_bound_is_never_enrolled_and_a_failed_enrolment_does_not_fail_the_launch()
    {
        var jobs = new FakeJobs { EnrollFails = new InvalidOperationException("no job") };
        var lines = new List<string>();
        var store = Store(jobs, lines);
        var token = store.Prepare("sess-1", "launch-1")!;
        Assert.Empty(jobs.Enrolled);
        store.Bind("launch-1", 100, Started);
        Assert.Contains(lines, l => l.Contains("session job") && l.Contains("InvalidOperationException"));
        Assert.NotNull(store.Check(token, Inside()).Accepted);
        Assert.Null(store.Check(token, Orphan()).Accepted);
        store.Abandon("launch-1");
        Assert.Empty(jobs.Enrolled);
    }

    [Fact]
    public void A_job_that_cannot_be_read_never_admits_a_caller()
    {
        var jobs = new FakeJobs();
        var lines = new List<string>();
        var store = Store(jobs, lines);
        var (token, _) = Launched(store, jobs);
        jobs.ContainsFails = new InvalidOperationException("no job");
        Assert.Null(store.Check(token, Orphan()).Accepted);
        Assert.Contains(lines, l => l.Contains("could not be read") && l.Contains("InvalidOperationException"));
        Assert.NotNull(store.Check(token, Inside()).Accepted);
    }

    [Fact]
    public void A_job_that_cannot_be_let_go_never_leaves_a_revoked_credential_on_disk()
    {
        var jobs = new FakeJobs();
        var store = Store(jobs);
        Launched(store, jobs);
        jobs.ReleaseFails = new InvalidOperationException("no job");
        Assert.True(store.Revoke("sess-1", "a person revoked it"));
        Assert.Equal(0, store.Count);
        Assert.Equal(0, Store(null).Count);
    }

    [Fact]
    public void A_job_is_let_go_when_its_credential_is_revoked_replaced_or_its_session_ends()
    {
        var jobs = new FakeJobs();
        var alive = true;
        var store = Store(jobs, alive: (_, _) => alive);
        var (_, first) = Launched(store, jobs, "sess-1", "launch-1", rootPid: 100);
        Assert.True(store.Revoke("sess-1", "a person revoked it"));
        Assert.Equal([first], jobs.Released);

        var (_, second) = Launched(store, jobs, "sess-2", "launch-2", rootPid: 200);
        store.Prepare("sess-2", "launch-3");
        Assert.Contains(second, jobs.Released);

        var (token, third) = Launched(store, jobs, "sess-3", "launch-4", rootPid: 300);
        alive = false;
        Assert.Null(store.Check(token, Inside(rootPid: 300)).Accepted);
        Assert.Contains(third, jobs.Released);
    }

    static ProcessAncestry Inside(int rootPid) => new([new PeerProcess(900, CallerStarted, "pwsh.exe"), new PeerProcess(rootPid, Started, "agent.exe")], true);

    [Fact]
    public void A_credential_revoked_while_its_job_is_being_made_leaves_no_job_held()
    {
        var jobs = new FakeJobs();
        var store = Store(jobs);
        store.Prepare("sess-1", "launch-1");
        jobs.DuringEnroll = () => store.Revoke("sess-1", "a person revoked it");
        store.Bind("launch-1", 100, Started);
        Assert.Single(jobs.Enrolled);
        Assert.Empty(jobs.Held);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_restarted_host_enrols_the_live_roots_again_and_never_a_dead_or_unbound_one()
    {
        var jobs = new FakeJobs();
        var store = Store(jobs);
        var (_, live) = Launched(store, jobs, "sess-live", "launch-live", rootPid: 100);
        var (_, dead) = Launched(store, jobs, "sess-dead", "launch-dead", rootPid: 200);
        store.Prepare("sess-unbound", "launch-unbound");

        var after = new FakeJobs();
        var lines = new List<string>();
        var restarted = new AgentCredentials(root, "machine1", log: lines.Add, rootAlive: (pid, _) => pid == 100, jobs: after);
        Assert.Equal([(live, 100, Started)], after.Enrolled);
        Assert.DoesNotContain(after.Enrolled, e => e.Key == dead);
        Assert.Equal(3, restarted.Count);

        // A failure to enrol is logged, never thrown: the restarted host still starts.
        var failing = new FakeJobs { EnrollFails = new InvalidOperationException("no job") };
        var again = new AgentCredentials(root, "machine1", log: lines.Add, rootAlive: (pid, _) => pid == 100, jobs: failing);
        Assert.Equal(3, again.Count);
        Assert.Contains(lines, l => l.Contains("session job") && l.Contains("InvalidOperationException"));
    }

    [Fact]
    public void A_token_value_never_reaches_the_job_service_or_the_log()
    {
        var jobs = new FakeJobs();
        var lines = new List<string>();
        var store = Store(jobs, lines);
        var (token, key) = Launched(store, jobs);
        store.Check(token, Orphan());
        Assert.DoesNotContain(token, key);
        Assert.All(lines, l => Assert.DoesNotContain(token, l));
        Assert.Matches("^[0-9A-F]{64}$", key);
    }
}
