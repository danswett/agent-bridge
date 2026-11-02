using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

// Home Assistant's own ban (http.ip_ban_enabled, login_attempts_threshold, 10 by default) counts
// every request it answers 401, and every WebSocket auth_invalid, per client address. Only a
// WebSocket auth_ok resets the count; a successful REST request does not. Past the threshold it
// writes the address to /config/ip_bans.yaml and answers 403 to everything from it, with any token,
// until that entry is removed and Home Assistant restarted.
//
// On 2026-11-02 a just-migrated machine's connector started with a token Home Assistant rejected
// and tried mqtt.publish again every 30 s. Ten rejections later the machine's address was banned,
// and the owner had to edit ip_bans.yaml and restart Home Assistant, taking every machine's
// entities to unavailable meanwhile (#227). agent-ha-bridge had already learned this the same way
// (its test-auth-backoff.ps1, 2026-09-28).
//
// So every request the host makes with a Home Assistant token goes through this guard, which
// counts refusals per token, and in all for this machine, across every caller and every process of
// this user (the connector, ha shadow, shadow run), in a file beside the profiles, so a restart
// starts nothing over. In all, not per address: Home Assistant counts by the client's address, and
// homeassistant.local, its IP and http or https are one Home Assistant to it:
//   * a rejected token (401, auth_invalid) is tried at most MaxRejections times in all, Spacing
//     apart, and then never again until it, or the address it is used with, changes;
//   * once Home Assistant has refused MaxRejections times without a WebSocket login succeeding in
//     between, a token not tried before gets exactly one attempt, and then stops too;
//   * an attempt not yet known to be accepted goes one at a time, so a burst of callers cannot
//     spend the budget at once;
//   * 403 is a ban: Home Assistant answers a refused or under-privileged token 401, and 403 only to
//     an address it has banned. Nothing is sent while it lasts but a look every BanChecks, since
//     requests from a banned address are refused before any token is read, and so are wasted
//     rather than counted;
//   * anything else (5xx, a timeout, no connection) is no refusal, and the callers' own retries
//     and backoff go on as before;
//   * without the record, closed: while it cannot be locked, read or written, a token not already
//     accepted is held, and a record found corrupt is set aside and holds every such token until a
//     person allows one (ha token set) or deletes the record.
public sealed class HaAuthGuard
{
    public const string FileName = "ha-auth.json";
    // Well short of Home Assistant's default threshold of 10, with room for a burst already in
    // flight when the first refusal comes back, and for a second token.
    public const int MaxRejections = 3;
    public static readonly IReadOnlyList<TimeSpan> Spacing = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];
    public static readonly IReadOnlyList<TimeSpan> BanChecks = [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(4)];
    public const string BanHelp = "docs/ha-connector.md, Troubleshooting";
    const int MaxServers = 8;
    // How long another process's attempt is waited for before its reservation counts as abandoned:
    // longer than any request the host makes takes to time out.
    static readonly TimeSpan Reservation = TimeSpan.FromSeconds(45);
    const int MaxCredentials = 16;
    public const string CorruptSuffix = ".corrupt";
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);
    // Short once the record is known to be unavailable: tried again before every request, it must
    // not hold each up for long.
    static readonly TimeSpan RetryLockWait = TimeSpan.FromMilliseconds(50);
    static readonly TimeSpan MoveWait = TimeSpan.FromSeconds(1);
    // Requests out at once from this process, even with an accepted token: one revoked while a
    // burst is on its way is refused once per request already sent, before the first refusal can
    // stop the rest. Each is then counted, by Home Assistant towards a ban and here towards the three.
    internal const int MaxOutstanding = 2;

    readonly string? path;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly object gate = new();
    readonly SemaphoreSlim probe = new(1, 1);
    readonly SemaphoreSlim outstanding = new(MaxOutstanding, MaxOutstanding);
    readonly List<HaCredential> known = [];
    State state = new();
    // The record could not be locked, read or written the last time it was tried. Until it can be
    // again, a token not already accepted is held, and what this process changed meanwhile is kept
    // in pending, to be applied to whatever the record says by then rather than lost to it.
    bool unavailable;
    readonly List<Func<State, bool>> pending = [];
    readonly Func<State, bool> markLost;
    bool lostSaid;
    DateTimeOffset? lostAt;
    // What the record said when this process last read or wrote it.
    string? readText;
    // The record was read again, with whatever another process recorded: what Problem says may have
    // changed, and the app is told (Notify), not only the connector that will skip its pass.
    bool reread;

    // path: the shared record (FileFor); null keeps it in this process only.
    public HaAuthGuard(string? path = null, TimeProvider? time = null, Action<string>? log = null)
    {
        this.path = path;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? (_ => { });
        markLost = s =>
        {
            // One time for the loss, however often it is applied before it can be written: leases
            // let go after it compare against it (Seen.Lost).
            s.Lost ??= lostAt ??= this.time.GetUtcNow();
            return true;
        };
        lock (gate) { Refresh(); }
    }

    // One record for every profile of this user: Home Assistant counts by address, not by profile.
    public static string FileFor(string profileFolder) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profileFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? profileFolder, FileName);

    // Raised when what Problem says may have changed.
    public event Action? Changed;

    // name and remedy are what a refusal says: "Home Assistant rejected <name>: <remedy>".
    public HaCredential Credential(Uri baseUrl, string token, string name = "the token", string remedy = "run `agent-bridge-host ha token set`, then restart the host")
    {
        var server = Normalize(baseUrl);
        var credential = new HaCredential(Hash(server), Hash(server + "\n" + token), name, remedy);
        lock (gate)
        {
            if (known.FirstOrDefault(k => k.Id == credential.Id && k.Name == name) is { } same) { return same; }
            known.Add(credential);
            if (known.Count > MaxCredentials) { known.RemoveAt(0); }
        }
        return credential;
    }

    // What is wrong now with any credential this process has used, with what to do and when it is
    // next tried; null when nothing is refused. For ha status, the log and the app.
    public string? Problem
    {
        get
        {
            lock (gate) { return known.Select(DescribeLocked).FirstOrDefault(p => p is not null); }
        }
    }

    public string? Describe(HaCredential credential)
    {
        lock (gate) { return DescribeLocked(credential); }
    }

    // Why nothing may be sent with this credential now, as a refusal would say it; null when
    // something may. From what this process knows, without reading the file: for a caller to skip
    // a whole pass rather than fail each request in it.
    public string? Refused(HaCredential credential)
    {
        try
        {
            lock (gate)
            {
                Refresh();
                return Decide(credential, time.GetUtcNow()) is { Gate: Gate.Held } d ? Message(credential, d.Kind) : null;
            }
        }
        finally { Notify(); }
    }

    // Changed, once the lock is let go, when the record was read again meanwhile.
    void Notify()
    {
        bool raise;
        lock (gate)
        {
            raise = reread;
            reread = false;
        }
        if (raise) { Changed?.Invoke(); }
    }

    // ha token set: the token was given again, which counts as a change, so it gets an attempt of
    // its own even when the same one was refused before. The address's count is kept. With the
    // record lost, this is the person's say-so for one attempt (Granted). False when it could not
    // be recorded: kept only by this process, it would end with it (ha token set exits at once).
    public bool Forget(HaCredential credential)
    {
        var now = time.GetUtcNow();
        bool recorded;
        lock (gate)
        {
            Mutate(s =>
            {
                if (s.Lost is not null) { ServerOf(s, credential, now).Credentials[credential.Id] = new Cred { Granted = true }; }
                else if (s.Servers.TryGetValue(credential.Server, out var server)) { server.Credentials.Remove(credential.Id); }
            });
            recorded = path is null || !unavailable;
        }
        Changed?.Invoke();
        return recorded;
    }

    // Before a request with this credential: a lease to report how it was answered, or a
    // HomeAssistantAuthException, with nothing sent, when it may not be made now. One that is not
    // known to be accepted waits for any other such attempt to Home Assistant to finish first.
    public async Task<Lease> EnterAsync(HaCredential credential, CancellationToken cancellationToken = default)
    {
        // Taken before the record is read, so a request waiting for its turn decides on what the
        // requests before it were answered.
        await outstanding.WaitAsync(cancellationToken);
        try { return await EnterCoreAsync(credential, cancellationToken); }
        catch
        {
            outstanding.Release();
            throw;
        }
        finally { Notify(); }
    }

    async Task<Lease> EnterCoreAsync(HaCredential credential, CancellationToken cancellationToken)
    {
        Decision decision;
        Seen seen;
        lock (gate)
        {
            // Even a token accepted here may since have been refused in another process.
            Refresh();
            decision = Decide(credential, time.GetUtcNow());
            seen = SeenLocked();
        }
        if (decision.Gate == Gate.Clear) { return new Lease(this, credential, null, seen, null); }
        if (decision.Gate == Gate.Held) { throw Refusal(credential, decision.Kind, sent: false); }
        await probe.WaitAsync(cancellationToken);
        string? reserved = null;
        try
        {
            // What another process, or the attempt waited on, has found meanwhile; and, for an
            // attempt still to be made, the record's reservation, so another process (serve beside
            // ha shadow or shadow run) waits for its answer too rather than sending its own at once.
            // Each reservation waited on is given its full time: one handed on to another attempt is
            // that attempt's, and abandoning it early would send a second unproven token beside it.
            string? waitingOn = null;
            var waitingSince = time.GetTimestamp();
            // The reservation is this attempt's own, and only its answer or its end lets go of it.
            var mine = Guid.NewGuid().ToString("N");
            while (true)
            {
                var busy = false;
                lock (gate)
                {
                    var now = time.GetUtcNow();
                    Func<State, bool> reserve = s =>
                    {
                        decision = Decide(credential, now);
                        if (decision.Gate != Gate.Probe || path is null || unavailable) { return false; }
                        if (s.ProbeBy is not null && s.ProbeUntil > now)
                        {
                            if (s.ProbeBy != waitingOn)
                            {
                                waitingOn = s.ProbeBy;
                                waitingSince = time.GetTimestamp();
                            }
                            if (time.GetElapsedTime(waitingSince) < Reservation)
                            {
                                busy = true;
                                return false;
                            }
                        }
                        s.ProbeBy = mine;
                        s.ProbeUntil = now + Reservation;
                        reserved = mine;
                        return true;
                    };
                    Mutate(reserve);
                    if (unavailable)
                    {
                        // A reservation that could not be recorded reserves nothing: not sent.
                        pending.Remove(reserve);
                        reserved = null;
                        busy = false;
                        decision = Decide(credential, now);
                    }
                    seen = SeenLocked();
                }
                if (!busy) { break; }
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
        }
        catch
        {
            probe.Release();
            throw;
        }
        if (decision.Gate == Gate.Clear) { probe.Release(); return new Lease(this, credential, null, seen, null); }
        if (decision.Gate == Gate.Held) { probe.Release(); throw Refusal(credential, decision.Kind, sent: false); }
        return new Lease(this, credential, probe, seen, reserved);
    }

    // The reservation given up without an answer (a timeout, no connection): the next attempt, in
    // any process, need not wait for it to lapse.
    void Unreserve(string? reservation)
    {
        if (path is null || reservation is null) { return; }
        lock (gate) { Mutate(s => Unreserved(s, reservation)); }
    }

    static bool Unreserved(State s, string? reservation)
    {
        if (reservation is null || s.ProbeBy != reservation) { return false; }
        s.ProbeBy = null;
        s.ProbeUntil = null;
        return true;
    }

    // One request's say in the guard. Disposed without an answer (a timeout, no connection), it
    // changes nothing.
    public sealed class Lease : IDisposable
    {
        readonly HaAuthGuard guard;
        readonly HaCredential credential;
        readonly Seen seen;
        readonly string? reservation;
        SemaphoreSlim? probe;
        bool answered;
        int disposed;

        internal Lease(HaAuthGuard guard, HaCredential credential, SemaphoreSlim? probe, Seen seen, string? reservation)
        {
            this.guard = guard;
            this.credential = credential;
            this.probe = probe;
            this.seen = seen;
            this.reservation = reservation;
        }

        // An HTTP answer, the WebSocket upgrade's included. The refusal to throw when it was one.
        public HomeAssistantAuthException? Answered(HttpStatusCode status)
        {
            if (answered) { return null; }
            switch ((int)status)
            {
                case 401:
                    answered = true;
                    return guard.Refuse(credential, HaAuthKind.Rejected, status, seen, reservation);
                // Not counted by Home Assistant, but how it answers a banned address, before it reads
                // any token. Taken for a ban whatever the token: told apart by a second attempt, a
                // token that had used up its attempts would never be looked at again.
                case 403:
                    answered = true;
                    return guard.Refuse(credential, HaAuthKind.Banned, status, seen, reservation);
                // Home Assistant only gets as far as a missing entity having read the token.
                case 404:
                case >= 200 and < 300:
                    answered = true;
                    guard.Accept(credential, resetsCount: false, seen, reservation);
                    return null;
                default:
                    return null;
            }
        }

        // The WebSocket login: auth_ok, which also resets Home Assistant's count, or auth_invalid.
        public HomeAssistantAuthException? Authenticated(bool ok)
        {
            if (answered) { return null; }
            answered = true;
            if (ok)
            {
                guard.Accept(credential, resetsCount: true, seen, reservation);
                return null;
            }
            return guard.Refuse(credential, HaAuthKind.Rejected, null, seen, reservation);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 1) { return; }
            try
            {
                if (Interlocked.Exchange(ref probe, null) is not { } held) { return; }
                // Answered, the reservation went with the answer; this only lets go of one left over.
                try { guard.Unreserve(reservation); }
                finally { held.Release(); }
            }
            finally { guard.outstanding.Release(); }
        }
    }

    enum Gate { Clear, Probe, Held }

    // What was recorded when a request was let go: an answer to it clears only what it could have
    // answered, never a refusal or ban recorded while it was on its way.
    // By the record's count of every refusal ever recorded, which only rises: a count that a login
    // resets could come back to where it was with a newer refusal in it.
    // And by when the record was last lost: an answer to a request let go before that says nothing
    // the record can still be held to.
    internal readonly record struct Seen(long Refusals, DateTimeOffset? Lost);

    Seen SeenLocked() => new(state.Refusals, state.Lost);

    readonly record struct Decision(Gate Gate, HaAuthKind Kind = HaAuthKind.Rejected);

    Decision Decide(HaCredential credential, DateTimeOffset now)
    {
        state.Servers.TryGetValue(credential.Server, out var server);
        Cred? c = null;
        server?.Credentials.TryGetValue(credential.Id, out c);
        // Without the record, closed: an attempt that could not be recorded could be repeated.
        if (Unproven(c) && unavailable) { return new(Gate.Held, HaAuthKind.Unrecorded); }
        if (Unproven(c) && state.Lost is not null && c is not { Granted: true }) { return new(Gate.Held, HaAuthKind.RecordLost); }
        // Nothing known of this address: a token not tried here before, the one attempt a change earns.
        if (server is null) { return new(Gate.Probe); }
        // A token's own refusals first, ban or no ban: a stopped token is not the one to look with.
        if (c is { Rejections: > 0 } && (Stopped(c) || now < NextAttempt(c))) { return new(Gate.Held); }
        if (server.BannedSince is { } since)
        {
            // A credential not tried since the ban began (a token or address given since) has its
            // one look; otherwise only the scheduled one.
            if (c?.AttemptAt is not { } tried || tried < since || now >= server.BanCheckAt) { return new(Gate.Probe); }
            return new(Gate.Held, HaAuthKind.Banned);
        }
        if (c is { Rejections: > 0 }) { return new(Gate.Probe); }
        return c is { Accepted: true } ? new(Gate.Clear) : new(Gate.Probe);
    }

    static bool Unproven(Cred? c) => c is not { Accepted: true, Rejections: 0 };

    bool Stopped(Cred c) => c.Stopped || c.Rejections >= MaxRejections || state.Strikes >= MaxRejections;

    DateTimeOffset NextAttempt(Cred c)
    {
        var last = c.RejectedAt ?? DateTimeOffset.MinValue;
        if (state.StrikeAt is { } strike && strike > last) { last = strike; }
        return last + Spacing[Math.Min(c.Rejections - 1, Spacing.Count - 1)];
    }

    HomeAssistantAuthException Refuse(HaCredential credential, HaAuthKind kind, HttpStatusCode? status, Seen seen, string? reservation)
    {
        var now = time.GetUtcNow();
        string said;
        lock (gate)
        {
            Mutate(s =>
            {
                Unreserved(s, reservation);
                // Nothing refused since this request was let go: its answer is the newest there is.
                var newest = s.Refusals == seen.Refusals;
                s.Refusals++;
                var server = ServerOf(s, credential, now);
                var c = CredOf(server, credential);
                c.AttemptAt = now;
                if (kind == HaAuthKind.Banned)
                {
                    if (server.BannedSince is null)
                    {
                        server.BannedSince = now;
                        server.BanLooks = 0;
                        server.BanCheckAt = now + BanChecks[0];
                    }
                    // Only a scheduled look moves the schedule on: a new token's one look, or a late
                    // answer to a request let go before the ban, would put the next look hours off.
                    else if (now >= server.BanCheckAt)
                    {
                        server.BanLooks++;
                        server.BanCheckAt = now + BanChecks[Math.Min(server.BanLooks, BanChecks.Count - 1)];
                    }
                }
                else
                {
                    // A 401 means Home Assistant read the token, so a ban recorded before this request
                    // was let go is gone; one recorded since may be the later of the two.
                    if (newest)
                    {
                        server.BannedSince = null;
                        server.BanCheckAt = null;
                        server.BanLooks = 0;
                    }
                    c.Rejections++;
                    c.RejectedAt = now;
                    s.Strikes++;
                    s.StrikeAt = now;
                    // Stopped for good, not only while the count stands: a login with another token
                    // resets the count, and must not give these their attempts back. At the machine's
                    // limit, that is every token refused so far, not only this one.
                    // With the record lost, how often it was refused before is not known: the one
                    // attempt a person allowed is all it gets.
                    if (c.Rejections >= MaxRejections || s.Lost is not null) { c.Stopped = true; }
                    if (s.Strikes >= MaxRejections)
                    {
                        foreach (var refused in s.Servers.Values.SelectMany(x => x.Credentials.Values).Where(x => x.Rejections > 0)) { refused.Stopped = true; }
                    }
                }
            });
            said = DescribeLocked(credential) ?? Message(credential, kind);
        }
        log(said);
        Changed?.Invoke();
        return Refusal(credential, kind, sent: true, status);
    }

    void Accept(HaCredential credential, bool resetsCount, Seen seen, string? reservation)
    {
        var now = time.GetUtcNow();
        string? was;
        string? still;
        bool changed;
        lock (gate)
        {
            // Most answers change nothing, and are not written: one known good, nothing pending.
            var server = state.Servers.GetValueOrDefault(credential.Server);
            var c = server?.Credentials.GetValueOrDefault(credential.Id);
            changed = server is null || c is not { Accepted: true, Rejections: 0, Stopped: false } || server.BannedSince is not null
                || (resetsCount && state.Strikes > 0);
            if (!changed)
            {
                c!.AttemptAt = now;
                return;
            }
            was = DescribeLocked(credential);
            Mutate(s =>
            {
                Unreserved(s, reservation);
                if (s.Lost is not null && s.Lost != seen.Lost) { return; }
                var sv = ServerOf(s, credential, now);
                var cr = CredOf(sv, credential);
                cr.Accepted = true;
                cr.AttemptAt = now;
                // A request answered in Home Assistant's order is not answered here in that order:
                // a refusal recorded since this one was let go may be the later of the two, and is
                // left for an answer that comes after it to clear.
                if (s.Refusals != seen.Refusals) { return; }
                cr.Rejections = 0;
                cr.RejectedAt = null;
                cr.Stopped = false;
                sv.BannedSince = null;
                sv.BanCheckAt = null;
                sv.BanLooks = 0;
                if (resetsCount)
                {
                    s.Strikes = 0;
                    s.StrikeAt = null;
                }
            });
            still = DescribeLocked(credential);
        }
        if (was is not null && still is null) { log($"Home Assistant accepted {credential.Name} again"); }
        Changed?.Invoke();
    }

    static HomeAssistantAuthException Refusal(HaCredential credential, HaAuthKind kind, bool sent, HttpStatusCode? status = null) =>
        new(Message(credential, kind, status), Message(credential, kind), kind, sent, status);

    // Stable, so a caller that says a problem once until it changes says this once. With the
    // status Home Assistant answered, when this attempt was the one it answered; never its body.
    static string Message(HaCredential credential, HaAuthKind kind, HttpStatusCode? status = null)
    {
        var answered = status is { } s ? $" ({(int)s})" : "";
        return kind switch
        {
            HaAuthKind.Banned => $"Home Assistant has banned this machine's address{answered}; see {BanHelp}",
            HaAuthKind.Unrecorded => $"Home Assistant's refusals cannot be recorded in {FileName} now, so {credential.Name} is not sent until they can; see {BanHelp}",
            HaAuthKind.RecordLost => $"{FileName} was unreadable and was set aside as {FileName}{CorruptSuffix}, so {credential.Name} is not sent until a person allows it: " +
                $"run `agent-bridge-host ha token set`, or delete {FileName}; see {BanHelp}",
            _ => $"Home Assistant rejected {credential.Name}{answered}: {credential.Remedy}",
        };
    }

    string? DescribeLocked(HaCredential credential)
    {
        var now = time.GetUtcNow();
        if (Decide(credential, now) is { Gate: Gate.Held, Kind: HaAuthKind.Unrecorded or HaAuthKind.RecordLost } d) { return Message(credential, d.Kind); }
        if (!state.Servers.TryGetValue(credential.Server, out var server)) { return null; }
        if (server.BannedSince is not null)
        {
            return $"{Message(credential, HaAuthKind.Banned)}. It answers 403 to everything from here, whatever the token. " +
                $"To clear it, remove this machine's entry from /config/ip_bans.yaml and restart Home Assistant. " +
                (server.BanCheckAt is { } at && at > now ? $"Looking again at {Clock(at)}." : "Looking again now.");
        }
        if (!server.Credentials.TryGetValue(credential.Id, out var c) || c.Rejections == 0) { return null; }
        var head = Message(credential, HaAuthKind.Rejected);
        if (Stopped(c))
        {
            return $"{head}. Stopped after {c.Rejections} refused attempt{(c.Rejections == 1 ? "" : "s")} with it: " +
                "nothing more is sent with it until it changes, so Home Assistant has no reason to ban this machine.";
        }
        var next = NextAttempt(c);
        return $"{head}. {c.Rejections} of {MaxRejections} attempts used; " +
            (next > now ? $"the next is not before {Clock(next)}" : "the next is due") + ", to stay well short of Home Assistant's ban threshold.";
    }

    // In Pacific time, as everything this project shows, whatever the machine's own zone.
    static string Clock(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Pacific).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " PT";

    static readonly TimeZoneInfo Pacific = FindPacific();

    static TimeZoneInfo FindPacific()
    {
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    static Server ServerOf(State s, HaCredential credential, DateTimeOffset now)
    {
        if (!s.Servers.TryGetValue(credential.Server, out var server))
        {
            s.Servers[credential.Server] = server = new Server { Touched = now };
            // Only addresses with nothing refused or banned, and never the one just added, whose
            // refusal is about to be recorded on it: forgotten, a refusal would be a fresh attempt.
            foreach (var old in s.Servers.Where(x => x.Key != credential.Server && x.Value.BannedSince is null && x.Value.Credentials.Values.All(c => c.Rejections == 0))
                .OrderBy(x => x.Value.Touched ?? DateTimeOffset.MinValue)
                .Take(Math.Max(0, s.Servers.Count - MaxServers)).Select(x => x.Key).ToList())
            {
                s.Servers.Remove(old);
            }
        }
        server.Touched = now;
        return server;
    }

    static Cred CredOf(Server server, HaCredential credential)
    {
        if (!server.Credentials.TryGetValue(credential.Id, out var c))
        {
            server.Credentials[credential.Id] = c = new Cred();
            // Never one with a refusal, nor, during a ban, one that has had its look: forgotten, it
            // would look again before the ban's next scheduled look.
            foreach (var old in server.Credentials.Where(x => x.Key != credential.Id && x.Value.Rejections == 0
                    && !(server.BannedSince is { } since && x.Value.AttemptAt >= since))
                .OrderBy(x => x.Value.AttemptAt ?? DateTimeOffset.MinValue)
                .Take(Math.Max(0, server.Credentials.Count - MaxCredentials)).Select(x => x.Key).ToList())
            {
                server.Credentials.Remove(old);
            }
        }
        return c;
    }

    // Read, changed and written under a lock every process of this user takes, so two never count
    // over each other. Without a file, in memory.
    void Mutate(Action<State> change) => Mutate(s =>
    {
        change(s);
        return true;
    });

    // change says whether it changed anything to write.
    void Mutate(Func<State, bool> change)
    {
        lock (gate) { Sync(change); }
    }

    // Reads the record again, for what another process has recorded since, and writes what this
    // one could not record before.
    void Refresh() => Sync(null);

    // Brings state up to the record, applies what this process changed that the record does not
    // have yet (pending) and change, and writes the result when anything changed. Readers take the
    // lock too: on Windows a reader's open handle makes replacing the file fail, and once that
    // failure stopped a process recording for the rest of its life, while a restart then found its
    // refusals gone (#234 review). A failure now lasts until the next try: meanwhile the change is
    // kept, counted here, and a token not already accepted is held (Decide).
    void Sync(Func<State, bool>? change)
    {
        if (path is null)
        {
            change?.Invoke(state);
            return;
        }
        FileStream? held = null;
        try
        {
            State? basis = null;
            held = Lock(unavailable ? RetryLockWait : LockWait);
            var failure = held is null ? "it is locked by another process" : Load(out basis);
            if (basis is null)
            {
                // Not to be had: what this process knows stands, with this change, kept for later.
                if (change is not null && change(state)) { pending.Add(change); }
                Unavailable(failure!);
                return;
            }
            // Unavailable, it is written even with nothing new: a read alone does not show a write
            // will succeed, and taking it for one would let a token not yet accepted try meanwhile.
            var write = pending.Count > 0 || unavailable;
            foreach (var p in pending) { p(basis); }
            // Before change, which decides (Decide) on state.
            state = basis;
            var changed = change is not null && change(basis);
            if (!write && !changed)
            {
                Available();
                return;
            }
            if (Write(basis) is { } error)
            {
                if (changed) { pending.Add(change!); }
                Unavailable(error);
                return;
            }
            if (pending.Contains(markLost))
            {
                // The loss is on record: another one later is a loss of its own, with its own time.
                lostAt = null;
                lostSaid = false;
            }
            pending.Clear();
            Available();
        }
        finally { held?.Dispose(); }
    }

    // The record as it is now, in basis, or why it could not be had. A record that is there but is
    // not one (a truncated write, a file zero-filled by a power cut) is set aside, never taken for a
    // clean one: that let every token try again and the next write erase every process's refusals.
    // Its loss is recorded instead (Lost), and holds every token not since accepted until a person
    // allows it.
    string? Load(out State? basis)
    {
        basis = null;
        string text;
        try
        {
            if (!File.Exists(path))
            {
                // Never written, or deleted by a person to start over.
                if (readText is not null) { reread = true; }
                readText = null;
                basis = new State();
                return null;
            }
            using var reader = new StreamReader(new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"it could not be read ({e.GetType().Name})";
        }
        if (Parse(text) is { } read)
        {
            if (text != readText) { reread = true; }
            readText = text;
            basis = read;
            return null;
        }
        // First, before what this process could not write yet: an acceptance waiting to be written
        // is from before the loss, and must find the record lost, not restore the token over it.
        if (!pending.Contains(markLost)) { pending.Insert(0, markLost); }
        // Nothing of what came before stands, here either, even if the record cannot be set aside
        // or replaced: another process may have stopped a token this one still knew as accepted.
        state = new State();
        markLost(state);
        // Copied, not moved: the corrupt record stays where it is until the record of its loss
        // replaces it, so a write that fails, or a process that ends, still finds it and holds.
        try { File.Copy(path!, path + CorruptSuffix, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"it is not a record of refusals and could not be set aside ({e.GetType().Name})";
        }
        if (!lostSaid)
        {
            log($"Home Assistant's refusal record {path} was not readable as one, and has been set aside as {FileName}{CorruptSuffix}. " +
                "Nothing is sent with a token not accepted since until `agent-bridge-host ha token set` is run or the record deleted");
            reread = true;
        }
        // Once: until the record of the loss is written over it, it is found again on every read.
        lostSaid = true;
        // Nothing of what came before stands, not even what this process last read: another may
        // have stopped a token it still knew as accepted. Only the loss (markLost, pending).
        readText = null;
        basis = new State();
        return null;
    }

    // 2: every field written, Lost and Granted among them. 1: written by builds of #234 before the
    // record was versioned for change (Strikes once kept per address; Stopped, Refusals, Lost and
    // Granted not always there).
    internal const int RecordVersion = 2;

    // The record in text as this build reads it, migrated from an older version; null when it is not
    // one: it does not parse, holds what no record does, lacks a field of its version, or is of a
    // version newer than this build knows.
    static State? Parse(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root || root["version"] is not JsonValue v || !v.TryGetValue<int>(out var version)) { return null; }
            if (version < 1 || version > RecordVersion) { return null; }
            if (!Complete(root, version == RecordVersion ? Fields : FieldsV1)) { return null; }
            var read = root.Deserialize<State>(Json);
            if (read is null) { return null; }
            if (version < RecordVersion) { Migrate(read, root); }
            return Valid(read) ? read : null;
        }
        // Whatever reading or migrating it throws (a key given twice, a count that overflows when
        // migrated, a type where another was expected), it is not a record: set aside, never thrown
        // to the host's start or ha status.
        catch (Exception) { return null; }
    }

    // A record of version 1, brought up to this one. A token refused at all is stopped, since how
    // many attempts it had is not certain; one accepted stays accepted. Lost and Granted, where a
    // record has none, are none (null, false): only a record that was lost holds every token.
    static void Migrate(State s, JsonObject root)
    {
        // The first records kept the machine's count per address.
        if (!root.ContainsKey("strikes"))
        {
            s.Strikes = root["servers"]!.AsObject().Sum(x => x.Value?["strikes"]?.GetValue<int>() ?? 0);
        }
        foreach (var c in s.Servers.Values.SelectMany(x => x.Credentials.Values).Where(c => c.Rejections > 0)) { c.Stopped = true; }
        s.Version = RecordVersion;
    }

    readonly record struct Shape(string[] Root, string[] Server, string[] Cred);

    static readonly Shape Fields = new(Names<State>(), Names<Server>(), Names<Cred>());

    // What every record of version 1 had, from the first written.
    static readonly Shape FieldsV1 = new(["version", "servers"], ["credentials"], ["rejections", "accepted"]);

    static string[] Names<T>() => [.. typeof(T).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))];

    static bool Complete(JsonObject root, Shape shape) =>
        shape.Root.All(root.ContainsKey)
        && root["servers"] is JsonObject servers
        && servers.All(x => x.Value is JsonObject server && shape.Server.All(server.ContainsKey)
            && server["credentials"] is JsonObject creds
            && creds.All(c => c.Value is JsonObject cred && shape.Cred.All(cred.ContainsKey)));

    // JSON that parses is not yet a record: a missing table or a negative count, written by hand or
    // by a bad write, would otherwise throw from the middle of recording a refusal, so that the
    // refusal went unrecorded and the request was retried as if it had merely failed.
    static bool Valid(State s) =>
        s.Servers is not null && s.Strikes >= 0 && s.Refusals >= 0
        && s.Servers.Values.All(v => v is { Credentials: not null, BanLooks: >= 0 } && v.Credentials.Values.All(c => c is { Rejections: >= 0 }));

    // To a temporary file, flushed to the disk, then over the record in one move, so the record is
    // never half written. A handle another program has open on it (a virus scanner, an indexer) is
    // given a moment to close.
    string? Write(State s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            var text = JsonSerializer.Serialize(s, Json);
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(Encoding.UTF8.GetBytes(text));
                file.Flush(flushToDisk: true);
            }
            var deadline = Environment.TickCount64 + (unavailable ? 0 : (long)MoveWait.TotalMilliseconds);
            while (true)
            {
                try
                {
                    File.Move(temp, path!, overwrite: true);
                    break;
                }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && Environment.TickCount64 < deadline) { Thread.Sleep(25); }
            }
            readText = text;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"it could not be written ({e.GetType().Name})";
        }
    }

    void Unavailable(string failure)
    {
        if (!unavailable)
        {
            log($"Home Assistant's refusals could not be recorded in {path}: {failure}. Until they can, nothing is sent with a token not already accepted");
            reread = true;
        }
        unavailable = true;
    }

    void Available()
    {
        if (!unavailable) { return; }
        unavailable = false;
        reread = true;
        log($"Home Assistant's refusals are recorded in {path} again");
    }

    FileStream? Lock(TimeSpan wait)
    {
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(20); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    // The address as the requests use it, without a trailing slash, so the same Home Assistant is
    // one record however it was written.
    static string Normalize(Uri baseUrl) => baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();

    // Never the token or the address: a digest that tells them apart and gives neither away.
    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // The record's format. To add a field: give it a default, bump RecordVersion, and add a step to
    // Migrate that fills it for records written before, erring toward holding only where it must,
    // never toward holding a token that was working. A field missing from a record of the current
    // version is no format at all, and the record is taken for a corrupt one (a stopped flag or a ban
    // left out would quietly give back what it held); so is a version newer than this build knows.
    sealed class State
    {
        public int Version { get; set; } = RecordVersion;
        // Refusals Home Assistant has counted against this machine since a WebSocket login last
        // reset its count: by any token, at any address it was reached by.
        public int Strikes { get; set; }
        public DateTimeOffset? StrikeAt { get; set; }
        // Every refusal ever recorded, by any process: only ever rises.
        public long Refusals { get; set; }
        // The process making the one attempt not yet known to be accepted, and until when.
        public string? ProbeBy { get; set; }
        public DateTimeOffset? ProbeUntil { get; set; }
        public Dictionary<string, Server> Servers { get; set; } = new(StringComparer.Ordinal);
        // When the record was found unreadable and set aside: what it held is gone, so a token not
        // accepted since waits for a person (Forget, or deleting the record).
        public DateTimeOffset? Lost { get; set; }
    }

    // One Home Assistant, by address.
    sealed class Server
    {
        public DateTimeOffset? BannedSince { get; set; }
        public int BanLooks { get; set; }
        public DateTimeOffset? BanCheckAt { get; set; }
        public DateTimeOffset? Touched { get; set; }
        public Dictionary<string, Cred> Credentials { get; set; } = new(StringComparer.Ordinal);
    }

    sealed class Cred
    {
        public int Rejections { get; set; }
        public bool Stopped { get; set; }
        public DateTimeOffset? RejectedAt { get; set; }
        public bool Accepted { get; set; }
        public DateTimeOffset? AttemptAt { get; set; }
        // Allowed one attempt by a person while the record is lost.
        public bool Granted { get; set; }
    }
}

// Unrecorded: the record of refusals cannot be had now. RecordLost: it was unreadable and was set aside.
public enum HaAuthKind { Rejected, Banned, Unrecorded, RecordLost }

// A token, for one Home Assistant address, as the guard knows it: digests only. Its ToString is
// its name, never the token.
public sealed class HaCredential
{
    internal HaCredential(string server, string id, string name, string remedy)
    {
        Server = server;
        Id = id;
        Name = name;
        Remedy = remedy;
    }

    internal string Server { get; }
    internal string Id { get; }
    public string Name { get; }
    public string Remedy { get; }
    public override string ToString() => Name;
}

// Home Assistant refused this machine, or would have been asked again too soon after it did.
// Sent says which: false when the guard held the request back and nothing reached Home Assistant.
// Reason is the message without the status: the same whether this attempt was answered or held.
public sealed class HomeAssistantAuthException(string message, string reason, HaAuthKind kind, bool sent, HttpStatusCode? status = null)
    : HttpRequestException(message, null, status)
{
    public string Reason { get; } = reason;
    public HaAuthKind Kind { get; } = kind;
    public bool Sent { get; } = sent;
}
