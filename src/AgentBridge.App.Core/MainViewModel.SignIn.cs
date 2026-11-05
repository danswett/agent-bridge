using System.Collections.ObjectModel;
using System.Text.Json;

namespace AgentBridge.App.Core;

// The sign-in banners and sheet (#800, SignInBanners) over the sessions list: one banner per
// machine, agent and account, from every machine whose rows can be acted on, whatever the picker
// shows, as Needs you counts them. Kept by key, so a button with focus is not replaced under it.
//
// This device's own flows: started on a person's press (signin.start, never otherwise), their code
// from signin.code, how they ended from signin.done. A flow id this device didn't start or join is
// not its to show. The code is in the sheet's model while the sheet is open, and nowhere else.
// Signing in isn't recovery: after signin.done the banner and rows follow the host, which says
// pending until its clear gate passes and then clears the sessions (ui.md §6.13).
public sealed partial class MainViewModel
{
    public ObservableCollection<SignInBannerViewModel> SignIns { get; } = [];
    public bool HasSignIns => SignIns.Count > 0;

    // The sheet open, or null. The app shows it as a dialog while it is set.
    public SignInSheetViewModel? SignInSheet { get => signInSheet; private set => Set(ref signInSheet, value); }
    SignInSheetViewModel? signInSheet;

    // A polite announcement for the app to make: "Copilot on DEV-VM1 needs sign-in". Never the code.
    public event Action<string>? Announce;
    // The sheet closed, by its banner's key: focus goes back to that banner. True when this close is
    // a person here signing in: focus goes to its status line, never back to Sign in. Decided for
    // each close, so a later attempt cancelled or failed goes back to its button.
    public event Action<string, bool>? SignInSheetClosed;
    // A banner about to be drawn anew for its new state (needed to confirming, say), by its key, while
    // its old one is still shown: focus inside the old one moves to the new one.
    public event Action<string>? SignInRedrawing;

    // How long a signin.code or signin.done is held for a start whose result hasn't come yet.
    static readonly TimeSpan EarlyHeld = TimeSpan.FromSeconds(10);

    readonly Dictionary<string, SignInBanners.Flow> signInFlows = new(StringComparer.Ordinal);
    // Cancels sent for a banner's flows and not yet finished: its next start waits for them, so it
    // can't join the flow being stopped (busy) and then end with it.
    readonly Dictionary<string, Task> signInCancelling = new(StringComparer.Ordinal);
    readonly Dictionary<string, SignInBanners.Resolved> signInResolved = new(StringComparer.Ordinal);
    // Each banner as last drawn needing sign-in or confirming, with every session it has held up
    // since it came: they confirm one by one, and one that goes without its sessions still needing
    // sign-in has recovered.
    readonly Dictionary<string, (SignInBanners.Banner Banner, HashSet<string> All)> signInLast = new(StringComparer.Ordinal);
    // Where a person here signed in (signin.done signedIn), by banner: once the host clears its
    // sessions, that reads "Signed in · N sessions recovered" rather than "Recovered by itself".
    readonly HashSet<string> signedInHere = new(StringComparer.Ordinal);
    // What the banners last said aloud, by key: each says so once a state (§9.1 A6).
    readonly Dictionary<string, string> signInSaid = new(StringComparer.Ordinal);
    // signin.code and signin.done that came before the start's result named their flow: held as read
    // (never the raw event, whose other fields could be anything), only while a start is under way,
    // and for EarlyHeld (or until that start settles) at most.
    readonly List<(string Machine, DeviceCode? Code, SignInDone? Done, DateTimeOffset At)> signInEarly = [];

    // How many codes this app holds, in flows and held early: none survives a cancel, an expiry or
    // its machine going away. For the tests.
    internal int SignInCodesHeld => signInFlows.Values.Count(f => f.Code is { Refused: false }) + signInEarly.Count(e => e.Code is { Refused: false })
        + signInLast.Values.Count(l => l.Banner.Code is not null || l.Banner.Flow?.Code is not null);

