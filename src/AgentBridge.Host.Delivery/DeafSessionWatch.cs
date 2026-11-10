using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Host.Core.Restore;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

// What suggests a Copilot session's host has stopped taking messages (#687): the CLI's own watchdog
// gave up on an event after 120 s and the session stays deaf until it is resumed.
public enum DeafSignal
{
    // A reply typed into an idle session that the agent never recorded.
    UnconfirmedWhileIdle,
    // A reply that took longer than DeafOptions.SlowTyping to type, at a time the session was idle.
    SlowTyping,
    // The session's own error says its host did not acknowledge an event.
    HostError,
    // A reply that went in and was recorded promptly: the session is hearing.
    Healthy,
}

public sealed class DeafOptions
{
    public TimeSpan SlowTyping { get; init; } = TimeSpan.FromSeconds(20);
}

// Decides, from signals alone, whether a session is deaf. Pure of I/O so it is tested directly.
//   * A host error is conclusive.
//   * An unconfirmed reply on an idle session is conclusive: nothing was working that could explain
//     the silence.
//   * One slow typing is not (a busy machine does that); two in a row are.
//   * A prompt, recorded reply clears what was counted.
public sealed class DeafPolicy
{
    readonly Lock gate = new();
    readonly Dictionary<string, int> slow = new(StringComparer.Ordinal);
    readonly HashSet<string> recovered = new(StringComparer.Ordinal);

    public bool Note(string sessionId, DeafSignal signal)
    {
        lock (gate)
        {
            switch (signal)
            {
                case DeafSignal.Healthy:
                    slow.Remove(sessionId);
                    recovered.Remove(sessionId);
                    return false;
                case DeafSignal.SlowTyping:
                    slow[sessionId] = slow.GetValueOrDefault(sessionId) + 1;
                    if (slow[sessionId] < 2) { return false; }
                    break;
            }
            // Once recovered, wait for a reply it heard: a session deaf again at once has a cause a restart does not cure.
            if (recovered.Contains(sessionId)) { return false; }
            return true;
        }
    }

    // A recovery was started: no other until the session hears a reply, and the signals that led to it forgotten.
    public void Recovering(string sessionId)
    {
        lock (gate)
        {
            slow.Remove(sessionId);
            recovered.Add(sessionId);
        }
    }

    // What a deaf session may be ended and resumed with: nothing it is doing would be lost, and
    // nothing a person has begun is in the way. Null when it is safe, else why not.
    public static string? Unsafe(Session s) =>
        s.Agent != AgentKind.Copilot ? "not a Copilot session"
        : s.Status != SessionStatus.Idle ? "the session is not idle"
        : s.Activity.CurrentTool is { Status: ToolStatus.Running } ? "a tool is running"
        : s.Activity.Background is not null ? "background work is running"
        : s.Subagents?.Any(a => !a.Finished) == true ? "a sub-agent is running"
        : s.Question is not null ? "the session is asking a question"
        : null;
}

// Ends a deaf Copilot session and resumes it, only when that costs nothing: no tool running, no
// background work, no question, and the input box on screen empty (nothing a person has typed is
// lost). Ending goes through StopService, with all its checks (G5, own user, no elevated process,
// the delivery gate); resuming through LaunchService.ResumeAsync, with all of its. It cannot bring
// back a result the CLI lost; it brings back a session that takes messages again. Never logs words
// from the session.
public sealed class DeafSessionWatch
{
    readonly ISessionSource source;
    readonly IConsoleInjector injector;
    readonly ISessionStopper stopper;
    readonly ISessionResumer resumer;
    readonly DeafPolicy policy;
    readonly Action<string> log;
    readonly Lock gate = new();
    readonly HashSet<string> recovering = new(StringComparer.Ordinal);

    public DeafSessionWatch(ISessionSource source, IConsoleInjector injector, ISessionStopper stopper, ISessionResumer resumer, DeafOptions? options = null, Action<string>? log = null)
    {
        this.source = source;
        this.injector = injector;
        this.stopper = stopper;
        this.resumer = resumer;
        policy = new DeafPolicy();
        this.log = log ?? (_ => { });
        slowAfter = (options ?? new DeafOptions()).SlowTyping;
    }

    readonly TimeSpan slowAfter;

