namespace AgentBridge.App.Core;

// The five steps of a move to another machine, in the contract's order (docs/protocol/v1-machine-load.md §2).
public enum MoveStepName { HandoffRequested, HandoffConfirmed, ArtifactsPushed, NewLaunched, NewOnline }

public enum MoveStepState { Waiting, Working, Done, Failed, NotStarted }
public enum MovePhase { Moving, Failed, Moved }

// One event of a move's step stream: a step reached (ms since the one before) or a failure, in words.
public sealed record MoveEvent(MoveStepName? Reached, long? Ms, string? Failure, string? NewSessionId)
{
    public static MoveEvent Step(MoveStepName step, long? ms, string? newSessionId = null) => new(step, ms, null, newSessionId);
    public static MoveEvent Fail(string? reason) => new(null, null, reason, null);
}

// What a move's host will send once the step events exist (S5, #1712); until then, a script.
public static class FakeMoveSteps
{
    static readonly MoveStepName[] Order = [MoveStepName.HandoffRequested, MoveStepName.HandoffConfirmed, MoveStepName.ArtifactsPushed, MoveStepName.NewLaunched, MoveStepName.NewOnline];
    static readonly long?[] Times = [null, 3000, 9000, 2000, 4000];

    public static IEnumerable<MoveEvent> Succeeding() =>
        Order.Select((step, i) => MoveEvent.Step(step, Times[i], step == MoveStepName.NewOnline ? "fork-1" : null));

    public static IEnumerable<MoveEvent> FailingAt(MoveStepName failedStep, string reason) =>
        Succeeding().Take((int)failedStep).Append(MoveEvent.Fail(reason));
}

public sealed class MoveStepItem : Observable
{
    MoveStepState state;
    long? ms;
    string? reason;

    internal MoveStepItem(int index, string words) { Number = index + 1; Words = words; }

    public int Number { get; }
    public string Words { get; }
    public MoveStepState State { get => state; internal set { if (Set(ref state, value)) { Changed(); } } }
    public long? Ms { get => ms; internal set { if (Set(ref ms, value)) { Changed(); } } }
    public string? Reason { get => reason; internal set { if (Set(ref reason, value)) { Raise(nameof(HasReason)); Raise(nameof(Name)); } } }
    public bool HasReason => reason is not null;

    public string StateText => state switch
    {
        MoveStepState.Waiting => "waiting",
        MoveStepState.Failed => "failed",
        MoveStepState.NotStarted => "not started",
        MoveStepState.Working => ms > 60_000 ? "still working" : "working",
        _ => "done",
    };

    public string TimeText => state switch
    {
        MoveStepState.Done => $"{StateText} · {(ms is { } d ? Duration(d) : "–")}",
        MoveStepState.Working when ms is { } w => $"{StateText} · {Duration(w)}",
        _ => StateText,
    };

    public string Name => $"{Number} {Words}, {TimeText}" + (reason is null ? "" : $". {reason}");

    static string Duration(long ms)
    {
        var s = (ms + 500) / 1000;
        return s >= 60 ? $"{s / 60} min {s % 60} s" : $"{s} s";
    }

    void Changed() { Raise(nameof(StateText)); Raise(nameof(TimeText)); Raise(nameof(Name)); }
}

// The move strip's view model (ui.md §6.6.2 rule 13). Fed events; keeps no clock (Tick is the view's).
public sealed class MoveProgressViewModel : Observable
{
    const int Last = 4;
    readonly string source;
    readonly string target;
    int reached = -1;
    MovePhase phase;
    string? newSessionId;

    public MoveProgressViewModel(string source, string target)
    {
        this.source = source;
        this.target = target;
        Steps = [.. new[] { "Hand-off requested", "Hand-off confirmed", $"Files sent to {target}", "New session launched", "New session online" }.Select((w, i) => new MoveStepItem(i, w))];
        Steps[0].State = MoveStepState.Working;
    }

    public event Action<string, bool>? Announced;
    public event Action<string?>? GoToRequested;
    public event Action? TryAgainRequested;
    public event Action? Closed;

    public IReadOnlyList<MoveStepItem> Steps { get; }
    public MovePhase Phase { get => phase; private set { if (Set(ref phase, value)) { RaiseAll(); } } }
    public string? NewSessionId => newSessionId;

    public string Head => phase switch
    {
        MovePhase.Failed => $"Move to {target} didn't finish",
        MovePhase.Moved => $"Moved to {target}",
        _ => $"Moving from {source} to {target}",
    };

    public string RowText => phase == MovePhase.Moved ? $"Moved to {target}" : $"Moving to {target}";
    public bool IsMoving => phase == MovePhase.Moving;
    public bool AcceptsAnswers => phase == MovePhase.Failed;
    public string? SendNote => phase == MovePhase.Moving ? "Hand-off in progress" : null;
    public bool CanGoToIt => phase == MovePhase.Moved;

    public void Apply(MoveEvent e)
    {
        if (phase != MovePhase.Moving) { return; }
        if (e.Failure is not null || e.Reached is null)
        {
            Fail(e.Failure);
            return;
        }
        var step = (int)e.Reached.Value;
        if (step != reached + 1) { return; }
        reached = step;
        Steps[step].State = MoveStepState.Done;
        Steps[step].Ms = e.Ms;
        if (step == Last)
        {
            newSessionId = e.NewSessionId;
            Phase = MovePhase.Moved;
            Announced?.Invoke($"Moved to {target}", false);
            return;
        }
        Steps[step + 1].State = MoveStepState.Working;
        Announced?.Invoke(Steps[step].Words, false);
    }

    public void Tick(TimeSpan working)
    {
        if (phase == MovePhase.Moving && reached < Last) { Steps[reached + 1].Ms = (long)working.TotalMilliseconds; }
    }

    public void GoToIt() => GoToRequested?.Invoke(newSessionId);
    public void TryAgain() => TryAgainRequested?.Invoke();
    public void Close() => Closed?.Invoke();

    void Fail(string? reason)
    {
        var words = string.IsNullOrWhiteSpace(reason) ? $"{target} didn't finish this step. Pick another machine or try again." : reason;
        var at = reached + 1;
        for (var i = at + 1; i <= Last; i++) { Steps[i].State = MoveStepState.NotStarted; }
        Steps[at].State = MoveStepState.Failed;
        Steps[at].Reason = words;
        Phase = MovePhase.Failed;
        Announced?.Invoke($"Move to {target} didn't finish. {words}", true);
    }
}