    // A machine whose sign-ins can be shown and acted on: current (#269), and connected with its
    // whole snapshot; this machine is always current, but its pipe can drop or its list be replaced.
    // Banners and Needs you both go by it.
    // After a gap in its events too, until the replacement snapshot is whole: a signin.done the gap
    // lost would otherwise leave a finished flow's code on the sheet.
    static bool SignInLive(Machine m) => m.Current && m.Host.State == LinkState.Connected && m.Host.Sessions.Complete && m.Confirmed;

    IEnumerable<SignInBanners.Source> SignInSources() => machines
        // A machine away (#269): what it last said may no longer be so, and nothing can be sent. This
        // machine too: always current, but its pipe can drop, and then nothing can be sent either.
        .Where(SignInLive)
        .Select(m => new SignInBanners.Source(m.Key, m.Name,
            [.. owner.Where(p => p.Value == m).Select(p => p.Key.Session)],
            m.Host.Can(AuthState.Capability) && m.Host.Hello?.Machine.ReadOnly is null));

    void SyncSignIns()
    {
        var now = Time.GetUtcNow();
        var sources = SignInSources().ToList();
        var current = sources.Select(s => s.MachineKey).ToHashSet(StringComparer.Ordinal);
        // A machine removed (signed out, another account): nothing of it stays on screen, not even
        // how its last sign-in ended. One merely away keeps what it last said until it is back.
        var owned = machines.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (k, r) in signInResolved.ToList()) { if (!owned.Contains(r.MachineKey)) { signInResolved.Remove(k); } }
        foreach (var (k, (b, _)) in signInLast.ToList())
        {
            if (owned.Contains(b.MachineKey)) { continue; }
            signInLast.Remove(k);
            signedInHere.Remove(k);
        }
        // No code outlives its use: one that has expired is dropped (the sheet says so), and a flow
        // under way on a machine gone away is let go with its code, so that once it is back a press
        // joins it again (busy) rather than finding a banner with nothing to press.
        foreach (var (k, f) in signInFlows.ToList())
        {
            if (f.Active && !current.Contains(k.Split('\n')[0])) { signInFlows.Remove(k); }
            else if (f.Code is { Refused: false } c && AuthState.Expired(c, now)) { signInFlows[k] = f with { Code = null, Outcome = "expired" }; }
        }
        LapseHeld(now);
        signInEarly.RemoveAll(e => !current.Contains(e.Machine) || !StillHeld(e, now));
        var first = SignInBanners.For(sources, signInFlows, signInResolved, now);
        var live = first.Select(b => b.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var b in first.Where(b => b.State is "needed" or "confirming"))
        {
            // A new episode: the last one's "recovered" is over, and this one gets its own when it ends.
            signInResolved.Remove(b.Key);
            var all = signInLast.TryGetValue(b.Key, out var was) ? was.All : new HashSet<string>(StringComparer.Ordinal);
            foreach (var (id, _) in b.Sessions) { all.Add(id); }
            // Its words and sessions only: never its flow, nor so its code.
            signInLast[b.Key] = (b with { Flow = null, Code = null }, all);
        }
        foreach (var (key, (b, all)) in signInLast.ToList())
        {
            if (live.Contains(key)) { continue; }
            // A machine away, or not shown: what it last said stands until it is back.
            if (!current.Contains(b.MachineKey)) { continue; }
            // A flow of this device's under way: its signin.done says how it went.
            if (signInFlows.GetValueOrDefault(key) is { Active: true }) { continue; }
            signInLast.Remove(key);
            var here = signedInHere.Remove(key);
            if (signInResolved.ContainsKey(key) || MachineOf(b.MachineKey) is not { } m) { continue; }
            // Recovered only by the sessions' own word: still there, and cleared by the host (or resumed).
            var cleared = all.Select(m.Host.Sessions.Get).Count(s => s is not null && AuthState.Of(s) is var at && !AuthState.NeedsSignIn(at) && !AuthState.Confirming(at));
            if (cleared > 0) { Resolve(b, here ? "signedIn" : "recovered", cleared); }
        }
        // An ended flow whose banner has gone keeps nothing.
        foreach (var (k, f) in signInFlows.ToList()) { if (!f.Active && !live.Contains(k)) { signInFlows.Remove(k); } }
        var want = SignInBanners.For(sources, signInFlows, signInResolved, now);
        var shown = SignIns.ToDictionary(b => b.Key, StringComparer.Ordinal);
        var keep = new List<SignInBannerViewModel>();
        var redrawn = new List<string>();
        foreach (var b in want)
        {
            // A banner that changes between needed, confirming and resolved is a new one: its button goes.
            var kind = b.State is "needed" or "confirming" ? b.State : "resolved";
            if (shown.TryGetValue(b.Key, out var vm) && vm.Kind == kind) { vm.Update(b); }
            else
            {
                if (shown.ContainsKey(b.Key)) { redrawn.Add(b.Key); }
                vm = new SignInBannerViewModel(b, p => _ = StartSignInAsync(p), (machine, id) => Open(machine, id));
            }
            // Busy while this banner's current start is in flight: not one cancelled, whose late result
            // would otherwise hold a new press back.
            vm.Busy = b.Flow?.State == "starting";
            keep.Add(vm);
            // Politely, once a state: needed, each way an attempt can end, confirming, signed in or recovered.
            var (said, words) = SignInBanners.Said(b);
            if (!signInSaid.TryGetValue(b.Key, out var was) || was != said)
            {
                signInSaid[b.Key] = said;
                Announce?.Invoke(words);
            }
        }
        foreach (var key in redrawn) { SignInRedrawing?.Invoke(key); }
        var had = SignIns.Count;
        var kept = keep.ToHashSet();
        foreach (var gone in SignIns.Where(v => !kept.Contains(v)).ToList())
        {
            SignIns.Remove(gone);
            if (!want.Any(b => b.Key == gone.Key)) { signInSaid.Remove(gone.Key); }
        }
        for (var i = 0; i < keep.Count; i++)
        {
            if (i < SignIns.Count && SignIns[i] == keep[i]) { continue; }
            var at = SignIns.IndexOf(keep[i]);
            if (at < 0) { SignIns.Insert(i, keep[i]); }
            else { SignIns.Move(at, i); }
        }
        foreach (var (k, r) in signInResolved.ToList()) { if (now - r.At >= AuthState.ResolvedShown) { signInResolved.Remove(k); } }
        if (had != SignIns.Count) { Raise(nameof(HasSignIns)); }
        if (SignInSheet is { } sheet)
        {
            if (want.FirstOrDefault(b => b.Key == sheet.Key && b.State == "needed") is { } still) { sheet.Update(still); }
            else
            {
                // Its machine went away (#269) or stopped being shown: the flow is let go here, so that
                // once it is back a press joins it again (busy) rather than finding a banner with nothing
                // to press.
                if (signInFlows.GetValueOrDefault(sheet.Key) is { Active: true }) { signInFlows.Remove(sheet.Key); }
                DropMachineHeld(sheet.Key.Split('\n')[0]);
                CloseSignInSheet(sheet.Key);
            }
        }
    }

    void Resolve(SignInBanners.Banner b, string kind, int count)
    {
        signInResolved[b.Key] = new SignInBanners.Resolved(kind, count, Time.GetUtcNow(), b.Agent, b.Account, b.MachineKey, b.Machine);
        signInFlows.Remove(b.Key);
        signInLast.Remove(b.Key);
    }

    // Only a person's press sends it, saying only which agent and account: the machine runs the
    // agent's own sign-in, and the person enters the code at github.com themselves.
    internal async Task StartSignInAsync(SignInBannerViewModel vm)
    {
        var b = vm.Banner;
        if (signInFlows.GetValueOrDefault(b.Key) is { State: "starting" }) { return; }
        if (MachineOf(b.MachineKey) is not { } m)
        {
            signInFlows[b.Key] = new SignInBanners.Flow("done", Problem: $"{b.Machine} can\u2019t be reached now");
            SyncSignIns();
            return;
        }
        // This press's own: a result that comes back after a Cancel, or after a later press on the
        // same banner, is not this attempt's to apply.
        var attempt = Guid.NewGuid().ToString();
        bool Mine() => signInFlows.GetValueOrDefault(b.Key)?.Attempt == attempt;
        signInFlows[b.Key] = new SignInBanners.Flow("starting", Attempt: attempt);
        vm.Busy = true;
        OpenSignInSheet(b.Key);
        if (signInCancelling.ContainsKey(b.Key))
        {
            await AwaitCancelsAsync(b.Key);
            // Cancelled, or pressed again, while it waited: not this attempt's to start.
            if (!Mine()) { return; }
            // The host stopped offering this device sign-in while it waited: nothing is sent, and the
            // sheet goes, rather than saying "Starting…" for a start that never comes.
            if (SignIns.FirstOrDefault(v => v.Key == b.Key) is not { Banner.CanSignIn: true })
            {
                signInFlows.Remove(b.Key);
                SyncSignIns();
                CloseSignInSheet(b.Key);
                return;
            }
        }
        StartResult r;
        try
        {
            object data = b.Account is { } account
                ? new { agent = AuthState.WireName(b.Agent), account, request = attempt }
                : new { agent = AuthState.WireName(b.Agent), request = attempt };
            r = AuthState.ReadStartResult(await m.Host.CallAsync(AuthState.Start, data));
        }
        // The machine's own words are not shown: they could be anything.
        catch (Exception)
        {
            if (!Mine()) { return; }
            DropOrphans(b.Key, m, signInFlows.GetValueOrDefault(b.Key)?.Orphans, null);
            signInFlows[b.Key] = new SignInBanners.Flow("done", Problem: "Couldn\u2019t start sign-in. Try again.");
            DropMachineHeld(b.MachineKey);
            SyncSignIns();
            CloseSignInSheet(b.Key);
            // Said, politely: the sheet went, and the line that says why isn't read on its own.
            Announce?.Invoke($"Couldn\u2019t start sign-in for {b.Subject}. Try again.");
            return;
        }
        if (!Mine())
        {
            // Cancelled while it started: the flow it started is cancelled too, unless a later press
            // here joined that same flow. While that press's own result hasn't come, it can't be known:
            // the flow and what came for it wait for that result (DropOrphans).
            // Only a flow it started: one it joined (busy) is another device's sign-in.
            if (r.Flow is not { } started || r.Outcome != "started") { return; }
            var current = signInFlows.GetValueOrDefault(b.Key);
            if (current is { State: "starting" }) { signInFlows[b.Key] = current with { Orphans = [.. current.Orphans ?? [], started] }; return; }
            if (current?.Id != started)
            {
                CancelInBackground(b.Key, m, started);
                DropFlowHeld(m.Key, started);
            }
            return;
        }
        DropOrphans(b.Key, m, signInFlows.GetValueOrDefault(b.Key)?.Orphans, r.Flow);
        if (r.Outcome is "started" or "busy")
        {
            signInFlows[b.Key] = new SignInBanners.Flow("pending", r.Flow, Attempt: attempt, Joined: r.Outcome == "busy");
            // What came for it before this result did.
            var now = Time.GetUtcNow();
            LapseHeld(now);
            var early = signInEarly.Where(e => e.Machine == b.MachineKey && FlowOf(e.Code, e.Done) == r.Flow).ToList();
            signInEarly.RemoveAll(e => early.Contains(e) || !StillHeld(e, now));
            foreach (var e in early) { Apply(e.Machine, e.Code, e.Done); }
            // What is left came for a flow no start here can claim now (a cancelled one's late code):
            // dropped, unless another start on this machine is still under way.
            DropMachineHeld(b.MachineKey);
            SyncSignIns();
            return;
        }
        if (r.Flow is { } ended) { DropFlowHeld(b.MachineKey, ended); }
        // Refused (any outcome the protocol doesn't define reads so). "Sign in at the machine" comes
        // only from the host's signIn: machine, never from a start's result.
        signInFlows[b.Key] = new SignInBanners.Flow("done", Outcome: "refused", Reason: r.Reason);
        // No flow of its own to claim it: what came early on this machine goes now, not after EarlyHeld,
        // unless another start there is still under way.
        DropMachineHeld(b.MachineKey);
        // Drawn first, so the banner's button is back for focus to return to.
        SyncSignIns();
        CloseSignInSheet(b.Key);
    }

    // Flows abandoned starts here began, once the start that followed them says which it is in: each
    // other one is cancelled at the machine, with whatever came for it.
    void DropOrphans(string key, Machine m, IReadOnlyList<string>? orphans, string? joined)
    {
        foreach (var o in orphans ?? [])
        {
            if (o == joined) { continue; }
            CancelInBackground(key, m, o);
            DropFlowHeld(m.Key, o);
        }
    }

    // What came early for one machine's flow: never another machine's, whose flow ids are its own.
    void DropFlowHeld(string machine, string flow) => signInEarly.RemoveAll(e => e.Machine == machine && FlowOf(e.Code, e.Done) == flow);

    // What came early for a machine, once no start there is under way to claim it.
    bool StartingOn(string machine) => signInFlows.Any(p => p.Value.State == "starting" && p.Key.StartsWith(machine + "\n", StringComparison.Ordinal));

    // Held for EarlyHeld, and past it while a start on its machine is still under way (its result can
    // take longer). Cancel, failure and the machine going away drop it at once.
    bool StillHeld((string Machine, DeviceCode? Code, SignInDone? Done, DateTimeOffset At) e, DateTimeOffset now) =>
        now - e.At < EarlyHeld || StartingOn(e.Machine);

    // A held code that has run out keeps only that it did, never the code: replayed when its start
    // settles, the sheet says "Code expired" and offers Try again rather than waiting on "Starting…".
    void LapseHeld(DateTimeOffset now)
    {
        for (var i = 0; i < signInEarly.Count; i++)
        {
            if (signInEarly[i].Code is { Refused: false, ExpiresAt: { } at } c && AuthState.Expired(c, now))
            {
                signInEarly[i] = signInEarly[i] with { Code = DeviceCode.LapsedFor(c.Flow, at) };
            }
        }
    }

    void DropMachineHeld(string machine)
    {
        if (StartingOn(machine)) { return; }
        signInEarly.RemoveAll(e => e.Machine == machine);
    }

    // The sheet's Cancel, or Escape.
    public void CancelSignIn()
    {
        if (SignInSheet is not { } sheet) { return; }
        var key = sheet.Key;
        var flow = signInFlows.GetValueOrDefault(key);
        signInFlows.Remove(key);
        if (flow?.Orphans is { Count: > 0 } && MachineOf(key.Split('\n')[0]) is { } owner) { DropOrphans(key, owner, flow.Orphans, null); }
        // Nothing held for it: its flow's, and its machine's unless another start there is under way.
        var machine = key.Split('\n')[0];
        if (flow?.Id is { } id0) { DropFlowHeld(machine, id0); }
        DropMachineHeld(machine);
        // Drawn first, so the banner's button is back for focus to return to.
        SyncSignIns();
        CloseSignInSheet(key);
        if (flow is { Id: { } id, Joined: false } && MachineOf(key.Split('\n')[0]) is { } m) { CancelInBackground(key, m, id); }
    }

    // Until no cancel for the banner is still under way. Each stays registered until it has finished,
    // so a press after this attempt is cancelled mid-wait waits for it too.
    async Task AwaitCancelsAsync(string key)
    {
        while (signInCancelling.GetValueOrDefault(key) is { } pending)
        {
            await pending;
            if (signInCancelling.GetValueOrDefault(key) == pending) { signInCancelling.Remove(key); }
        }
    }

    // A cancel sent for a banner's flow, which its next start waits for (signInCancelling).
    void CancelInBackground(string key, Machine m, string flow)
    {
        var before = signInCancelling.GetValueOrDefault(key);
        signInCancelling[key] = After(before, CancelFlowAsync(m, flow));

        static async Task After(Task? first, Task then)
        {
            if (first is not null) { await first; }
            await then;
        }
    }

    // The sheet's Try again, for a code that couldn't be shown or has expired: a new request, the one
    // before cancelled at the machine.
    public void RetrySignIn() => _ = RetrySignInAsync();

    // The flow before is cancelled at the machine first, and only then a new request sent, so the new
    // one can't join the flow being stopped (busy) and end with it. Guarded by an attempt: a Cancel,
    // or the host no longer offering sign-in, while it waits starts nothing.
    internal async Task RetrySignInAsync()
    {
        if (SignInSheet is not { CanRetry: true } sheet) { return; }
        var key = sheet.Key;
        var flow = signInFlows.GetValueOrDefault(key);
        var attempt = Guid.NewGuid().ToString();
        signInFlows[key] = new SignInBanners.Flow("starting", Attempt: attempt);
        SyncSignIns();
        if (flow is { Id: { } id, Joined: false } && MachineOf(key.Split('\n')[0]) is { } m) { CancelInBackground(key, m, id); }
        await AwaitCancelsAsync(key);
        if (signInFlows.GetValueOrDefault(key)?.Attempt != attempt) { return; }
        signInFlows.Remove(key);
        if (SignInSheet?.Key != key || SignIns.FirstOrDefault(v => v.Key == key) is not { } vm || !vm.Banner.CanSignIn)
        {
            // Nothing will start: the sheet goes rather than saying "Starting…", focus to the banner.
            SyncSignIns();
            CloseSignInSheet(key);
            return;
        }
        await StartSignInAsync(vm);
    }

    static async Task CancelFlowAsync(Machine m, string flow)
    {
        // The code expires with the CLI's own flow if this doesn't reach the machine.
        try { await m.Host.CallAsync(AuthState.Cancel, new { flow }); }
        catch (Exception) { }
    }

    static string? FlowOf(DeviceCode? code, SignInDone? done) => code?.Flow ?? done?.Flow;

    // signin.code or signin.done from a machine (HostConnection.AgentSignIn): read at once, so nothing
    // else of the event is kept.
    internal void OnAgentSignIn(string machineKey, string ev, JsonElement data)
    {
        var code = ev == AuthState.CodeEvent ? AuthState.ReadCode(data) : null;
        var done = ev == AuthState.DoneEvent ? AuthState.ReadDone(data) : null;
        if (code is null && done is null) { return; }
        Apply(machineKey, code, done);
        SyncSignIns();
    }

    void Apply(string machineKey, DeviceCode? code, SignInDone? done)
    {
        var flowId = FlowOf(code, done);
        var key = signInFlows.FirstOrDefault(p => p.Key.StartsWith(machineKey + "\n", StringComparison.Ordinal) && p.Value.Id == flowId).Key;
        if (key is null)
        {
            // Held a moment for a start whose result hasn't come yet; never shown unless it names its flow.
            if (StartingOn(machineKey)) { signInEarly.Add((machineKey, code, done, Time.GetUtcNow())); }
            return;
        }
        if (code is { Lapsed: true })
        {
            // A code that ran out while its start's result was on the way: the sheet says so, Try again.
            signInFlows[key] = signInFlows[key] with { State = "pending", Code = null, Outcome = "expired" };
            return;
        }
        if (code is not null)
        {
            signInFlows[key] = signInFlows[key] with { State = "pending", Code = code, Outcome = null };
            return;
        }
        if (done!.Outcome == "signedIn")
        {
            // Signing in isn't recovery (ui.md §6.13): the banner and rows follow the host, which says
            // pending (waiting for the session to confirm) and clears the sessions only once the model
            // answers. All this device keeps is that a person here signed in.
            signInFlows.Remove(key);
            signedInHere.Add(key);
        }
        else if (done.Outcome == "cancelled") { signInFlows.Remove(key); }
        else { signInFlows[key] = new SignInBanners.Flow("done", Outcome: done.Outcome, Reason: done.Reason); }
        SyncSignIns();
        CloseSignInSheet(key, signedIn: done.Outcome == "signedIn");
    }

    void OpenSignInSheet(string key)
    {
        var b = SignInBanners.For(SignInSources(), signInFlows, signInResolved, Time.GetUtcNow()).FirstOrDefault(x => x.Key == key);
        if (b is null) { return; }
        if (SignInSheet is { } open && open.Key == key) { open.Update(b); return; }
        SignInSheet = new SignInSheetViewModel(b);
    }

    // key: close it only if it is that banner's sheet (a late result never closes another's).
    void CloseSignInSheet(string key, bool signedIn = false)
    {
        if (SignInSheet is not { } sheet || sheet.Key != key) { return; }
        SignInSheet = null;
        SignInSheetClosed?.Invoke(sheet.Key, signedIn);
    }
}
