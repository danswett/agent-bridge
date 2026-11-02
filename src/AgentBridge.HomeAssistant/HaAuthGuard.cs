using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
//     and backoff go on as before.
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

    readonly string? path;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly object gate = new();
    readonly SemaphoreSlim probe = new(1, 1);
    // This guard, in the record's reservation of the one attempt not yet known to be accepted.
    readonly string me = Guid.NewGuid().ToString("N");
    readonly List<HaCredential> known = [];
    State state = new();
    bool unreadableSaid;
    bool unlockedSaid;
    // The record could not be written once: from then on this process keeps its own count, which
    // a read of the stale file must not undo (a refusal lost that way would be repeated for ever).
    bool unwritable;
    // When the record had last been written as this process last read it.
    DateTime readAt;

    // path: the shared record (FileFor); null keeps it in this process only.
    public HaAuthGuard(string? path = null, TimeProvider? time = null, Action<string>? log = null)
    {
        this.path = path;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? (_ => { });
        lock (gate) { state = Read(); }
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
        lock (gate)
        {
            Refresh();
            return Decide(credential, time.GetUtcNow()) is { Gate: Gate.Held } d ? Message(credential, d.Kind) : null;
        }
    }

    // ha token set: the token was given again, which counts as a change, so it gets an attempt of
    // its own even when the same one was refused before. The address's count is kept.
    public void Forget(HaCredential credential)
    {
        Mutate(s =>
        {
            if (s.Servers.TryGetValue(credential.Server, out var server)) { server.Credentials.Remove(credential.Id); }
        });
        Changed?.Invoke();
    }

    // Before a request with this credential: a lease to report how it was answered, or a
    // HomeAssistantAuthException, with nothing sent, when it may not be made now. One that is not
    // known to be accepted waits for any other such attempt to Home Assistant to finish first.
    public async Task<Lease> EnterAsync(HaCredential credential, CancellationToken cancellationToken = default)
    {
        Decision decision;
        lock (gate)
        {
            // Even a token accepted here may since have been refused in another process.
            Refresh();
            decision = Decide(credential, time.GetUtcNow());
        }
        if (decision.Gate == Gate.Clear) { return new Lease(this, credential, null); }
        if (decision.Gate == Gate.Held) { throw Refusal(credential, decision.Kind, sent: false); }
        await probe.WaitAsync(cancellationToken);
        try
        {
            // What another process, or the attempt waited on, has found meanwhile; and, for an
            // attempt still to be made, the record's reservation, so another process (serve beside
            // ha shadow or shadow run) waits for its answer too rather than sending its own at once.
            var giveUp = Environment.TickCount64 + (long)Reservation.TotalMilliseconds;
            while (true)
            {
                var busy = false;
                lock (gate)
                {
                    var now = time.GetUtcNow();
                    Mutate(s =>
                    {
                        decision = Decide(credential, now);
                        if (decision.Gate != Gate.Probe || path is null || unwritable) { return false; }
                        if (s.ProbeBy is { } by && by != me && s.ProbeUntil > now && Environment.TickCount64 < giveUp)
                        {
                            busy = true;
                            return false;
                        }
                        s.ProbeBy = me;
                        s.ProbeUntil = now + Reservation;
                        return true;
                    });
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
        if (decision.Gate == Gate.Clear) { probe.Release(); return new Lease(this, credential, null); }
        if (decision.Gate == Gate.Held) { probe.Release(); throw Refusal(credential, decision.Kind, sent: false); }
        return new Lease(this, credential, probe);
    }

    // The reservation given up without an answer (a timeout, no connection): the next attempt, in
    // any process, need not wait for it to lapse.
    void Unreserve()
    {
        if (path is null) { return; }
        lock (gate) { Mutate(s => Unreserved(s)); }
    }

    bool Unreserved(State s)
    {
        if (s.ProbeBy != me) { return false; }
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
        SemaphoreSlim? probe;
        bool answered;

        internal Lease(HaAuthGuard guard, HaCredential credential, SemaphoreSlim? probe)
        {
            this.guard = guard;
            this.credential = credential;
            this.probe = probe;
        }

        // An HTTP answer, the WebSocket upgrade's included. The refusal to throw when it was one.
        public HomeAssistantAuthException? Answered(HttpStatusCode status)
        {
            if (answered) { return null; }
            switch ((int)status)
            {
                case 401:
                    answered = true;
                    return guard.Refuse(credential, HaAuthKind.Rejected, status);
                // Not counted by Home Assistant, but how it answers a banned address, before it reads
                // any token. Taken for a ban whatever the token: told apart by a second attempt, a
                // token that had used up its attempts would never be looked at again.
                case 403:
                    answered = true;
                    return guard.Refuse(credential, HaAuthKind.Banned, status);
                // Home Assistant only gets as far as a missing entity having read the token.
                case 404:
                case >= 200 and < 300:
                    answered = true;
                    guard.Accept(credential, resetsCount: false);
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
                guard.Accept(credential, resetsCount: true);
                return null;
            }
            return guard.Refuse(credential, HaAuthKind.Rejected, null);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref probe, null) is not { } held) { return; }
            // Answered, the reservation went with the answer; this only lets go of one left over.
            try { guard.Unreserve(); }
            finally { held.Release(); }
        }
    }

    enum Gate { Clear, Probe, Held }

    readonly record struct Decision(Gate Gate, HaAuthKind Kind = HaAuthKind.Rejected);

    Decision Decide(HaCredential credential, DateTimeOffset now)
    {
        // Nothing known of this address: a token not tried here before, the one attempt a change earns.
        if (!state.Servers.TryGetValue(credential.Server, out var server)) { return new(Gate.Probe); }
        server.Credentials.TryGetValue(credential.Id, out var c);
        if (server.BannedSince is { } since)
        {
            // A credential not tried since the ban began (a token or address given since) has its
            // one look; otherwise only the scheduled one.
            if (c?.AttemptAt is not { } tried || tried < since || now >= server.BanCheckAt) { return new(Gate.Probe); }
            return new(Gate.Held, HaAuthKind.Banned);
        }
        if (c is { Rejections: > 0 })
        {
            if (Stopped(c)) { return new(Gate.Held); }
            return now < NextAttempt(c) ? new(Gate.Held) : new(Gate.Probe);
        }
        return c is { Accepted: true } ? new(Gate.Clear) : new(Gate.Probe);
    }

    bool Stopped(Cred c) => c.Rejections >= MaxRejections || state.Strikes >= MaxRejections;

    DateTimeOffset NextAttempt(Cred c)
    {
        var last = c.RejectedAt ?? DateTimeOffset.MinValue;
        if (state.StrikeAt is { } strike && strike > last) { last = strike; }
        return last + Spacing[Math.Min(c.Rejections - 1, Spacing.Count - 1)];
    }

    HomeAssistantAuthException Refuse(HaCredential credential, HaAuthKind kind, HttpStatusCode? status)
    {
        var now = time.GetUtcNow();
        string said;
        lock (gate)
        {
            Mutate(s =>
            {
                Unreserved(s);
                var server = ServerOf(s, credential, now);
                var c = CredOf(server, credential);
                c.AttemptAt = now;
                if (kind == HaAuthKind.Banned)
                {
                    if (server.BannedSince is null) { server.BannedSince = now; server.BanLooks = 0; }
                    else { server.BanLooks++; }
                    server.BanCheckAt = now + BanChecks[Math.Min(server.BanLooks, BanChecks.Count - 1)];
                }
                else
                {
                    // A 401 means Home Assistant read the token, so whatever ban there was is gone.
                    server.BannedSince = null;
                    server.BanCheckAt = null;
                    server.BanLooks = 0;
                    c.Rejections++;
                    c.RejectedAt = now;
                    s.Strikes++;
                    s.StrikeAt = now;
                }
            });
            said = DescribeLocked(credential) ?? Message(credential, kind);
        }
        log(said);
        Changed?.Invoke();
        return Refusal(credential, kind, sent: true, status);
    }

    void Accept(HaCredential credential, bool resetsCount)
    {
        var now = time.GetUtcNow();
        string? was;
        bool changed;
        lock (gate)
        {
            // Most answers change nothing, and are not written: one known good, nothing pending.
            var server = state.Servers.GetValueOrDefault(credential.Server);
            var c = server?.Credentials.GetValueOrDefault(credential.Id);
            changed = server is null || c is not { Accepted: true, Rejections: 0 } || server.BannedSince is not null
                || (resetsCount && state.Strikes > 0);
            if (!changed)
            {
                c!.AttemptAt = now;
                return;
            }
            was = DescribeLocked(credential);
            Mutate(s =>
            {
                Unreserved(s);
                var sv = ServerOf(s, credential, now);
                var cr = CredOf(sv, credential);
                cr.Accepted = true;
                cr.Rejections = 0;
                cr.RejectedAt = null;
                cr.AttemptAt = now;
                sv.BannedSince = null;
                sv.BanCheckAt = null;
                sv.BanLooks = 0;
                if (resetsCount)
                {
                    s.Strikes = 0;
                    s.StrikeAt = null;
                }
            });
        }
        if (was is not null) { log($"Home Assistant accepted {credential.Name} again"); }
        Changed?.Invoke();
    }

    static HomeAssistantAuthException Refusal(HaCredential credential, HaAuthKind kind, bool sent, HttpStatusCode? status = null) =>
        new(Message(credential, kind, status), Message(credential, kind), kind, sent, status);

    // Stable, so a caller that says a problem once until it changes says this once. With the
    // status Home Assistant answered, when this attempt was the one it answered; never its body.
    static string Message(HaCredential credential, HaAuthKind kind, HttpStatusCode? status = null)
    {
        var answered = status is { } s ? $" ({(int)s})" : "";
        return kind == HaAuthKind.Banned
            ? $"Home Assistant has banned this machine's address{answered}; see {BanHelp}"
            : $"Home Assistant rejected {credential.Name}{answered}: {credential.Remedy}";
    }

    string? DescribeLocked(HaCredential credential)
    {
        var now = time.GetUtcNow();
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
            // Never the one just added, whose refusal is about to be recorded on it.
            foreach (var old in s.Servers.Where(x => x.Key != credential.Server).OrderBy(x => x.Value.Touched ?? DateTimeOffset.MinValue)
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
            foreach (var old in server.Credentials.Where(x => x.Key != credential.Id).OrderBy(x => x.Value.AttemptAt ?? DateTimeOffset.MinValue)
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
        lock (gate)
        {
            if (path is null)
            {
                change(state);
                return;
            }
            using var held = Lock();
            state = Read();
            if (!change(state)) { return; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
                File.Move(temp, path, overwrite: true);
                readAt = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still counted in this process, which is what keeps it from repeating itself.
                if (!unwritable) { log($"Home Assistant's refusals could not be recorded in {path} ({e.GetType().Name}); counting in this process"); }
                unwritable = true;
            }
        }
    }

    void Reload()
    {
        if (path is not null) { state = Read(); }
    }

    // Reads the record again only when it has been written since it was last read: one look at its
    // time, cheap enough for every request.
    void Refresh()
    {
        if (path is null || unwritable) { return; }
        try
        {
            if (File.GetLastWriteTimeUtc(path) != readAt) { state = Read(); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    State Read()
    {
        if (path is null || unwritable) { return state; }
        try
        {
            if (!File.Exists(path)) { return state.Servers.Count == 0 ? new State() : state; }
            var written = File.GetLastWriteTimeUtc(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = JsonSerializer.Deserialize<State>(stream, Json) ?? new State();
            readAt = written;
            return read;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // What this process has counted stands; an unreadable record is not taken for a clean one.
            if (!unreadableSaid) { log($"Home Assistant's refusals could not be read from {path} ({e.GetType().Name}); counting in this process"); }
            unreadableSaid = true;
            return state;
        }
    }

    FileStream? Lock()
    {
        var deadline = Environment.TickCount64 + 3000;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(20); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (!unlockedSaid) { log($"Home Assistant's refusals could not be locked ({e.GetType().Name}); recording them anyway"); }
                unlockedSaid = true;
                return null;
            }
        }
    }

    // The address as the requests use it, without a trailing slash, so the same Home Assistant is
    // one record however it was written.
    static string Normalize(Uri baseUrl) => baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/').ToLowerInvariant();

    // Never the token or the address: a digest that tells them apart and gives neither away.
    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    sealed class State
    {
        public int Version { get; set; } = 1;
        // Refusals Home Assistant has counted against this machine since a WebSocket login last
        // reset its count: by any token, at any address it was reached by.
        public int Strikes { get; set; }
        public DateTimeOffset? StrikeAt { get; set; }
        // The process making the one attempt not yet known to be accepted, and until when.
        public string? ProbeBy { get; set; }
        public DateTimeOffset? ProbeUntil { get; set; }
        public Dictionary<string, Server> Servers { get; set; } = new(StringComparer.Ordinal);
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
        public DateTimeOffset? RejectedAt { get; set; }
        public bool Accepted { get; set; }
        public DateTimeOffset? AttemptAt { get; set; }
    }
}

public enum HaAuthKind { Rejected, Banned }

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
