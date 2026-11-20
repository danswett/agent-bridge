using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// Another of the account's machines' updates (#51, docs/protocol/v1-update.md): what it runs, a
// newer release once a check has found one, what its app is doing about it, how the last update
// ended, and Check and Install. Install asks that machine's app to install the latest release as
// soon as no session there has been busy for two minutes, however long that takes (#516): its host
// and app restart then, and the outcome comes back here. That machine's host lets only a
// person's device ask (this app is one) and none at all when it is read-only; it is then not
// listed, as it offers no updates.
public sealed class MachineUpdateViewModel : Observable
{
    readonly Machine machine;
    readonly TimeProvider time;
    bool busy;
    string? note;

    public MachineUpdateViewModel(Machine machine, TimeProvider time)
    {
        this.machine = machine;
        this.time = time;
    }

    public string Key => machine.Key;
    public string Name => machine.Name;
    UpdateState? State => machine.Host.Update;

    // The host's own line for an update held back for a question waiting (AutoUpdater.QuestionLine).
    const string QuestionLinePrefix = "Update ready \u00b7 ";

    public string StatusText
    {
        get
        {
            if (State is not { } s) { return ""; }
            if (!s.Installer) { return Line(s.Unavailable ?? "its app is not running, so it cannot update now"); }
            // "Needs you" (#800) is the host's own line, in whatever phase: an update waited past
            // six hours is never left to the log alone.
            if (s.Waiting is { } held && held.StartsWith("Needs you", StringComparison.Ordinal)) { return Words.Sentence(held); }
            // So is the line of an update held back for a question waiting (Creative on #719,
            // 6674096426): its exact words.
            if (s.Waiting is { } question && question.StartsWith(QuestionLinePrefix, StringComparison.Ordinal)) { return question; }
            return s.Phase switch
            {
                UpdatePhase.Checking => "Checking for updates\u2026",
                UpdatePhase.Downloading => $"Downloading {s.Available ?? "the update"}{(s.Progress is { } p ? $" ({p}%)" : "")}\u2026",
                UpdatePhase.Installing => $"Installing {s.Available ?? "the update"}: its app and host restart\u2026",
                UpdatePhase.Waiting => $"{s.Available ?? "The update"} installs when it can: {s.Waiting ?? "a session is busy"}.",
                _ when s.Available is { } next => $"{next} is available (running {s.Current}).",
                // No check has finished there yet (automatic updates off, or the first one failed):
                // nothing newer found is not the same as nothing newer.
                _ when s.CheckedAt is null => $"Running {s.Current}; not checked for updates yet.",
                _ => $"Up to date: {s.Current}, checked {Words.Ago(s.CheckedAt.Value, time.GetUtcNow())}.",
            };
        }
    }

    // How the last update asked for, or installed by itself, ended.
    public string? LastText => State?.Last is not { } l ? null : machine.NowPresses.Contains(l.Request) ? l.Outcome switch
    {
        UpdateOutcome.Updated => l.To is { } to ? $"Updated to {to}." : "Updated.",
        UpdateOutcome.Current => "Already up to date.",
        _ => UpdateNowWords.NotUpdated(l.Code, State?.UpdateNowAt, failed: l.Outcome == UpdateOutcome.Failed),
    } : l.Outcome switch
    {
        UpdateOutcome.Updated => $"Updated from {l.From} to {l.To} {Words.Ago(l.At, time.GetUtcNow())}.",
        UpdateOutcome.Current => $"Already up to date when asked {Words.Ago(l.At, time.GetUtcNow())}.",
        UpdateOutcome.Refused => $"Not updated: {Words.Sentence(l.Reason ?? "refused")}",
        _ => $"The update failed: {Words.Sentence(l.Reason ?? "no reason given")}",
    };
    public bool HasLast => LastText is not null;

    // What a press came to, when the machine refused it or could not be reached.
    public string? Note => note;
    public bool HasNote => note is not null;

    bool Settled => State is { Installer: true, Phase: UpdatePhase.Idle };
    public bool CanCheck => !busy && Settled;
    public bool CanInstall => !busy && Settled && State?.Available is not null;
    public string InstallName => $"Install {State?.Available ?? "the update"} on {Name} when no session there is busy";
    public string CheckName => $"Check {Name} for updates";

    MachineUpdateNowPrompt? nowPrompt;
    UpdateState? nowState;
    long nowConnection;
    long nowSnapshot;
    bool nowWasCurrent;
    // The press last taken and its connection: the one whose answer may ask again, consumed by the
    // next PrepareNow whatever it is, and withdrawn by CancelNow.
    (MachineUpdateNowChoice Choice, long Connection)? nowTaken;
    bool retired;
    public bool CanUpdateNow => !retired && !busy && !machine.IsLocal && !machine.Offline
        && machine.Host.State == LinkState.Connected && machine.Host.Can(UpdateProtocol.Capability)
        && State is { CanUpdateNow: true, Available: not null };
    bool NowCurrent => CanUpdateNow && ReferenceEquals(State, nowState) && machine.Host.ConnectionNumber == nowConnection
        && machine.Host.Sessions.Snapshots == nowSnapshot && machine.Current == nowWasCurrent;

