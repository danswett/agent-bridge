using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// Where a session's history stands (ui.md H5). Idle: nothing is loading and older turns may remain
// (or none has been asked for yet). StartReached: the host serves nothing older. NoHistory: this
// machine sends none (no capability, or `unavailable`). Gone: the machine no longer lists the session.
public enum HistoryLoad { Idle, Loading, Failed, NoHistory, StartReached, Gone }

// One session's history as the app holds it (docs/protocol/v1-history.md, H0-H6): the turns the
// host served, keyed by their stable id so a turn that arrives twice (a page, a newer page, the
// live exchange) is held once, and the cursor for the page before the oldest. Data only: no view
// reads it yet, and nothing here touches the UI thread, so a caller on any thread may use it. One
// load runs at a time; a call made while one runs does nothing.
public sealed class SessionHistory(HostConnection host, string sessionId)
{
    readonly object gate = new();
    readonly List<HistoryTurn> held = [];
    // Pages read past turns already held after a stale cursor: the held turns are at most Kept, a page is
    // at least one turn, so this bounds a host that keeps answering with pages of the same turns.
    const int MaxRestartPages = HistoryLimits.Kept / HistoryLimits.DefaultTurns + 5;
    string? before;
    bool loadedNewest;
    // The host said "unavailable": it is not asked again. A machine only lacking the capability is asked again, since hello may not have listed it yet.
    bool unavailable;

    // Raised on the thread that finished a load or added a live turn, once the change is held.
    public event Action? Changed;

    public HistoryLoad State { get; private set; }
    // The turns held, oldest first.
    public IReadOnlyList<HistoryTurn> Turns { get { lock (gate) { return [.. held]; } } }
    // Whether an older page can be asked for: the newest has been read and its cursor says more.
    public bool HasOlder { get { lock (gate) { return loadedNewest && before is not null && State is not (HistoryLoad.NoHistory or HistoryLoad.Gone); } } }
    // Why the host has none, when it said (`unavailable`), as secondary text.
    public string? Reason { get; private set; }
    // How the start of the history reads once reached: "Start of session · date" or, when Limited, "Start of available history".
    public HistoryStart? Start { get; private set; }
    // Turns at the newest end were let go to stay within HistoryLimits.Kept; scrolling forward would need them again.
    public bool NewestDropped { get; private set; }
    // Turns at the oldest end were let go for a live or newest turn past HistoryLimits.Kept; nothing older is asked for after.
    public bool OldestDropped { get; private set; }
    // Whether a turn was read in part or not at all (a page's `partial`), for a line above it (H5).
    public bool AnyPartial { get { lock (gate) { return held.Any(t => t.Unreadable == true || t.Truncated == true); } } }

    // Opens the session: the newest page. Nothing is sent before the host's hello (its capabilities are not known
    // yet, so a later call, e.g. on the connection's Changed, asks), nor without the capability once hello is read.
    public async Task LoadNewestAsync(CancellationToken ct = default)
    {
        if (!Begin()) { return; }
        await RunAsync(newest: true, ct);
    }

    // The page before the oldest turn held, the same call that retries a failed one: the first
    // page again while none has been read.
    public async Task LoadOlderAsync(CancellationToken ct = default)
    {
        if (!loadedNewest) { await LoadNewestAsync(ct); return; }
        if (!HasOlder && State != HistoryLoad.Failed) { return; }
        if (!Begin()) { return; }
        await RunAsync(newest: false, ct);
    }

    // The live exchange's turn: held once whatever else has brought it. A turn already held stays
    // as it is (the live session says what is newer of it); a new one goes after every other,
    // never between (v1-history.md, Live turns and history 3). Returns whether it was new.
    public bool AddLive(HistoryTurn turn)
    {
        lock (gate)
        {
            if (held.Any(t => t.Id == turn.Id)) { return false; }
            held.Add(turn);
            TrimOldest();
        }
        Changed?.Invoke();
        return true;
    }

