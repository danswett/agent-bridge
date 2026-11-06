using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The status glyphs (docs/design/ui.md §2.1), drawn by the app's StatusGlyph control from the
// generated AbGlyph* styles.
public enum StatusGlyph { Idle, Working, Delegating, Needs, Done, Failed, Ended }

// The colour role a status's words take (ui.md §3): needs' text for a question, agent's text
// while an agent drives, secondary while ending, otherwise the plain text colour. Words are
// never in active or neutral, which are glyph colours.
public enum StatusTone { Plain, Needs, Agent, Muted }

// What a session says about itself: the rule that matched, its glyph, the card's state (View)
// and its facts ("Waiting on you", "2 delegates still running"). Driven is rule 7's overlay.
// The facts stay apart until they are shown: a delegate's name is part of a fact, and cutting
// the joined words at the separator let a delegate named "Idle · Read logs" set its row's status
// to "Delegate Idle" (#286).
public sealed record StatusLine(int Rule, StatusGlyph Glyph, string View, IReadOnlyList<string> Facts, StatusTone Tone, bool Driven)
{
    public const string Separator = " · ";

    // The words as the header and Details show them.
    public string Text => string.Join(Separator, Facts);

    // What a row shows (ui.md §6.1): "Waiting on you · 2 delegates still running" is one fact
    // there and both in the header.
    public string FirstFact => Facts.Count > 0 ? Facts[0] : "";

    // The words as they read mid-sentence: a delegate's name and a number keep their case. Built
    // from the facts, each with no comma of its own: Label joins with ", ", and a name of "x,
    // idle, DEV-VM2" would read as facts to a screen reader (#303's security review).
    public string Spoken
    {
        get
        {
            var text = string.Join(Separator, Facts.Select(DelegateLabel.Aloud));
            return Rule == 2 || text.Length == 0 || char.IsDigit(text[0]) ? text : char.ToLowerInvariant(text[0]) + text[1..];
        }
    }

    // The glyph's accessible name (ui.md §9): "Plan Migration to Windows App, delegating to 3
    // agents, DEV-VM1", with the driver said in words, since the diamond is only a shape.
    public string Label(string? title, string? machine) =>
        string.Join(", ", new[] { string.IsNullOrEmpty(title) ? "Untitled session" : title, Spoken, Driven ? "driven by an agent" : null, machine }.Where(s => !string.IsNullOrEmpty(s)));
}

// docs/design/ui.md §2.2, as pwa/js/status.js has it: rules 1-6 first match wins, rule 8
// refines rule 4, rules 10-12 are #800's, and rule 7 is an overlay on whatever they chose. SessionStatusTests names a
// test after each rule.
public static class StatusRules
{
    // Not a row of §2.2: the host is ending the session.
    public const int Ending = 0;
    // Nor this: the session's machine is away (#269), and the row is what it last said, not to be
    // acted on. The host is unreachable, which is what `failed` says of a host (§2.1).
    public const int Away = -1;
    // #800 (Session.attention, AuthState): the agent needs sign-in (or is signed in and waiting for the
    // session to confirm), after rules 1 and 2; its latest turn ended on an error, before rules 4 and
    // 5; it has stopped answering, refining rule 3.
    public const int SignIn = 10;
    public const int Error = 11;
    public const int NotResponding = 12;

    public static StatusLine Offline(DateTimeOffset since, DateTimeOffset now) =>
        Line(Away, StatusGlyph.Failed, "offline", StatusTone.Muted, "Offline", $"last seen {Words.Ago(since, now)}");

    public static StatusLine Reconnecting() => Line(Away, StatusGlyph.Failed, "offline", StatusTone.Muted, "Reconnecting…");

    public static StatusLine For(Session s)
    {
        var line = Pick(s);
        // Driven: the agent's tone, except over rules 10-12 (#800), which keep theirs (the PWA's too).
        return s.Driver == DriverKind.Agent ? line with { Driven = true, Tone = line.Rule is SignIn or Error or NotResponding ? line.Tone : StatusTone.Agent } : line;
    }

