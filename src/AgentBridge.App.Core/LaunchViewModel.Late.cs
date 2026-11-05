using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// What a machine is busy with, listed again once it can have ended, and a launch reported
// unconfirmed that turns out to have started (#122). A launched agent asking whether to trust
// its folder keeps the machine busy until its session registers, or for two minutes at most
// (v1-launch.md); the page used to keep saying so until it was left, and to keep saying the
// launch was not confirmed, with its prompt still in the box, after its session had appeared.
// Both invited a second press and a second session. Nothing new is asked of the machine: its
// listing, its sessions and the outcome's session id say all of it.
public sealed partial class LaunchViewModel
{
    static readonly TimeSpan BusyFor = TimeSpan.FromMinutes(2);
    // Listed again a moment after the machine's own two minutes, and never sooner than this: a
    // clock here ahead of the machine's would otherwise list again and again.
    static readonly TimeSpan BusySlack = TimeSpan.FromSeconds(1);
    static readonly TimeSpan BusyAtLeast = TimeSpan.FromSeconds(5);
    // How often a session that appeared may have the machine asked whether its question still waits.
    static readonly TimeSpan LateRecheck = TimeSpan.FromSeconds(15);

    ITimer? busyTimer;
    // The sessions the machine shown had when it said it was busy: a new one may be the one
    // that kept it busy, now registered.
    HashSet<string>? busySessions;
    readonly Dictionary<Machine, Action<Session>> sessionWatch = [];

    // The last launch, reported unconfirmed with its session's id while that session was not
    // listed at its machine. Seen: the load counter when it appeared. Never taken as started
    // while the session is ending, and never on appearing alone for a Copilot (or Agency), which
    // registers before its window is ready (Launchers' RegistersBeforeReady): one asking whether
    // to trust its folder counts once a listing asked for after it appeared no longer has a
    // question for that folder (an answer at the machine; one declined here ends the following),
    // and one with no question is left as the machine said. One already listed when the outcome
    // came is never followed: a Copilot whose window was not seen ready, as its appearing says
    // nothing about.
    sealed record LateStart(Machine Machine, string Request, string Session, string? Trust, string? Folder)
    {
        public int? Seen { get; init; }
        public DateTimeOffset? Checked { get; init; }
    }
    LateStart? late;
    bool lateChecking;
    // The launch shown as started since its unconfirmed outcome, by request.
    string? startedLate;

    void WatchSessions()
    {
        foreach (var m in main.AllMachines.Where(m => !sessionWatch.ContainsKey(m)).ToList())
        {
            void Upserted(Session s) => OnSession(m, s);
            sessionWatch[m] = Upserted;
            m.Host.Sessions.Upserted += Upserted;
        }
        foreach (var (m, upserted) in sessionWatch.Where(p => !main.AllMachines.Contains(p.Key)).ToList())
        {
            m.Host.Sessions.Upserted -= upserted;
            sessionWatch.Remove(m);
            if (late?.Machine == m) { late = null; }
        }
    }

    // A launch's outcome: the unconfirmed one of a session not yet listed is followed.
    void FollowLate(Machine m, Delivery d)
    {
        if (OutcomeOf(d) != "unconfirmed" || lost is not null || m != lastMachine || last?.Request != d.Request) { return; }
        if (Result(d) is not { Session: { } id } r || m.Host.Sessions.Get(id) is not null) { return; }
        late = new LateStart(m, d.Request, id, r.Trust?.Id, r.Trust?.Folder);
    }

    bool Alive(LateStart l) => l.Machine.Host.Sessions.Get(l.Session) is { Status: not SessionStatus.Ending };

    // Whether a listing still has the late launch's question: by its id, or by its folder, since
    // a question whose words change is listed again under a new id.
    static bool Asks(LateStart l, IEnumerable<TrustQuestion> listed) => listed.Any(q => q.Id == l.Trust || q.Folder == l.Folder);

