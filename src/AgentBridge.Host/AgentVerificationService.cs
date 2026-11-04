using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// Reports belong to this host run only. A successful launch, login, or hook from another
// session is not evidence that the selected agent can answer through its current hooks.
public sealed class AgentVerificationService : IAsyncDisposable
{
    readonly ISessionSource source;
    readonly ISessionLauncher launcher;
    readonly IHookNotices? hooks;
    readonly Func<string?> unavailable;
    readonly Action<string> log;
    readonly TimeProvider time;
    readonly TimeSpan timeout;
    readonly TimeSpan poll;
    readonly CancellationTokenSource stopping;
    readonly Lock gate = new();
    readonly Dictionary<Guid, Run> runs = [];
    readonly Dictionary<string, Run> latest = new(StringComparer.Ordinal);

    sealed class Run(AgentVerificationRequest request, AgentVerificationReport report, CancellationTokenSource stop)
    {
        public AgentVerificationRequest Request { get; } = request;
        public AgentVerificationReport Report { get; set; } = report;
        public CancellationTokenSource Stop { get; } = stop;
        public Task Task { get; set; } = Task.CompletedTask;
        public SessionProcess? Process { get; set; }
    }

    sealed record HookProof(string Session, int? Owner, IReadOnlyList<int> Ancestors, DateTimeOffset Received, DateTimeOffset Handled);

    public AgentVerificationService(ISessionSource source, ISessionLauncher launcher, IHookNotices? hooks,
        Func<string?> unavailable, Action<string>? log = null, CancellationToken stopping = default,
        TimeProvider? time = null, TimeSpan? timeout = null, TimeSpan? poll = null)
    {
        this.source = source;
        this.launcher = launcher;
        this.hooks = hooks;
        this.unavailable = unavailable;
        this.log = log ?? (_ => { });
        this.time = time ?? TimeProvider.System;
        this.timeout = timeout ?? TimeSpan.FromMinutes(8);
        this.poll = poll ?? TimeSpan.FromMilliseconds(250);
        this.stopping = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    }

    public event Action? Changed;

    public IReadOnlyList<AgentVerificationReport> Snapshot
    {
        get
        {
            lock (gate)
            {
                return latest.Values.Select(Current).ToArray();
            }
        }
    }

    AgentVerificationReport Current(Run run)
    {
        if (run.Report.State != VerificationState.Verified) { return run.Report; }
        var why = unavailable()
            ?? (source.Held is not null ? "Session discovery is incomplete." : null)
            ?? (source.ProcessOf(run.Report.Session!) != run.Process ? "The verified session's process is no longer current." : null)
            ?? (time.GetUtcNow() - run.Report.CheckedAt >= TimeSpan.FromMinutes(10) ? "The test evidence has expired." : null);
        return why is null ? run.Report : run.Report with
        {
            State = VerificationState.NeedsAction,
            Detail = $"{why} Run a new verification before finishing setup.",
        };
    }

