using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core;

// Who drives each session's turn: the person, or an agent acting through the host (the purple
// edge). agent-ha-bridge's Set-DaemonDriverPending and Get-BridgeDriverFromState, kept once for the
// whole host, so its own clients (Session.Driver) and the Home Assistant connector's cards (the
// activity sensor's driver attribute) can never disagree about a session.
//
// A press, answer, reply or launch arms the driver: it shows at once, and the next turn to start
// within the arm window is the arm's. A turn that starts with no arm, or after it lapsed, is the
// person's: that is someone typing at the machine. So an agent's edge stays until the person takes
// the session back, by answering or replying from anywhere or by typing in its window.
public sealed class SessionDrivers
{
    public const string Human = "human";
    public const string Agent = "agent";

    readonly ConcurrentDictionary<string, SessionDriver> drivers = new(StringComparer.Ordinal);

    // DriverArmSeconds: an arm left by a press stops counting for the next turn after this long,
    // since the turn it was for is not coming.
    public TimeSpan Arm { get; init; } = TimeSpan.FromSeconds(600);

    // A session's shown driver changed outside a transcript change (a press armed it), with the
    // session's id. Raised on the caller's thread, after the change; a handler that throws is
    // ignored, so it cannot undo a press.
    public event Action<string>? Changed;

    // Set by the SessionSource that observes every session for this record, from the transcript
    // as it is read. Anyone else then only arms and reads: a second observer may hold an older
    // or budget-trimmed snapshot, and would take it for another turn. Without one (a connector
    // over a source of its own), the connector observes, and forgets a session once it has gone.
    public bool SourceObserves { get; set; }

    public SessionDriver For(string id) => drivers.GetOrAdd(id, i => new SessionDriver(this, i));

    // The session has gone.
    public void Forget(string id) => drivers.TryRemove(id, out _);

    // The driver as Session.Driver carries it: Agent with its principal, or null for the person.
    public (DriverKind? Kind, string? Principal) Shown(string id) => drivers.TryGetValue(id, out var d) ? d.Shown : (null, null);

    // How many times the session has been armed: a result that comes back after another arm is
    // older than it, and must not replace it. Zero for a session with no record, which this never
    // creates (the id may be anything a device sent).
    public long GenerationOf(string id) => drivers.TryGetValue(id, out var d) ? d.Generation : 0;

    // A person answered or replied from a device, if nothing armed the session since `generation`.
    public void ArmPerson(string id, DateTimeOffset now, long generation)
    {
        if (GenerationOf(id) == generation) { For(id).ArmDriver(Human, now); }
    }

    // A person answered or replied from a device: the turn is theirs, as if typed at the machine.
    public void ArmPerson(string id, DateTimeOffset now) => For(id).ArmDriver(Human, now);

    // The principal of a press by one of homeAssistant.agentUserIds.
    public static string HomeAssistantPrincipal(string userId) =>
        Limits.KeepStart("homeassistant:" + userId.Trim(), Limits.DriverPrincipal)!;

    internal void Raise(string id)
    {
        if (Changed is not { } handlers) { return; }
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
        {
            try { handler(id); }
            catch (Exception) { }
        }
    }
}

// One session's driver and the arm left for its next turn. Every member takes the lock, since the
// connector arms it from its passes while the session source reads it on its own.
public sealed class SessionDriver
{
    readonly SessionDrivers owner;
    readonly string id;
    readonly Lock gate = new();
    // As the Home Assistant card reads it: "agent", "human", or what a reply-card publish named.
    string driver = SessionDrivers.Human;
    string? principal;
    DateTimeOffset? pendingAt;
    string? turnKey;
    // An agent's launch: its opening turn, the prompt it launched with, is the agent's, and the arm
    // is for that turn alone. The opening turn may be here already (the session registered before
    // its launch was reported), so it is looked for as well as waited for; the turn after it is
    // whoever's it is. Without a prompt there is no opening turn, and the arm waits for the first,
    // as a press's does.
    string? openingPrompt;
    bool openingResumed;
    long generation;