    void OnSession(Machine m, Session s)
    {
        var relist = false;
        if (late is { } l && l.Machine == m && l.Session == s.Id && lost is null && last?.Request == l.Request && s.Status != SessionStatus.Ending)
        {
            if (l.Seen is null)
            {
                late = l = l with { Seen = loads };
                if (l.Trust is null)
                {
                    if (s.Agent == AgentKind.Copilot) { late = null; } else { StartedLate(l); }
                }
                // Its listing says whether the question still waits: the page's own, when it shows
                // that machine, which also redraws its busy and its questions.
                else if (shown && machine == m) { relist = true; }
                else { CheckLate(); }
            }
            else { CheckLate(); }
        }
        // A session new since the machine said it was busy may be the one that kept it busy.
        if (busySessions is { } known && shown && machine == m && options?.Busy is { Length: > 0 } && known.Add(s.Id)) { relist = true; }
        if (relist) { _ = RefreshAsync(); }
    }

    // A listing of m asked for after the late session appeared: started, once it no longer has
    // that session's question.
    void ListedLate(Machine m, int load)
    {
        if (late is not { Seen: { } seen, Trust: not null } l || l.Machine != m || load <= seen || !Alive(l)) { return; }
        if (!Asks(l, (m.IsLocal ? localTrust ?? options?.Trust : options?.Trust) ?? [])) { StartedLate(l); }
    }

    // Asks the machine whether the question still waits, at most every LateRecheck, without
    // redrawing the page: it is not on that machine, or the session changed since.
    void CheckLate()
    {
        if (late is not { Seen: not null, Trust: not null } l || lateChecking) { return; }
        var now = main.Time.GetUtcNow();
        if (l.Checked is { } at && now - at < LateRecheck) { return; }
        late = l with { Checked = now };
        lateChecking = true;
        _ = CheckLateAsync(l);
    }

    async Task CheckLateAsync(LateStart l)
    {
        IReadOnlyList<TrustQuestion>? listed = null;
        try { listed = (await l.Machine.Host.ReadAsync<LaunchOptions>(LaunchProtocol.Options, timeout: OptionsTimeout)).Trust ?? []; }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or System.Text.Json.JsonException) { }
        ui.Post(() =>
        {
            lateChecking = false;
            if (listed is not null && late is { } now && now.Request == l.Request && Alive(now) && !Asks(now, listed)) { StartedLate(now); }
        });
    }

    // As a launch that started at once: its prompt went with it, and its card opens.
    void StartedLate(LateStart l)
    {
        late = null;
        if (lost is not null || last is not { } e || e.Request != l.Request) { return; }
        startedLate = l.Request;
        if (l.Trust is not null && askedBy.TryGetValue(l.Machine.Key, out var by) && by.Question == l.Trust) { askedBy.Remove(l.Machine.Key); }
        if (e.Payload is LaunchRequest sent && (string.IsNullOrWhiteSpace(prompt) ? null : prompt) == sent.Prompt) { Prompt = ""; }
        main.Open(l.Machine.Key, l.Session, untilItArrives: true);
        Show();
    }

    // After each listing: once what keeps the machine busy can have ended, it is listed again.
    void ArmBusy(Machine m)
    {
        busyTimer?.Dispose();
        busyTimer = null;
        if (options?.Busy is not { Length: > 0 })
        {
            busySessions = null;
            return;
        }
        busySessions = [.. m.Host.Sessions.All.Select(s => s.Id)];
        var asked = (m.IsLocal ? localTrust ?? options.Trust : options.Trust) ?? [];
        // Bypass Permissions' busy names no question, nor one older than two minutes: the most it lasts.
        var left = asked.Count > 0 ? asked.Max(q => q.AskedAt) + BusyFor - main.Time.GetUtcNow() : TimeSpan.Zero;
        var wait = left > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Clamp((left + BusySlack).Ticks, BusyAtLeast.Ticks, (BusyFor + BusySlack).Ticks)) : BusyFor + BusySlack;
        var load = loads;
        busyTimer = main.Time.CreateTimer(_ => ui.Post(() =>
        {
            if (shown && machine == m && load == loads) { _ = RefreshAsync(); }
        }), null, wait, Timeout.InfiniteTimeSpan);
    }

    void StopBusy()
    {
        busyTimer?.Dispose();
        busyTimer = null;
        busySessions = null;
    }
}
