using System.Globalization;

namespace AgentBridge.Host.Core.Move;

public sealed record MoveStep(string Name, DateTimeOffset At, long Ms);

public sealed record MoveBytes(long Session, long Workspace);

// The v1 MoveTelemetry shape (docs/protocol/v1-machine-load.md, #1717). Counts, sizes and times
// only: no session id content, path, title or transcript text can be in it.
public sealed record MoveTelemetry(
    string MoveId, string From, string To, string Session, string Outcome, string? Reason,
    IReadOnlyList<MoveStep> Steps, MoveBytes Bytes)
{
    // A step that was not reached is absent from Steps, never present with 0 ms.
    public bool Reached(string step) => Steps.Any(s => s.Name == step);

    public string? LastReached => Steps.Count == 0 ? null : Steps[^1].Name;

    // newOnline.At minus handoffRequested.At, both the target's clock; null unless both were reached.
    // The Ms values sum to it because each is the gap from the previous step taken.
    public long? TotalMs => Reached(MoveSteps.HandoffRequested) && Reached(MoveSteps.NewOnline)
        ? Steps.Sum(s => s.Ms) : null;

    // One line for the move's audit record.
    public string AuditLine()
    {
        var inv = CultureInfo.InvariantCulture;
        var parts = new List<string> { $"move {MoveId}", $"outcome {Outcome}" };
        if (Reason is not null) { parts.Add($"reason {Reason}"); }
        foreach (var s in Steps) { parts.Add($"{s.Name} +{s.Ms.ToString(inv)}ms"); }
        parts.Add(TotalMs is { } t ? $"total {t.ToString(inv)}ms" : "total not reached");
        if (Outcome != "online") { parts.Add(LastReached is { } l ? $"stopped after {l}" : "stopped before handoffRequested"); }
        foreach (var n in MoveSteps.All.Where(n => !Reached(n))) { parts.Add($"{n} not reached"); }
        parts.Add($"bytes session={Bytes.Session.ToString(inv)} workspace={Bytes.Workspace.ToString(inv)}");
        return string.Join("; ", parts);
    }
}

public static class MoveSteps
{
    public const string HandoffRequested = "handoffRequested";
    public const string HandoffConfirmed = "handoffConfirmed";
    public const string ArtifactsPushed = "artifactsPushed";
    public const string NewLaunched = "newLaunched";
    public const string NewOnline = "newOnline";

    // In order; the order is the contract's.
    public static readonly IReadOnlyList<string> All =
        [HandoffRequested, HandoffConfirmed, ArtifactsPushed, NewLaunched, NewOnline];

    public static readonly IReadOnlyList<string> Outcomes = ["launched", "online", "refused", "failed", "timeout"];
}

// Collects the five step times of one move. S2 calls Step as each step lands.
public sealed class MoveTelemetryRecorder
{
    readonly string _moveId, _from, _to, _session;
    readonly List<MoveStep> _steps = [];
    readonly object _gate = new();
    long _sessionBytes, _workspaceBytes;

    public MoveTelemetryRecorder(string moveId, string from, string to, string session)
    {
        _moveId = moveId; _from = from; _to = to; _session = session;
    }

    // A repeat of a step already taken keeps the first time. A step that comes before one already
    // taken is refused: the steps happen in order, so it would be a wiring mistake.
    public void Step(string name, DateTimeOffset at)
    {
        var index = MoveSteps.All.ToList().IndexOf(name);
        if (index < 0) { throw new ArgumentException($"unknown move step '{name}'", nameof(name)); }
        lock (_gate)
        {
            if (_steps.Count > 0)
            {
                var last = MoveSteps.All.ToList().IndexOf(_steps[^1].Name);
                if (index == last) { return; }
                if (index < last) { throw new InvalidOperationException($"{name} came after {_steps[^1].Name}"); }
            }
            // A clock that stepped back is a gap of 0, not a negative time.
            var ms = _steps.Count == 0 ? 0 : Math.Max(0, (long)(at - _steps[^1].At).TotalMilliseconds);
            _steps.Add(new MoveStep(name, at, ms));
        }
    }

    public void AddBytes(long session, long workspace)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(session);
        ArgumentOutOfRangeException.ThrowIfNegative(workspace);
        lock (_gate) { _sessionBytes += session; _workspaceBytes += workspace; }
    }

    public MoveTelemetry Finish(string outcome, string? reason = null)
    {
        if (!MoveSteps.Outcomes.Contains(outcome)) { throw new ArgumentException($"unknown move outcome '{outcome}'", nameof(outcome)); }
        lock (_gate)
        {
            return new MoveTelemetry(_moveId, _from, _to, _session, outcome, reason,
                _steps.ToArray(), new MoveBytes(_sessionBytes, _workspaceBytes));
        }
    }
}