    static StatusLine Pick(Session s)
    {
        var q = s.Question;
        var atMachine = q?.AnswerAtMachine == true ? "answer at the machine" : null;
        if (s.Status == SessionStatus.Ending) { return Line(Ending, StatusGlyph.Ended, "ending", StatusTone.Muted, "Ending session…"); }
        // The session's own question asks (rule 1), or a delegate's (rule 2). The session's own wins
        // at the host, which then sends no waitingOn.
        var asker = s.Status == SessionStatus.Waiting ? s.WaitingOn ?? q?.Agent : null;
        if (s.Status == SessionStatus.Waiting && asker is null)
        {
            var n = RunningDelegates(s);
            return Line(1, StatusGlyph.Needs, "waiting", StatusTone.Needs, "Waiting on you", n > 0 ? $"{Words.Plural(n, "delegate", "delegates")} still running" : null, atMachine);
        }
        // #800: what the host says is wrong with the session, shown over its status, which stays
        // truthful underneath. Never idle, working or a stale age for any of them.
        var at = AuthState.Of(s);
        // Rule 10, confirming: signed in, and the host hasn't yet seen the model answer (recovery
        // pending). Not waiting on you, and not recovered either: one fact, so the row says all of it.
        // After the session's own question and before a delegate's (ui.md §6.13): the main agent
        // can't run, which a delegate's question would otherwise hide.
        if (AuthState.Confirming(at)) { return Line(SignIn, StatusGlyph.Idle, "confirming", StatusTone.Plain, "Signed in · waiting for the session to confirm"); }
        // Rule 10: the agent can't run until a person signs it in. It waits on you as a question
        // does: "Needs sign-in · Copilot · @octocat".
        if (AuthState.NeedsSignIn(at))
        {
            return Line(SignIn, StatusGlyph.Needs, "signin", StatusTone.Needs, "Needs sign-in", s.Agent == AgentKind.Unknown ? null : AuthState.AgentName(s.Agent), at!.Account is { } account ? $"@{account}" : null);
        }
        if (asker is not null)
        {
            // Rule 2: a delegate's question, named for the delegate after the word Delegate, which
            // "Main agent asks" never has.
            var name = s.Subagents?.FirstOrDefault(a => a.Id == asker) is { } agent ? DelegateLabel.Titled(DelegateLabel.For(agent.Name, agent.Id)) : "A delegate";
            return Line(2, StatusGlyph.Needs, "waiting", StatusTone.Needs, $"{name} asks", atMachine);
        }
        // Rule 12, refining rule 3: the host has heard nothing from a working session past its
        // threshold. The failed glyph, and no button until there is something to do.
        if (at?.Kind == AuthState.NotResponding)
        {
            return Line(NotResponding, StatusGlyph.Failed, "stalled", StatusTone.Needs, "Not responding", AuthState.Clock(at.LastEventAt ?? at.Since) is { } since ? $"since {since}" : null);
        }
        if (s.Status == SessionStatus.Working)
        {
            var task = ForegroundTask(s);
            return Line(3, StatusGlyph.Working, "working", StatusTone.Plain, "Working", task is null ? null : $"task: {DelegateLabel.Titled(DelegateLabel.For(task.Name, task.Id))}");
        }
        var b = s.Activity.Background;
        // Rule 11, before rules 4 and 5: the latest turn ended on an agent error. Background work that
        // survived the turn never hides it behind "Delegating" (ui.md §6.13). The row says its class in
        // the app's own words; the host's message is in Details only, as plain text.
        if (at?.Kind == AuthState.Error)
        {
            // Every delegate still running, task ones too, which can survive the failed turn (v1-sessions.md).
            var running = RunningDelegates(s);
            return Line(Error, StatusGlyph.Failed, "error", StatusTone.Needs, AuthState.ErrorWords(at.ErrorClass), running > 0 ? $"{Words.Plural(running, "delegate", "delegates")} still running" : null);
        }
        if (b is { Agents: > 0 })
        {
            // The host's count is at any depth and never trimmed: a nested delegate counts once,
            // and the count holds when the list is cut short.
            var text = $"Delegating to {Words.Plural(b.Agents, "agent", "agents")}";
            // Rule 8: delegation the host counts but can't list (Claude and Codex until #84's
            // later slices, or a host older than the list). Nothing is attributed.
            if (s.Subagents is not { Count: > 0 } && s.SubagentsMore is not > 0) { return Line(8, StatusGlyph.Delegating, "delegating", StatusTone.Plain, text, "details not reported"); }
            return Line(4, StatusGlyph.Delegating, "delegating", StatusTone.Plain, text);
        }
        if (b is { Shells: > 0 }) { return Line(5, StatusGlyph.Delegating, "delegating", StatusTone.Plain, Words.Plural(b.Shells, "background command", "background commands")); }
        return Line(6, StatusGlyph.Idle, "idle", StatusTone.Plain, "Idle");
    }

    static StatusLine Line(int rule, StatusGlyph glyph, string view, StatusTone tone, params string?[] facts) =>
        new(rule, glyph, view, facts.OfType<string>().ToArray(), tone, false);

    static bool Running(Subagent a) => a.Status is SubagentStatus.Working or SubagentStatus.Waiting;

    // Delegates still running beside the session's own question. The host's background count is
    // at any depth and never trimmed, but counts background agents only; task agents come from
    // the list (which a budget may trim). The listed background ones stand in for the count when
    // a host sends none, and are never counted twice.
    static int RunningDelegates(Session s)
    {
        var listed = s.Subagents?.Where(Running).ToList() ?? [];
        var background = listed.Count(a => a.Kind == SubagentKind.Background);
        return Math.Max(s.Activity.Background?.Agents ?? 0, background) + (listed.Count - background);
    }

    // The task agent running as part of the main agent's own turn: a running task entry whose
    // starters are all task agents too. One a background delegate started is that delegate's
    // work. The list is newest first, so this is the latest to start.
    static Subagent? ForegroundTask(Session s)
    {
        var list = s.Subagents ?? [];
        var byId = new Dictionary<string, Subagent>();
        foreach (var a in list) { byId.TryAdd(a.Id, a); }
        bool Foreground(Subagent a)
        {
            for (var (p, seen) = (a.Parent, 0); p is not null; seen++)
            {
                if (!byId.TryGetValue(p, out var parent) || parent.Kind != SubagentKind.Task || seen > list.Count) { return false; }
                p = parent.Parent;
            }
            return true;
        }
        return list.FirstOrDefault(a => a.Kind == SubagentKind.Task && Running(a) && !string.IsNullOrEmpty(a.Name) && Foreground(a));
    }
}