    // Preparation only: the UI/send slice consumes a choice, never sends from this model. again: the
    // machine's answer to the press of; only confirm, or policy-unreadable once to the press last
    // taken (#3307), asks again.
    public MachineUpdateNowPrompt? PrepareNow(UpdateCommandResult? again = null, MachineUpdateNowChoice? of = null)
    {
        Close();
        var taken = nowTaken;
        nowTaken = null;
        if (!CanUpdateNow) { return null; }
        var confirm = again is { Outcome: UpdateAck.Confirm };
        var policy = again is { Outcome: UpdateAck.Refused, Code: UpdateCode.PolicyUnreadable };
        var ours = taken is { } t && ReferenceEquals(t.Choice, of) && t.Choice.Release == State?.Available
            && t.Connection == machine.Host.ConnectionNumber;
        if ((again is not null && !confirm && !policy) || (policy && (!ours || of!.Unchecked))) { return null; }
        var uncheckedPolicy = again is not null && ours && (policy || of!.Unchecked);
        var state = confirm ? again!.State : policy ? again!.State ?? State : State;
        if (state is not { CanUpdateNow: true, Available: { } release } || release != State?.Available) { return null; }
        var b = UpdateNowWords.Read(state.Busy);
        if (!machine.Current) { b = b with { Unknown = true }; }
        var sheet = confirm || uncheckedPolicy || !UpdateNowWords.Quiet(b)
            ? UpdateNowWords.Sheet(Name, b, id => machine.Host.Sessions.Get(id) is { } s
                ? string.IsNullOrEmpty(s.Title) ? "Untitled session" : s.Title : null, uncheckedPolicy)
            : null;
        nowState = State;
        nowConnection = machine.Host.ConnectionNumber;
        nowSnapshot = machine.Host.Sessions.Snapshots;
        nowWasCurrent = machine.Current;
        return nowPrompt = new(release, sheet, uncheckedPolicy);
    }

    public MachineUpdateNowChoice? TakeNow(MachineUpdateNowPrompt prompt, bool confirmed = false)
    {
        if (!ReferenceEquals(prompt, nowPrompt)) { return null; }
        if (!NowCurrent || prompt.Cancelled || (prompt.Sheet is not null && !confirmed)) { CancelNow(); return null; }
        nowPrompt = null;
        var choice = new MachineUpdateNowChoice(prompt.Release, prompt.Sheet is { } sheet ? UpdateNowWords.AcceptOf(sheet.Busy) : null, prompt.Unchecked);
        nowTaken = (choice, nowConnection);
        return choice;
    }

    public void CancelNow()
    {
        nowTaken = null;
        Close();
    }

    void Close()
    {
        var prompt = nowPrompt;
        nowPrompt = null;
        if (prompt is not null) { prompt.Cancel(); }
    }

    internal void Retire() { retired = true; CancelNow(); }

    public void Check() => _ = AskAsync(UpdateProtocol.Check);
    public void Install() => _ = AskAsync(UpdateProtocol.Install);

    // The machine's answer: Accepted, its app takes it from there; or why not.
    public async Task AskAsync(string op)
    {
        if (busy) { return; }
        busy = true;
        note = null;
        Changed();
        try
        {
            var result = await machine.Host.ReadAsync<UpdateCommandResult>(op, new UpdateCommand(Guid.NewGuid().ToString()), TimeSpan.FromSeconds(15));
            note = result.Outcome == UpdateAck.Accepted ? null : Line(result.Reason ?? "it refused");
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or OperationCanceledException or InvalidOperationException
            or System.Text.Json.JsonException or ObjectDisposedException)
        {
            note = $"{Name} did not take it: {Words.Sentence(e.Message)}";
        }
        busy = false;
        Changed();
    }

    // A line of its own, which a host's reason (written to follow a colon) starts.
    static string Line(string s)
    {
        var t = Words.Sentence(s);
        return t.Length > 0 ? char.ToUpperInvariant(t[0]) + t[1..] : t;
    }

    // The machine's state changed, or a second passed (the "checked 3 minutes ago").
    internal void Changed()
    {
        if (!NowCurrent) { Close(); }
        Raise(nameof(CanUpdateNow));
        Raise(nameof(Name));
        Raise(nameof(StatusText));
        Raise(nameof(LastText));
        Raise(nameof(HasLast));
        Raise(nameof(Note));
        Raise(nameof(HasNote));
        Raise(nameof(CanCheck));
        Raise(nameof(CanInstall));
        Raise(nameof(InstallName));
        Raise(nameof(CheckName));
    }
}

public sealed class MachineUpdateNowPrompt(string release, UpdateNowSheet? sheet, bool uncheckedPolicy = false) : Observable
{
    public string Release { get; } = release;
    public UpdateNowSheet? Sheet { get; } = sheet;
    public bool Unchecked { get; } = uncheckedPolicy;
    public bool Cancelled { get; private set; }
    internal void Cancel() { Cancelled = true; Raise(nameof(Cancelled)); }
}

public sealed record MachineUpdateNowChoice(string Release, UpdateBusy? Accept, bool Unchecked = false);
