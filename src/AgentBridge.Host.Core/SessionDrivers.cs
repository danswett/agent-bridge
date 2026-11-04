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

    // A person's answer or reply from a device took the session back, unless something armed it
    // since `generation`, which is then the later input. Checked and applied under the record's own
    // lock, so a press in between cannot be overwritten. An answer continues the turn it was asked
    // in, so it arms nothing for the next prompt; a reply arms the prompt it is.
    public void TakeBack(string id, DateTimeOffset now, long generation, bool answer)
    {
        SessionDriver d;
        if (!drivers.TryGetValue(id, out var known))
        {
            if (generation != 0) { return; }
            d = For(id);
        }
        else { d = known; }
        d.ApplyIf(Human, answer ? null : now, generation);
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
public sealed partial class SessionDriver
{
    readonly SessionDrivers owner;
    readonly string id;
    readonly Lock gate = new();
    // As the Home Assistant card reads it: "agent", "human", or what a reply-card publish named.
    string driver = SessionDrivers.Human;
    string? principal;
    DateTimeOffset? pendingAt;
    string? turnKey;
    // The prompt an agent's reply or launch sent (userMessageFrom, #489), and whether a turn it
    // started is still waiting for it. Known by the prompt itself (its record, else its words), not
    // by turn: Codex counts a turn before its text arrives, and a turn no prompt started (a notice)
    // changes the count but not the prompt, so a turn key handed one prompt's sender to another.
    // By record where the parser knows it, so the same words sent by the person and by an agent are
    // two prompts.
    string? agentPrompt;
    bool awaitingAgentPrompt;
    string? lastMessage;
    // The words an agent's reply was sent with, where the arm knows them (Expect): only a prompt
    // that starts with them is the agent's. A prompt the person typed while it was on its way, read
    // in the same refresh, is then theirs rather than taken for the agent's.
    string? expected;
    // An arm whose words are still to come (a press of Send, before the box is read): until they
    // are known, no prompt is taken for the agent's, so one the person typed meanwhile stays theirs.
    bool wordsToCome;
    // The transcript's prompt count at the last look: more than one new prompt in one read, and which
    // of them an arm was for cannot be told, so none is taken for the agent's.
    int? lastPrompts;
    // An agent's launch: its opening turn, the prompt it launched with, is the agent's, and the arm
    // is for that turn alone. The opening turn may be here already (the session registered before
    // its launch was reported), so it is looked for as well as waited for; the turn after it is
    // whoever's it is. Without a prompt there is no opening turn, and the arm waits for the first,
    // as a press's does. A resume already shows a prompt from before the launch, possibly the
    // person's in the very words the launch sends: the one shown when the arm was set is never the
    // launch's, and the arm waits for the next.
    string? openingPrompt;
    bool openingResumed;
    string? openingSeen;
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

    // Whether the session's latest prompt is one an agent sent through the host: the turn it
    // started was an agent's reply's or launch's. An answer continues a turn rather than starting
    // one, so it never makes the prompt before it the agent's.
    public bool PromptIsAgents
    {
        get { lock (gate) { return agentPrompt is not null && agentPrompt == lastMessage; } }
    }

    // A reply or a press of Send: shown at once, and armed for the prompt it is. With its words,
    // where they are known, in the same step: a refresh between the two would find the arm with
    // nothing to compare against. wordsToCome: they will be known later (Expect).
    public void ArmDriver(string by, DateTimeOffset now, string? agentPrincipal = null, string? words = null, bool wordsToCome = false) =>
        Apply(by, now, agentPrincipal, null, false, words, wordsToCome, null);

    // The whole launch, opening turn included, before anyone hears of it: a source told first
    // would observe an opening turn already here without spending the arm on it. Its words are
    // what any prompt must be to be the launch's, the opening turn or the next.
    public void ArmLaunch(DateTimeOffset at, string? prompt, bool resumed, string? agentPrincipal = null) =>
        Apply(SessionDrivers.Agent, at, agentPrincipal, prompt is { Length: > 0 } ? Collapse(prompt) : null, resumed, prompt, false, null);

    // An answer continues the turn it was asked in: that turn is the answerer's from now on, and
    // nothing is left armed, so the next prompt is whoever's it is. An arm left for the next prompt
    // would wear the agent's edge onto the person's own prompt typed at the machine.
    public void Answered(string by, string? agentPrincipal = null) => Apply(by, null, agentPrincipal, null, false, null, false, null);

    internal void ApplyIf(string by, DateTimeOffset? pending, long generation) => Apply(by, pending, null, null, false, null, false, generation);

    // The words the reply armed for is being sent with, once they are known (a press of Send arms
    // before the box is read). For the arm pending now; a later arm forgets them.
    public void Expect(string text)
    {
        lock (gate)
        {
            if ((pendingAt is not null || wordsToCome) && Collapse(text) is { Length: > 0 } said) { (expected, wordsToCome) = (said, false); }
        }
    }

    void Apply(string by, DateTimeOffset? pending, string? agentPrincipal, string? opening, bool resumed, string? words, bool toCome, long? onlyAt)
    {
        bool changed;
        lock (gate)
        {
            if (onlyAt is { } g && generation != g) { return; }
            var before = ShownLocked();
            expected = words is null ? null : Collapse(words) is { Length: > 0 } said ? said : null;
            wordsToCome = toCome && expected is null;
            driver = by;
            principal = by == SessionDrivers.Agent ? agentPrincipal : null;
            pendingAt = pending;
            openingPrompt = opening;
            openingResumed = resumed;
            openingSeen = resumed ? lastMessage : null;
            generation++;
            changed = before != ShownLocked();
        }
        if (changed) { owner.Raise(id); }
    }

    // The driver and its arm as they are, to be put back when an arm turns out to be for nothing.
    public readonly record struct Saved(string Driver, string? Principal, DateTimeOffset? Pending, string? Expected = null, bool WordsToCome = false);

    public Saved Save() { lock (gate) { return new Saved(driver, principal, pendingAt, expected, wordsToCome); } }

    public void Restore(Saved saved)
    {
        bool changed;
        lock (gate)
        {
            var before = ShownLocked();
            (driver, principal, pendingAt, expected, wordsToCome) = (saved.Driver, saved.Principal, saved.Pending, saved.Expected, saved.WordsToCome);
            generation++;
            changed = before != ShownLocked();
        }
        if (changed) { owner.Raise(id); }
    }

    // The session as it is now. A new turn (a new message from the person or an agent) decides who
    // drives it: the arm that was waiting for it, or nobody, and then the person at the machine
    // does. Otherwise a launch's opening turn, once seen, spends its arm. Returns whether the shown
    // driver changed; it raises nothing, since whoever observes is about to show the session.
    // prompt: which prompt the session's userMessage is (SessionTracker.PromptKey); without one, its
    // words stand for it.
    // prompts: the transcript's prompt count (SessionTracker.Prompts), where the source knows it.
    public bool Observe(Session full, DateTimeOffset now, TimeSpan arm, string? prompt = null, int? prompts = null)
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
            var added = prompts is { } count && lastPrompts is { } was ? count - was : 0;
            var waited = awaitingAgentPrompt;
            if (turnKey is not null && turnKey != turn)
            {
                if (pendingAt is not { } at || now - at >= arm)
                {
                    driver = SessionDrivers.Human;
                    principal = null;
                }
                awaitingAgentPrompt = driver == SessionDrivers.Agent;
                pendingAt = null;
                openingPrompt = null;
            }
            // A new prompt the count did not show, since a notice's uncounted turn was running when
            // it came: the arm waiting for the next prompt is for this one.
            else if (turnKey is not null && pendingAt is { } armed && now - armed < arm && openingPrompt is null
                && Prompt(full, prompt) is { } next && next != lastMessage)
            {
                awaitingAgentPrompt = driver == SessionDrivers.Agent;
                pendingAt = null;
            }
            else if (openingPrompt is { } opening && !(openingResumed && openingSeen is not null && Prompt(full, prompt) == openingSeen))
            {
                var message = full.Activity.UserMessage is { } m ? Collapse(m) : "";
                var head = opening[..Math.Min(80, opening.Length)];
                if (message.StartsWith(head, StringComparison.Ordinal) || (!openingResumed && full.Activity.TurnCount >= 1))
                {
                    // Spent either way, but the prompt is the launch's only when it is the one shown,
                    // its words exactly: the person may have sent another before the launch was
                    // reported, and one that only begins like it ("Fix the build, but do not deploy")
                    // is still theirs.
                    if (driver == SessionDrivers.Agent && Matches(opening, full.Activity.UserMessage))
                    {
                        (agentPrompt, awaitingAgentPrompt) = (Prompt(full, prompt), false);
                    }
                    pendingAt = null;
                    openingPrompt = null;
                }
            }
            // The agent's turn's own text: the first message it brings that is new.
            // More than one prompt since the last look (an agent's and the person's in one read), or
            // another after the one awaited began: which is the arm's cannot be told, so neither is.
            if (added > 1 || (waited && added > 0)) { awaitingAgentPrompt = false; }
            var said = Prompt(full, prompt);
            if (awaitingAgentPrompt && said is not null && said != lastMessage && Sent(full))
            {
                (agentPrompt, awaitingAgentPrompt) = (said, false);
            }
            if (prompts is not null) { lastPrompts = prompts; }
            lastMessage = said;
            turnKey = turn;
            return before != ShownLocked();
        }
    }

    // Words as delivery types them (InjectableText.Collapse, LaunchProtocol.Flatten): a control
    // character or a line or paragraph separator is a space, a lone surrogate U+FFFD. Then runs of
    // whitespace are one space and the ends are trimmed. Kept as delivery's, or a reply with an
    // escape in it is recorded as other words than were expected and shown as the person's.
    static string Collapse(string text)
    {
        var typed = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                typed.Append(c).Append(text[++i]);
                continue;
            }
            typed.Append(char.IsSurrogate(c) ? '\uFFFD'
                : char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Control
                    or System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator ? ' ' : c);
        }
        return Regex.Replace(typed.ToString(), @"\s+", " ").Trim();
    }

    // Whether the prompt shown is the one the arm expected, when it knows what to expect.
    bool Sent(Session full) => expected is null ? !wordsToCome : Matches(expected, full.Activity.UserMessage);

    // Whether a prompt shown is these words: exactly, or after the attachments delivery puts before
    // them ("@<path>" mentions, Codex's "[Image #n]" placeholders, which it runs together). Or,
    // where the shown prompt was cut to its limit, their start.
    static bool Matches(string words, string? shown)
    {
        if (shown is null) { return false; }
        // The words as they are first: a reply may itself begin with "@README.md" or "[Image #1]".
        var said = Collapse(shown);
        if (said == words) { return true; }
        // Then only what delivery put before them, whole: nothing of the reply's own is taken off.
        if (said.EndsWith(" " + words, StringComparison.Ordinal) && Attachments().IsMatch(said[..^(words.Length + 1)])) { return true; }
        // Cut to its limit, the shown prompt is the start of what was sent.
        return System.Text.Encoding.UTF8.GetByteCount(shown) >= Limits.UserMessage - 4
            && (words.StartsWith(said, StringComparison.Ordinal)
                || Enumerable.Range(1, said.Length - 1).Any(i => said[i - 1] == ' ' && Attachments().IsMatch(said[..(i - 1)]) && words.StartsWith(said[i..], StringComparison.Ordinal)));
    }

    [GeneratedRegex(@"^(?:@\S+|(?:\[Image #\d+\])+)(?: (?:@\S+|(?:\[Image #\d+\])+))*$")]
    private static partial Regex Attachments();

    static string? Prompt(Session full, string? prompt) =>
        full.Activity.UserMessage is null ? null : prompt ?? Collapse(full.Activity.UserMessage);
}