    // For a Copilot reply that reached its confirmation: whether it was working when typed, when the
    // typing finished in ms after receipt, and whether the agent recorded it.
    public Task NoteReplyAsync(string sessionId, AgentKind agent, bool workingWhenTyped, long? typedMs, AnswerOutcome outcome)
    {
        if (agent != AgentKind.Copilot || workingWhenTyped) { return Task.CompletedTask; }
        var signal = outcome == AnswerOutcome.Unconfirmed ? DeafSignal.UnconfirmedWhileIdle
            : typedMs is { } t && t > slowAfter.TotalMilliseconds ? DeafSignal.SlowTyping
            : DeafSignal.Healthy;
        return NoteAsync(sessionId, signal);
    }

    // For each change to a session: its own error naming the host's missed acknowledgement.
    // Once for each error: the session is listed again with it on every change that follows.
    readonly Dictionary<string, DateTimeOffset?> errorsSeen = new(StringComparer.Ordinal);

    public Task NoteSessionAsync(Session s)
    {
        if (s.Agent != AgentKind.Copilot || s.Attention is not { Kind: AttentionKind.Error, Message: { } m } a
            || !string.Equals(m, AgentErrors.HostWedgeMessage, StringComparison.Ordinal))
        {
            // The error is gone: forget it so the table holds only sessions now showing one.
            lock (gate) { errorsSeen.Remove(s.Id); }
            return Task.CompletedTask;
        }
        lock (gate)
        {
            if (errorsSeen.TryGetValue(s.Id, out var since) && since == a.Since) { return Task.CompletedTask; }
            errorsSeen[s.Id] = a.Since;
        }
        return NoteAsync(s.Id, DeafSignal.HostError);
    }

    public async Task NoteAsync(string sessionId, DeafSignal signal)
    {
        try
        {
            if (!policy.Note(sessionId, signal)) { return; }
            lock (gate) { if (!recovering.Add(sessionId)) { return; } }
            try { await RecoverAsync(sessionId, signal); }
            finally { lock (gate) { recovering.Remove(sessionId); } }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"deaf-session recovery of {Short(sessionId)} failed: {e.GetType().Name}");
        }
    }

    async Task RecoverAsync(string sessionId, DeafSignal signal)
    {
        if (source.Get(sessionId) is not { } session) { return; }
        if (DeafPolicy.Unsafe(session) is { } why) { log($"{Short(sessionId)} looks deaf ({signal}) but is left alone: {why}"); return; }
        if (session.ProcessId is not { } pid) { log($"{Short(sessionId)} looks deaf ({signal}) but its process is not known"); return; }
        if (!stopper.Available) { log($"{Short(sessionId)} looks deaf ({signal}) but ending sessions is not available now"); return; }

        // A screen that cannot be read cannot say nothing has been typed.
        ConsoleScreen screen;
        try { screen = await injector.ReadScreenAsync(pid); }
        catch (Exception) { log($"{Short(sessionId)} looks deaf ({signal}) but its screen could not be read"); return; }
        if (screen.Rows is not { } rows || CopilotInputBoxReader.Read(rows).Box is null)
        {
            log($"{Short(sessionId)} looks deaf ({signal}) but its input box is not empty or not readable, so it is left alone");
            return;
        }

        // The read took time: a tool, sub-agent or question may have started during it.
        var after = source.Get(sessionId) is { } now ? DeafPolicy.Unsafe(now) : "it is gone";
        if (after is not null) { log($"{Short(sessionId)} looks deaf ({signal}) but is left alone: {after}"); return; }

        log($"{Short(sessionId)} looks deaf ({signal}); ending it to resume it");
        var ended = await stopper.StopAsync(new StopRequest { Id = sessionId, Request = Guid.NewGuid().ToString("D") });
        if (ended.Outcome != StopOutcome.Ended) { log($"{Short(sessionId)} was not ended ({ended.Outcome}); not resumed, and it may be tried again"); return; }
        // Only an ended session counts as recovered; one that could not be ended is tried again.
        policy.Recovering(sessionId);
        // Null launch: the machine's defaults. The host does not know the old permission mode, so
        // it is not carried over, and a resumed session is never given wider rights than before.
        var resumed = await resumer.ResumeAsync(new ResumeAsk(sessionId, AgentKind.Copilot, null, "host-deaf-session"));
        log($"{Short(sessionId)} resumed after being deaf: {resumed.Outcome.ToString().ToLowerInvariant()}");
    }

    static string Short(string id) => id.Length > 8 ? id[..8] : id;
}
