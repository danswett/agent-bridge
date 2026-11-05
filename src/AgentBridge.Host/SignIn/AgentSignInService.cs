using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host.SignIn;

internal sealed class AgentSignInService : IAsyncDisposable
{
    internal sealed record StartResult(string Outcome, string? Flow = null, string? Reason = null);
    internal sealed record DoneResult(string Flow, string Outcome, IReadOnlyList<string> Resumed, IReadOnlyList<string> Continue);

    // Not a record: its generated ToString must never interpolate the code.
    sealed class CodeResult(string flow, AgentKind agent, PrivateSignInCode code)
    {
        public string Flow { get; } = flow;
        public AgentKind Agent { get; } = agent;
        public string UserCode { get; } = code.UserCode;
        public string VerificationUri { get; } = code.VerificationUri;
        public DateTimeOffset ExpiresAt { get; } = code.ExpiresAt;
        public override string ToString() => "Private sign-in event";
    }

    sealed class Flow(SignInContext context, DateTimeOffset at, TimeProvider time, TimeSpan limit)
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public SignInContext Context { get; } = context;
        public DateTimeOffset At { get; } = at;
        public CancellationTokenSource Stop { get; } = new(limit, time);
        public Dictionary<IProtocolChannel, SignInGrant> Participants { get; } = [];
        public Dictionary<string, SignInGrant> Bindings { get; } = new(StringComparer.Ordinal);
        public PrivateSignInCode? Code;
        public DoneResult? Done;
        public DateTimeOffset? EndedAt;
        public Task? Work;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started;
        public bool Ending;
        public bool Silent;
        public bool Automatic;
        public DateTimeOffset? AloneSince;
    }

    sealed record Request(SignInTarget Target, Flow Flow);
    readonly object gate = new();
    readonly ISessionSource source;
    readonly ISignInAuthority authority;
    readonly ISignInContextResolver resolver;
    readonly ISignInRecovery recovery;
    readonly PrivateSignInRunner runner;
    readonly Func<bool> readOnly;
    readonly TimeProvider time;
    readonly Dictionary<string, Flow> active = new(StringComparer.Ordinal);
    readonly Dictionary<(SignInGrant Peer, Guid Request), Request> requests = [];
    readonly ITimer timer;
    int sweeping;
    bool disposed;

    public AgentSignInService(ISessionSource source, ISignInAuthority authority, ISignInContextResolver resolver,
        ISignInRecovery recovery, PrivateSignInRunner runner, Func<bool> readOnly, TimeProvider? time = null)
    {
        this.source = source; this.authority = authority; this.resolver = resolver;
        this.recovery = recovery; this.runner = runner; this.readOnly = readOnly;
        this.time = time ?? TimeProvider.System;
        timer = this.time.CreateTimer(_ => _ = SweepAsync(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    public bool Enabled { get; init; } = AgentSignIn.EnabledByDefault;
    public TimeSpan Limit { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ReattachGrace { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Kept { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan CheckLimit { get; init; } = TimeSpan.FromSeconds(5);
    const int MaxRequests = 128;
    const int MaxFlows = 8;
    const int MaxParticipants = 8;

    SignInGrant? Grant(IProtocolChannel channel)
    {
        if (!Enabled || disposed || !channel.IsOpen || readOnly()) { return null; }
        try { return authority.Current(channel); }
        catch (Exception) { return null; }
    }

    bool Current(Flow flow, IProtocolChannel channel, SignInGrant grant) =>
        !flow.Ending && flow.Done is null && !flow.Stop.IsCancellationRequested && Grant(channel) == grant && Compatible(flow.Context);

    bool Latest(SignInContext context) => context.Failures.Count is > 0 and <= 50
        && context.Failures.All(f => source.Get(f.Id)?.Attention is { Kind: AttentionKind.SignInNeeded } a
            && a.Since == f.Since && a.Agent == context.Target.Agent && a.Provider == "github"
            && a.Account == context.Target.Account);

    bool Compatible(SignInContext context) => context.Failures.All(f => source.Get(f.Id) is { } session
        && (session.Attention is null || session.Attention is { Kind: AttentionKind.SignInNeeded } a
            && a.Since == f.Since && a.Agent == context.Target.Agent && a.Provider == "github" && a.Account == context.Target.Account));

    public bool Offered(IProtocolChannel channel) => Grant(channel) is not null;

    public async Task<bool> TryPrepareSilentRecoveryAsync(AgentKind agent, string? account, CancellationToken ct = default)
    {
        if (!Enabled || disposed || readOnly()) { return false; }
        SignInContext? context;
        try { context = resolver.Resolve(agent, account); }
        catch (Exception) { return false; }
        if (context is null || !Latest(context)) { return false; }
        Flow flow;
        lock (gate)
        {
            if (disposed || readOnly() || active.Count >= MaxFlows || active.ContainsKey(context.Target.Store)) { return false; }
            flow = new(context, time.GetUtcNow(), time, Limit) { Started = true, Silent = true, Automatic = true };
            active.Add(context.Target.Store, flow);
        }
        using var cancel = ct.Register(() => flow.Stop.Cancel());
        var work = PrepareAsync();
        lock (gate) { flow.Work = work; }
        return await work;

        async Task<bool> PrepareAsync()
        {
            try
            {
                await StateAsync(context, flow.Id, RecoveryState.Checking, true, flow.Stop.Token);
                var candidate = await recovery.HasCandidateAsync(context.Target, flow.Stop.Token).WaitAsync(CheckLimit, time, flow.Stop.Token);
                if (!candidate || !CanPrepare(flow)) { await EndAsync(flow, SignInOutcome.Failed); return false; }
                await StateAsync(context, flow.Id, RecoveryState.Pending, ct: flow.Stop.Token);
                await recovery.ResumeAsync(context, () => CanPrepare(flow), flow.Stop.Token).WaitAsync(CheckLimit, time, flow.Stop.Token);
                return true; // Pending, not recovered; no event or notification claims otherwise.
            }
            catch (Exception) { await EndAsync(flow, SignInOutcome.Failed); return false; }
            finally
            {
                lock (gate) { flow.Completed.TrySetResult(); if (flow.Done is not null) { Release(flow); } }
            }
        }
    }

    bool CanPrepare(Flow flow) => Enabled && !disposed && !readOnly() && !flow.Ending
        && !flow.Stop.IsCancellationRequested && Compatible(flow.Context);

    public object Start(IProtocolChannel channel, JsonElement data)
    {
        var grant = Grant(channel);
        if (grant is null) { return Refused(); }
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("request", out var r) || r.ValueKind != JsonValueKind.String
            || !Guid.TryParse(r.GetString(), out var request)
            || !data.TryGetProperty("agent", out var a) || a.ValueKind != JsonValueKind.String || a.GetString() != "copilot")
        {
            return new StartResult("refused", Reason: "a supported agent and request UUID are required");
        }
        string? account = null;
        if (data.TryGetProperty("account", out var accountValue))
        {
            if (accountValue.ValueKind != JsonValueKind.String || accountValue.GetString() is not { Length: > 0 and <= 64 } name)
            {
                return Refused();
            }
            account = name;
        }
        SignInContext? context;
        try { context = resolver.Resolve(AgentKind.Copilot, account); }
        catch (Exception) { return Refused(); }
        if (context is null || context.Target.Agent != AgentKind.Copilot || string.IsNullOrEmpty(context.Target.Store)
            || (account is not null && account != context.Target.Account) || !Latest(context)) { return Refused(); }
        lock (gate)
        {
            Trim();
            if (Grant(channel) != grant) { return Refused(); }
            Flow flow;
            string outcome;
            if (requests.TryGetValue((grant, request), out var prior))
            {
                if (prior.Target != context.Target) { return Refused(); }
                flow = prior.Flow;
                outcome = flow.Done is null ? "busy" : "started";
            }
            else
            {
                if (requests.Count >= MaxRequests) { return new StartResult("refused", Reason: "too many recent sign-in requests"); }
                if (active.TryGetValue(context.Target.Store, out var existing))
                {
                    if (existing.Context.Target != context.Target || existing.Ending || !CanBind(existing, grant)) { return new StartResult("refused", Reason: "this credential store is busy"); }
                    flow = existing;
                    outcome = "busy";
                }
                else
                {
                    if (active.Count >= MaxFlows) { return new StartResult("refused", Reason: "too many sign-ins are pending"); }
                    flow = new(context, time.GetUtcNow(), time, Limit);
                    active.Add(context.Target.Store, flow);
                    outcome = "started";
                }
                requests.Add((grant, request), new(context.Target, flow));
            }
            // Nothing starts or emits a code before the response reaches this requester.
            return new ChannelReply(new StartResult(outcome, flow.Id), _ => AttachAsync(flow, channel, grant));
        }
    }

    static StartResult Refused() => new("refused", Reason: "sign-in is unavailable or not authorized");
    static bool CanBind(Flow flow, SignInGrant grant) => flow.Bindings.TryGetValue(grant.Device, out var original)
        ? original == grant : flow.Bindings.Count < MaxParticipants;

    async Task AttachAsync(Flow flow, IProtocolChannel channel, SignInGrant grant)
    {
        DoneResult? done;
        PrivateSignInCode? code;
        bool start;
        lock (gate)
        {
            if (Grant(channel) != grant) { return; }
            done = flow.Done;
            if (done is null && (!Current(flow, channel, grant) || !CanBind(flow, grant)
                || flow.Participants.Count >= MaxParticipants && !flow.Participants.ContainsKey(channel))) { return; }
            if (done is null)
            {
                flow.Bindings[grant.Device] = grant;
                flow.Participants[channel] = grant;
            }
            flow.AloneSince = null;
            code = flow.Code;
            start = done is null && !flow.Started;
            if (start) { flow.Started = true; }
        }
        if (done is not null) { await SendAsync(channel, grant, AgentSignIn.Done, done, CancellationToken.None); return; }
        if (code is not null && code.ExpiresAt > time.GetUtcNow())
        {
            await SendAsync(channel, grant, AgentSignIn.Code, new CodeResult(flow.Id, flow.Context.Target.Agent, code), flow.Stop.Token,
                () => Current(flow, channel, grant) && ReferenceEquals(flow.Code, code) && code.ExpiresAt > time.GetUtcNow());
        }
        if (start)
        {
            var work = RunAsync(flow);
            lock (gate) { flow.Work = work; }
        }
    }

    async Task StateAsync(SignInContext context, string generation, RecoveryState state, bool begin = false, CancellationToken ct = default)
    {
        foreach (var f in context.Failures) { await source.SetSignInRecoveryAsync(f.Id, f.Since, generation, state, begin, ct); }
    }

    async Task RunAsync(Flow flow)
    {
        try
        {
            if (!HasParticipant(flow) || !Compatible(flow.Context)) { await EndAsync(flow, SignInOutcome.Cancelled); return; }
            await StateAsync(flow.Context, flow.Id, RecoveryState.Checking, true, flow.Stop.Token);
            var candidate = await recovery.HasCandidateAsync(flow.Context.Target, flow.Stop.Token).WaitAsync(CheckLimit, time, flow.Stop.Token);
            if (!HasParticipant(flow) || !Compatible(flow.Context)) { await EndAsync(flow, SignInOutcome.Cancelled); return; }
            if (candidate)
            {
                flow.Silent = true;
                await StateAsync(flow.Context, flow.Id, RecoveryState.Pending, ct: flow.Stop.Token);
                await recovery.ResumeAsync(flow.Context, () => HasParticipant(flow) && CanPrepare(flow), flow.Stop.Token).WaitAsync(CheckLimit, time, flow.Stop.Token);
                // A candidate is never called recovered. Sweep waits for the parser's model-proof gate.
                return;
            }
            await StateAsync(flow.Context, flow.Id, RecoveryState.SigningIn, ct: flow.Stop.Token);
            var result = await runner.RunAsync(flow.Context.Target, (code, _) => ShowAsync(flow, code), flow.Stop.Token);
            if (result == SignInOutcome.SignedIn)
            {
                if (!HasParticipant(flow) || !Compatible(flow.Context)) { await EndAsync(flow, SignInOutcome.Cancelled); return; }
                lock (gate) { flow.Code = null; }
                await StateAsync(flow.Context, flow.Id, RecoveryState.Pending, ct: flow.Stop.Token);
                var resumed = await recovery.ResumeAsync(flow.Context, () => HasParticipant(flow) && CanPrepare(flow), flow.Stop.Token).WaitAsync(CheckLimit, time, flow.Stop.Token);
                await EndAsync(flow, result, resumed);
            }
            else { await EndAsync(flow, result); }
        }
        catch (OperationCanceledException) { await EndAsync(flow, SignInOutcome.Cancelled); }
        catch (Exception) { await EndAsync(flow, SignInOutcome.Failed); }
        finally
        {
            lock (gate)
            {
                flow.Completed.TrySetResult();
                if (flow.Done is not null) { Release(flow); }
            }
        }
    }

    async Task<bool> ShowAsync(Flow flow, PrivateSignInCode code)
    {
        KeyValuePair<IProtocolChannel, SignInGrant>[] recipients;
        lock (gate)
        {
            if (flow.Ending || flow.Stop.IsCancellationRequested || !Compatible(flow.Context)) { return false; }
            flow.Code = code;
            recipients = flow.Participants.Where(p => Current(flow, p.Key, p.Value)).ToArray();
        }
        var sent = false;
        foreach (var (channel, grant) in recipients)
        {
            if (Current(flow, channel, grant))
            {
                sent |= await SendAsync(channel, grant, AgentSignIn.Code, new CodeResult(flow.Id, flow.Context.Target.Agent, code), flow.Stop.Token,
                    () => Current(flow, channel, grant) && ReferenceEquals(flow.Code, code) && code.ExpiresAt > time.GetUtcNow());
            }
        }
        return sent;
    }

    async Task<bool> SendAsync(IProtocolChannel channel, SignInGrant grant, string name, object value, CancellationToken ct, Func<bool>? authorize = null)
    {
        bool Allowed() => Grant(channel) == grant && (authorize?.Invoke() ?? true);
        if (!Allowed()) { return false; }
        try
        {
            var sending = channel is SecureChannel secure
                ? secure.SendPrivateEventAsync(name, value, Allowed, ct) : channel.SendEventAsync(name, value, ct);
            await sending.WaitAsync(CheckLimit, time, ct);
            return Allowed();
        }
        catch (Exception) { return false; }
    }

    bool HasParticipant(Flow flow)
    {
        lock (gate) { return flow.Participants.Any(p => Current(flow, p.Key, p.Value)); }
    }

    public async Task<object> CancelAsync(IProtocolChannel channel, JsonElement data)
    {
        var grant = Grant(channel);
        if (grant is null || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("flow", out var id)
            || id.ValueKind != JsonValueKind.String) { return Refused(); }
        Flow? flow;
        lock (gate)
        {
            flow = active.Values.FirstOrDefault(f => f.Id == id.GetString());
            if (flow is null || !flow.Participants.Any(p => p.Value == grant)) { return Refused(); }
        }
        await EndAsync(flow, SignInOutcome.Cancelled);
        return new { outcome = "cancelled" };
    }

    async Task EndAsync(Flow flow, SignInOutcome outcome, SignInResume? resume = null)
    {
        KeyValuePair<IProtocolChannel, SignInGrant>[] recipients;
        DoneResult done;
        lock (gate)
        {
            if (flow.Ending || flow.Done is not null) { return; }
            flow.Ending = true;
            flow.Code = null;
            flow.Stop.Cancel();
            var ids = flow.Context.Failures.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            done = new(flow.Id, outcome switch
            {
                SignInOutcome.SignedIn => "signedIn", SignInOutcome.Expired => "expired",
                SignInOutcome.Cancelled => "cancelled", SignInOutcome.Refused => "refused", _ => "failed",
            }, resume?.Resumed.Where(ids.Contains).Distinct().Take(50).ToArray() ?? [],
                resume?.Continue.Where(ids.Contains).Distinct().Take(50).ToArray() ?? []);
            flow.Done = done;
            flow.EndedAt = time.GetUtcNow();
            if (!flow.Started || flow.Completed.Task.IsCompleted) { Release(flow); }
            recipients = flow.Participants.ToArray();
            flow.Participants.Clear();
            flow.Bindings.Clear();
        }
        if (outcome != SignInOutcome.SignedIn) { await StateAsync(flow.Context, flow.Id, RecoveryState.Failed); }
        foreach (var (channel, grant) in recipients) { await SendAsync(channel, grant, AgentSignIn.Done, done, CancellationToken.None); }
    }

    void Release(Flow flow)
    {
        if (active.TryGetValue(flow.Context.Target.Store, out var current) && ReferenceEquals(current, flow))
        {
            active.Remove(flow.Context.Target.Store);
            if (flow.Automatic) { flow.Stop.Dispose(); }
        }
    }

    internal async Task SweepAsync()
    {
        if (Interlocked.Exchange(ref sweeping, 1) != 0) { return; }
        try
        {
            Flow[] flows;
            lock (gate) { flows = active.Values.ToArray(); Trim(); }
            foreach (var flow in flows)
            {
                if (flow.Stop.IsCancellationRequested || flow.Code is { } code && code.ExpiresAt <= time.GetUtcNow())
                {
                    await EndAsync(flow, SignInOutcome.Expired); continue;
                }
                if (!Enabled || disposed || readOnly()) { await EndAsync(flow, SignInOutcome.Cancelled); continue; }
                bool revoked;
                bool alone;
                lock (gate)
                {
                    revoked = flow.Bindings.Values.Any(g => !authority.StillCurrent(g))
                        || flow.Participants.Any(p => p.Key.IsOpen && Grant(p.Key) != p.Value);
                    foreach (var p in flow.Participants.Keys.Where(c => !c.IsOpen).ToArray()) { flow.Participants.Remove(p); }
                    alone = !flow.Automatic && flow.Participants.Count == 0;
                    flow.AloneSince = alone ? flow.AloneSince ?? time.GetUtcNow() : null;
                }
                if (revoked || alone && flow.AloneSince is { } since && time.GetUtcNow() - since >= ReattachGrace)
                {
                    await EndAsync(flow, SignInOutcome.Cancelled); continue;
                }
                if (flow.Silent && flow.Context.Failures.All(f => source.Get(f.Id) is { Attention: null }))
                {
                    await EndAsync(flow, SignInOutcome.SignedIn); continue;
                }
                if (!Compatible(flow.Context)) { await EndAsync(flow, SignInOutcome.Failed); }
            }
        }
        finally { Volatile.Write(ref sweeping, 0); }
    }

    void Trim()
    {
        foreach (var key in requests.Where(p => p.Value.Flow.EndedAt is { } end && time.GetUtcNow() - end > Kept
            && (!p.Value.Flow.Started || p.Value.Flow.Completed.Task.IsCompleted)).Select(p => p.Key).ToArray())
        {
            var flow = requests[key].Flow;
            requests.Remove(key);
            if (!requests.Values.Any(r => ReferenceEquals(r.Flow, flow))) { flow.Stop.Dispose(); flow.Participants.Clear(); }
        }
    }

    internal int CodesHeld { get { lock (gate) { return active.Values.Count(f => f.Code is not null); } } }
    internal int ParticipantsHeld { get { lock (gate) { return requests.Values.Select(r => r.Flow).Distinct().Sum(f => f.Participants.Count); } } }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        timer.Dispose();
        Flow[] flows;
        lock (gate) { flows = active.Values.ToArray(); }
        foreach (var flow in flows) { await EndAsync(flow, SignInOutcome.Cancelled); }
        foreach (var flow in flows)
        {
            if (flow.Work is { } work)
            {
                try { await work.WaitAsync(CheckLimit, time); } catch (Exception) { }
            }
            flow.Stop.Dispose();
        }
        lock (gate) { requests.Clear(); active.Clear(); }
    }
}