    bool Begin()
    {
        var noCapability = false;
        lock (gate)
        {
            if (State is HistoryLoad.Loading or HistoryLoad.Gone or HistoryLoad.StartReached || (State == HistoryLoad.NoHistory && unavailable)) { return false; }
            if (host.Hello is null) { return false; }
            if (host.Can(HistoryProtocol.Capability)) { State = HistoryLoad.Loading; Reason = null; return true; }
            noCapability = State != HistoryLoad.NoHistory;
            State = HistoryLoad.NoHistory;
        }
        if (noCapability) { Changed?.Invoke(); }
        return false;
    }

    void Settle(HistoryLoad state, string? reason = null, bool unavailable = false)
    {
        lock (gate)
        {
            State = state;
            Reason = reason;
            this.unavailable = unavailable;
        }
        Changed?.Invoke();
    }

    // Keeps at most HistoryLimits.Kept turns by letting the oldest go (the live end is what is read). Their cursor
    // went with them, so nothing older is asked for after. Call with the gate held.
    void TrimOldest()
    {
        var over = held.Count - HistoryLimits.Kept;
        if (over <= 0) { return; }
        held.RemoveRange(0, over);
        before = null;
        OldestDropped = true;
    }

    async Task RunAsync(bool newest, CancellationToken ct)
    {
        Changed?.Invoke();
        try
        {
            var asked = newest ? null : before;
            var restarted = false;
            var pages = 0;
            while (true)
            {
                var result = await host.ReadAsync<HistoryResult>(HistoryProtocol.Op, new HistoryRequest { Id = sessionId, Before = asked }, ct: ct);
                if (result.Missing == true) { Settle(HistoryLoad.Gone); return; }
                if (result.Unavailable is { } why) { Settle(HistoryLoad.NoHistory, why, unavailable: true); return; }
                if (result.Stale == true)
                {
                    // The cursor can't be continued from: keep what is held and read the newest page
                    // again, merged by id, once (a second answer like it is a failure, not a loop).
                    if (asked is null || restarted) { Settle(HistoryLoad.Failed); return; }
                    restarted = true;
                    asked = null;
                    continue;
                }
                if (result.Turns is null) { Settle(HistoryLoad.Failed); return; }

                var added = Merge(result, atNewestEnd: asked is null);
                lock (gate)
                {
                    loadedNewest = true;
                    if (!OldestDropped) { before = result.Before; }
                    if (result.Before is null) { Start = result.Start; }
                }
                if (result.Before is null) { Settle(HistoryLoad.StartReached); return; }
                // After a restart the newest page's cursor leads back over turns already held: keep
                // reading until a page brings one that is not, so the call shows something older.
                if (restarted && added == 0 && ++pages < MaxRestartPages)
                {
                    asked = result.Before;
                    continue;
                }
                Settle(HistoryLoad.Idle);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Settle(HistoryLoad.Idle);
            throw;
        }
        catch (Exception e) when (e is HostErrorException or HostNotRunningException or IOException or TimeoutException or JsonException or OperationCanceledException)
        {
            // What is held stays (H5): the failure is the line above it, and Try again asks again.
            Settle(HistoryLoad.Failed);
        }
    }

    // Puts the page's turns among those held, each id once: a turn held is replaced where it is;
    // the rest go in by time (v1-history.md
    // Live turns and history 3 to 6). An older page that overfills the cap lets the newest go (H6).
    // Returns how many turns were new.
    int Merge(HistoryResult page, bool atNewestEnd)
    {
        var added = 0;
        lock (gate)
        {
            var fresh = new List<HistoryTurn>();
            foreach (var turn in page.Turns!)
            {
                var at = held.FindIndex(t => t.Id == turn.Id);
                if (at >= 0) { held[at] = turn; }
                else if (fresh.All(t => t.Id != turn.Id)) { fresh.Add(turn); }
            }
            added = fresh.Count;
            // By time, not at an end: a live turn already held is newer than the newest page's older turns, and a
            // newest page that skipped a gap leaves held turns above the older page that comes next.
            foreach (var turn in fresh)
            {
                var after = held.FindIndex(t => t.At > turn.At);
                if (after < 0) { held.Add(turn); } else { held.Insert(after, turn); }
            }
            if (atNewestEnd) { TrimOldest(); }
            else
            {
                var over = held.Count - HistoryLimits.Kept;
                if (over > 0)
                {
                    held.RemoveRange(held.Count - over, over);
                    NewestDropped = true;
                }
            }
        }
        return added;
    }
}