    public AgentVerificationReport Start(AgentVerificationRequest request)
    {
        if (!request.Confirmed) { throw new ArgumentException("Confirm opening a new test session and sending its harmless verification prompt. Provider usage may be charged; normal sign-in and folder-trust questions remain yours to answer."); }
        if (!Guid.TryParseExact(request.Request, "D", out var key) || key == Guid.Empty) { throw new ArgumentException("Verification needs a new request UUID."); }
        if (!LaunchProtocol.Agents.Contains(request.Agent, StringComparer.Ordinal)) { throw new ArgumentException("Choose agency, copilot, claude or codex."); }
        if (string.IsNullOrWhiteSpace(request.Workspace)) { throw new ArgumentException("Choose an approved workspace for the test session."); }
        var launch = new LaunchRequest { Request = request.Request, Agent = request.Agent, Workspace = request.Workspace, Profile = request.Profile };
        if (LaunchProtocol.Check(launch) is { } bad) { throw new ArgumentException(bad); }
        AgentVerificationReport started;
        lock (gate)
        {
            stopping.Token.ThrowIfCancellationRequested();
            foreach (var old in runs.Where(p => p.Value.Report.State != VerificationState.Running
                && time.GetUtcNow() - p.Value.Report.CheckedAt > TimeSpan.FromMinutes(25)).Select(p => p.Key).ToArray()) { runs.Remove(old); }
            if (runs.TryGetValue(key, out var existing))
            {
                if (existing.Request != request) { throw new ArgumentException("A verification UUID cannot be reused with different selections."); }
                return Current(existing);
            }
            if (runs.Values.Any(r => r.Report.State == VerificationState.Running)) { throw new InvalidOperationException("Another verification is running. Finish it or stop observing it before opening another test session."); }
            if (unavailable() is { } why) { throw new InvalidOperationException($"Verification is unavailable: {why}"); }
            if (source.Held is not null) { throw new InvalidOperationException("Session discovery is incomplete. Resolve it before opening a test session."); }
            if (hooks is null) { throw new InvalidOperationException("Hooks are not active in this host. Set up hooks and explicitly restart the host before verification."); }
            if (!launcher.Available) { throw new InvalidOperationException("Session launching is unavailable. Check the selected CLI, approved workspace, and host state."); }
            var now = time.GetUtcNow();
            var report = new AgentVerificationReport(request.Request, request.Agent, request.Workspace, request.Profile,
                VerificationState.Running, "Opening a new test session. Answer sign-in or folder-trust questions in its window.",
                now, now, new(ReadinessState.Unknown, "Waiting for launch."),
                new(ReadinessState.Unknown, "Waiting for this new session and its process."),
                new(ReadinessState.Unknown, "Waiting for the main agent's verification reply."),
                new(ReadinessState.Unknown, "Waiting for a fresh hook from the same process and session."));
            var run = new Run(request, report, CancellationTokenSource.CreateLinkedTokenSource(stopping.Token));
            runs.Add(key, run);
            latest[request.Agent] = run;
            run.Task = Task.Run(() => ObserveAsync(key, run));
            _ = run.Task.ContinueWith(t => log($"Agent verification {key}: unexpected failure: {t.Exception}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            started = report;
        }
        Changed?.Invoke();
        return started;
    }

    public AgentVerificationReport Cancel(string request)
    {
        AgentVerificationReport report;
        var changed = false;
        lock (gate)
        {
            if (!Guid.TryParseExact(request, "D", out var key) || !runs.TryGetValue(key, out var run)) { throw new ArgumentException("That verification is not known to this host."); }
            if (run.Report.State == VerificationState.Running)
            {
                run.Stop.Cancel();
                run.Report = run.Report with
                {
                    State = VerificationState.Cancelled, CheckedAt = time.GetUtcNow(),
                    Detail = "Observation stopped. The requested launch may still open and its session was not closed. Check its window; close it yourself if no longer needed.",
                };
                changed = true;
            }
            report = Current(run);
        }
        if (changed) { Changed?.Invoke(); }
        return report;
    }

    async Task ObserveAsync(Guid key, Run run)
    {
        var bound = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Started(Guid request, string id) { if (request == key) { bound.TrySetResult(id); } }
        var reader = hooks!.Subscribe();
        launcher.RequestRegistered += Started;
        Task<LaunchResult>? launching = null;
        try
        {
            var before = source.Snapshot().Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expected = $"AGENT_BRIDGE_VERIFY_{Guid.NewGuid():N}";
            var prompt = $"This is an {Product.Name} connection test. Reply with exactly {expected} and nothing else. Do not use tools, read or change files, or delegate.";
            var agent = run.Request.Agent == "agency" ? AgentKind.Copilot : Enum.Parse<AgentKind>(run.Request.Agent, ignoreCase: true);
            var hookAgent = run.Request.Agent == "agency" ? "copilot" : run.Request.Agent;
            var proofs = new Queue<HookProof>();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(run.Stop.Token);
            using var timer = time.CreateTimer(_ => limit.Cancel(), null, timeout, Timeout.InfiniteTimeSpan);
            var ct = limit.Token;
            ct.ThrowIfCancellationRequested();
            if (unavailable() is { } unavailableNow) { Finish(VerificationState.Blocked, unavailableNow); return; }
            // Launch commits independently of a pipe's lifetime. Cancelling this test stops
            // observation, never claims the launch was undone, and never kills its session.
            launching = launcher.LaunchVerificationAsync(new LaunchRequest
            {
                Request = run.Request.Request, Agent = run.Request.Agent, Workspace = run.Request.Workspace,
                Profile = run.Request.Profile, Permissions = LaunchProtocol.Ask, Prompt = prompt,
            }, "local app verification");
            var launchRead = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (unavailable() is { } why) { Finish(VerificationState.Blocked, why); return; }
                while (reader.TryRead(out var notice))
                {
                    var now = time.GetUtcNow();
                    if (notice.Agent != hookAgent || notice.SessionId is not { } id || notice.ReceivedAt is not { } received
                        || received < run.Report.StartedAt || received > now || notice.HandledAt < received || notice.HandledAt > now) { continue; }
                    proofs.Enqueue(new(id, notice.OwnerPid, notice.Ancestors, received, notice.HandledAt));
                    if (proofs.Count > 256) { proofs.Dequeue(); }
                }
                if (!launchRead && launching.IsCompleted)
                {
                    var result = await launching;
                    launchRead = true;
                    if (result.Outcome == LaunchOutcome.Started && result.Session is { } id) { bound.TrySetResult(id); }
                    if (result.Outcome is LaunchOutcome.Failed or LaunchOutcome.Refused or LaunchOutcome.Busy)
                    {
                        Set(run, Read(run) with { Launch = new(ReadinessState.NeedsAction, result.Reason ?? "The test session was not started.") });
                        Finish(result.Outcome == LaunchOutcome.Failed ? VerificationState.Failed : VerificationState.NeedsAction, result.Reason ?? "The test session was not started.");
                        return;
                    }
                    if (result.Outcome == LaunchOutcome.Unconfirmed)
                    {
                        Set(run, Read(run) with { Launch = new(ReadinessState.NeedsAction, result.Reason ?? "Check the test session's window for sign-in or folder trust. Observation continues.") });
                    }
                }
                if (source.Held is not null)
                {
                    Set(run, Read(run) with
                    {
                        Detail = "Waiting for session discovery to complete. Answer sign-in or folder-trust questions in the test window; observation continues.",
                        Discovery = new(ReadinessState.Unknown, "Session discovery is incomplete; waiting within this test's timeout."),
                    });
                }
                else if (bound.Task.IsCompletedSuccessfully)
                {
                    var id = await bound.Task;
                    if (before.Contains(id)) { Finish(VerificationState.NeedsAction, "The launcher returned an existing session, not a new verification session. Run a new test."); return; }
                    var report = Read(run) with { Session = id, Launch = new(ReadinessState.Working, "The launcher bound this request to a new session.") };
                    var session = source.GetUnbudgeted(id);
                    var process = source.ProcessOf(id);
                    if (session?.Agent == agent && process is { Id: > 0, StartedAt: { } started } && started >= report.StartedAt)
                    {
                        if (run.Process is not null && run.Process != process) { Finish(VerificationState.NeedsAction, "The test session's process changed. Run a new test."); return; }
                        run.Process = process;
                        report = report with { Discovery = new(ReadinessState.Working, "The exact new session and its new process were discovered."), DiscoveredAt = report.DiscoveredAt ?? time.GetUtcNow() };
                        var response = session.Activity.Source?.Response is null ? session.Activity.Response : session.Activity.Own?.Response;
                        if (session.UpdatedAt >= report.StartedAt && session.Activity.UserMessage?.Trim() == prompt && response?.Trim() == expected)
                        {
                            report = report with { Provider = new(ReadinessState.Working, "The main agent answered this run's unique test prompt."), ProviderObservedAt = report.ProviderObservedAt ?? time.GetUtcNow() };
                        }
                        if (proofs.FirstOrDefault(p => string.Equals(p.Session, id, StringComparison.OrdinalIgnoreCase)
                            && p.Received >= started && (p.Owner == process.Id || p.Ancestors.Contains(process.Id))) is { } proof)
                        {
                            report = report with { Hooks = new(ReadinessState.Working, "A fresh hook reached this host from the same session and process."), HookReceivedAt = proof.Received };
                        }
                        if (report.Provider.State == ReadinessState.Working && report.Hooks.State == ReadinessState.Working)
                        {
                            ct.ThrowIfCancellationRequested();
                            Set(run, report with { State = VerificationState.Verified, Detail = "This new session was discovered, answered the test prompt, and delivered a correlated hook. This verifies this agent and workspace only; it does not verify relay connectivity or other agents." });
                            return;
                        }
                    }
                    Set(run, report);
                }
                if (reader.Completion.IsCompleted) { Finish(VerificationState.Blocked, "Hook intake stopped. Restart the host before testing again."); return; }
                await Task.Delay(poll, time, ct);
            }
        }
        catch (OperationCanceledException)
        {
            Finish(run.Stop.IsCancellationRequested ? VerificationState.Cancelled : VerificationState.NeedsAction,
                run.Stop.IsCancellationRequested
                    ? "Observation stopped. The requested launch may still open and its session was not closed. Check its window; close it yourself if no longer needed."
                    : "Verification timed out without all required evidence. Check the session's window for sign-in, folder trust, or provider errors. It was not closed; a new test needs your confirmation.");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            log($"Agent verification {key}: {e}");
            Finish(VerificationState.Failed, $"Verification failed: {e.Message}. Any opened session was not closed.");
        }
        finally
        {
            if (Read(run).State == VerificationState.Running) { Finish(VerificationState.Failed, "Verification stopped unexpectedly. Check the host log. Any requested session was not closed."); }
            launcher.RequestRegistered -= Started;
            hooks.Unsubscribe(reader);
            run.Stop.Dispose();
            if (launching is not null)
            {
                _ = launching.ContinueWith(t => log($"Agent verification {key}: launch failed after observation stopped: {t.Exception}"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
        void Finish(VerificationState state, string detail) => Set(run, Read(run) with { State = state, Detail = detail });
    }

    AgentVerificationReport Read(Run run) { lock (gate) { return run.Report; } }

    void Set(Run run, AgentVerificationReport report)
    {
        lock (gate)
        {
            if (run.Report.State != VerificationState.Running) { return; }
            if (run.Report == report) { return; }
            run.Report = report with { CheckedAt = time.GetUtcNow() };
        }
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (gate) { stopping.Cancel(); pending = runs.Values.Select(r => r.Task).ToArray(); }
        await Task.WhenAll(pending);
        stopping.Dispose();
    }
}