    internal SessionDriver(SessionDrivers owner, string id)
    {
        this.owner = owner;
        this.id = id;
    }

    public string Driver { get { lock (gate) { return driver; } } }

    public long Generation { get { lock (gate) { return generation; } } }

    public string? Principal { get { lock (gate) { return principal; } } }

    // When the arm for the next turn was left, or null when there is none.
    public DateTimeOffset? PendingAt
    {
        get { lock (gate) { return pendingAt; } }
        set { lock (gate) { pendingAt = value; } }
    }

    public (DriverKind? Kind, string? Principal) Shown
    {
        get { lock (gate) { return ShownLocked(); } }
    }

    (DriverKind?, string?) ShownLocked() => driver == SessionDrivers.Agent ? (DriverKind.Agent, principal) : (null, null);

    public void ArmDriver(string by, DateTimeOffset now, string? agentPrincipal = null) => Arm(by, now, agentPrincipal, null, false);

    // The whole launch, opening turn included, before anyone hears of it: a source told first
    // would observe an opening turn already here without spending the arm on it.
    public void ArmLaunch(DateTimeOffset at, string? prompt, bool resumed, string? agentPrincipal = null) =>
        Arm(SessionDrivers.Agent, at, agentPrincipal, prompt is { Length: > 0 } ? Collapse(prompt) : null, resumed);

    void Arm(string by, DateTimeOffset now, string? agentPrincipal, string? opening, bool resumed)
    {
        bool changed;
        lock (gate)
        {
            var before = ShownLocked();
            driver = by;
            principal = by == SessionDrivers.Agent ? agentPrincipal : null;
            pendingAt = now;
            openingPrompt = opening;
            openingResumed = resumed;
            generation++;
            changed = before != ShownLocked();
        }
        if (changed) { owner.Raise(id); }
    }

    // The driver and its arm as they are, to be put back when an arm turns out to be for nothing.
    public readonly record struct Saved(string Driver, string? Principal, DateTimeOffset? Pending);

    public Saved Save() { lock (gate) { return new Saved(driver, principal, pendingAt); } }

    public void Restore(Saved saved)
    {
        bool changed;
        lock (gate)
        {
            var before = ShownLocked();
            (driver, principal, pendingAt) = (saved.Driver, saved.Principal, saved.Pending);
            generation++;
            changed = before != ShownLocked();
        }
        if (changed) { owner.Raise(id); }
    }

    // The session as it is now. A new turn (a new message from the person or an agent) decides who
    // drives it: the arm that was waiting for it, or nobody, and then the person at the machine
    // does. Otherwise a launch's opening turn, once seen, spends its arm. Returns whether the shown
    // driver changed; it raises nothing, since whoever observes is about to show the session.
    public bool Observe(Session full, DateTimeOffset now, TimeSpan arm)
    {
        // A turn is a prompt started: TurnCount counts those whose turn has finished, one fewer
        // while one is in progress, so an agent's turn ending is no new turn. The count alone: its
        // text can come in a record of its own after it (Codex's task_started, then user_message),
        // and must not read as a second prompt. Text only where there is no count.
        var turn = full.Activity.TurnCount is { } n
            ? (n + (full.Status is SessionStatus.Working or SessionStatus.Waiting ? 1 : 0)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "\n" + full.Activity.UserMessage;
        lock (gate)
        {
            var before = ShownLocked();
            if (turnKey is not null && turnKey != turn)
            {
                if (pendingAt is not { } at || now - at >= arm)
                {
                    driver = SessionDrivers.Human;
                    principal = null;
                }
                pendingAt = null;
                openingPrompt = null;
            }
            else if (openingPrompt is { } opening)
            {
                var message = full.Activity.UserMessage is { } m ? Collapse(m) : "";
                var head = opening[..Math.Min(80, opening.Length)];
                if (message.StartsWith(head, StringComparison.Ordinal) || (!openingResumed && full.Activity.TurnCount >= 1))
                {
                    pendingAt = null;
                    openingPrompt = null;
                }
            }
            turnKey = turn;
            return before != ShownLocked();
        }
    }

    static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}
