using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// Another of the account's machines' updates (#51, docs/protocol/v1-update.md): what it runs, a
// newer release once a check has found one, what its app is doing about it, how the last update
// ended, and Check and Install. Install asks that machine's app to install the latest release as
// soon as no session there is busy (or after six hours), as its automatic updates do: its host
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

    public string StatusText
    {
        get
        {
            if (State is not { } s) { return ""; }
            if (!s.Installer) { return Line(s.Unavailable ?? "its app is not running, so it cannot update now"); }
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
    public string? LastText => State?.Last is not { } l ? null : l.Outcome switch
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
            var result = await machine.Host.CallAsync<UpdateCommandResult>(op, new UpdateCommand(Guid.NewGuid().ToString()), TimeSpan.FromSeconds(15));
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
