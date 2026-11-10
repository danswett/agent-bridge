using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Host.Delivery.Handoff;

public enum HandoffSessionState { Idle, Ended }

// What the host reads from an ended session's transcript when nobody can answer for it.
public sealed record HandoffDigest(IReadOnlyList<string> Done, IReadOnlyList<string> Open, IReadOnlyList<string> FilesTouched);

// One timed step. Names are the contract's (v1-machine-load.md, MoveTelemetry): At is the
// host's clock, Ms the time since the previous step (0 for the first).
public sealed record HandoffStep(string Name, DateTimeOffset At, long Ms);

// Confirmed carries the note's size; otherwise FailedStep names where it stopped and Reason says
// why in plain words. Steps is what ran, in order, for the move's timing (S5).
public sealed record HandoffResult(bool Confirmed, int? Bytes, string? FailedStep, string? Reason, IReadOnlyList<HandoffStep> Steps);

// Asks a session's agent to write its handoff note. Null: it did not answer in time.
public interface IHandoffAgent
{
    Task<string?> AskForNoteAsync(string sessionId, string prompt, CancellationToken cancellationToken);
}

// Reads an ended session's transcript, never writes it. Null: it cannot be read.
public interface IHandoffTranscript
{
    Task<HandoffDigest?> ReadAsync(string sessionId, CancellationToken cancellationToken);
}

// Keeps the note beside the session, never in it. A failed write leaves nothing behind.
public interface IHandoffStore
{
    Task WriteAsync(string sessionId, string note, CancellationToken cancellationToken);
}

public sealed class HandoffService(IHandoffAgent agent, IHandoffTranscript transcript, IHandoffStore store, TimeProvider clock)
{
    public const string RequestedStep = "handoffRequested";
    public const string ConfirmedStep = "handoffConfirmed";
    public const int MaxNoteBytes = 256 * 1024;
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public const string Prompt =
        "Write a handoff note for whoever continues this work on another machine, in plain text: " +
        "what was done, what is still open, and the files you touched. Reply with the note only.";

    static readonly Regex SafeId = new("^[A-Za-z0-9._-]{1,128}$", RegexOptions.Compiled);

    public async Task<HandoffResult> RunAsync(string sessionId, HandoffSessionState state, CancellationToken cancellationToken = default)
    {
        var steps = new List<HandoffStep>();
        Step(steps, RequestedStep);
        if (!SafeId.IsMatch(sessionId) || sessionId is "." or "..")
        {
            return Fail(steps, "handoff", "the session id is not one this host can keep a note for");
        }

        string? note;
        try
        {
            note = state == HandoffSessionState.Ended
                ? await FromTranscriptAsync(sessionId, cancellationToken)
                : await AskAgentAsync(sessionId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            note = null;
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return Fail(steps, "handoff", state == HandoffSessionState.Ended
                ? "the session's transcript could not be read, so no handoff was made"
                : "the session did not answer the handoff request, so no handoff was made");
        }

        var bytes = Encoding.UTF8.GetByteCount(note);
        if (bytes > MaxNoteBytes)
        {
            return Fail(steps, "handoff", "the handoff note is too large to keep");
        }

        try
        {
            await store.WriteAsync(sessionId, note, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(steps, "write", "the handoff note could not be saved; the session is unchanged");
        }

        Step(steps, ConfirmedStep);
        return new HandoffResult(true, bytes, null, null, steps);
    }

    async Task<string?> AskAgentAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AnswerTimeout);
        return await agent.AskForNoteAsync(sessionId, Prompt, timeout.Token);
    }

    async Task<string?> FromTranscriptAsync(string sessionId, CancellationToken cancellationToken)
    {
        var digest = await transcript.ReadAsync(sessionId, cancellationToken);
        if (digest is null)
        {
            return null;
        }

        var text = new StringBuilder("Handoff note (built from the transcript; the session had ended)\n");
        Section(text, "Done", digest.Done);
        Section(text, "Open", digest.Open);
        Section(text, "Files touched", digest.FilesTouched);
        return text.ToString();
    }

    static void Section(StringBuilder text, string title, IReadOnlyList<string> lines)
    {
        text.Append('\n').Append(title).Append(":\n");
        if (lines.Count == 0)
        {
            text.Append("- none recorded\n");
        }

        foreach (var line in lines)
        {
            text.Append("- ").Append(line).Append('\n');
        }
    }

    void Step(List<HandoffStep> steps, string name)
    {
        var now = clock.GetUtcNow();
        steps.Add(new HandoffStep(name, now, steps.Count == 0 ? 0 : (long)(now - steps[^1].At).TotalMilliseconds));
    }

    static HandoffResult Fail(List<HandoffStep> steps, string step, string reason) =>
        new(false, null, step, $"failed at {step}: {reason}", steps);
}
// Writes <session>.handoff.md into a folder of its own, through a temp file moved into place, so
// a failure never leaves a half note and the agent's transcript is never opened.
public sealed class FileHandoffStore(string folder) : IHandoffStore
{
    public async Task WriteAsync(string sessionId, string note, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var final = Path.Combine(folder, sessionId + ".handoff.md");
        var temp = final + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, note, new UTF8Encoding(false), cancellationToken);
            File.Move(temp, final, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}