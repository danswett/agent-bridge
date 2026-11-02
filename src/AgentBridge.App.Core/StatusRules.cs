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
// and its words. Driven is rule 7's overlay.
public sealed record StatusLine(int Rule, StatusGlyph Glyph, string View, string Text, StatusTone Tone, bool Driven)
{
    // The words as they read mid-sentence: a delegate's name and a number keep their case.
    public string Spoken => Rule == 2 || Text.Length == 0 || char.IsDigit(Text[0]) ? Text : char.ToLowerInvariant(Text[0]) + Text[1..];

    // The glyph's accessible name (ui.md §9): "Plan Migration to Windows App, delegating to 3
    // agents, DEV-VM1", with the driver said in words, since the diamond is only a shape.
    public string Label(string? title, string? machine) =>
        string.Join(", ", new[] { string.IsNullOrEmpty(title) ? "Untitled session" : title, Spoken, Driven ? "driven by an agent" : null, machine }.Where(s => !string.IsNullOrEmpty(s)));
}

// docs/design/ui.md §2.2, as pwa/js/status.js has it: rules 1-6 first match wins, rule 8
// refines rule 4, and rule 7 is an overlay on whatever they chose. SessionStatusTests names a
// test after each rule.
public static class StatusRules
{
    // Not a row of §2.2: the host is ending the session.
    public const int Ending = 0;

    public static StatusLine For(Session s)
    {
        var line = Pick(s);
        return s.Driver == DriverKind.Agent ? line with { Driven = true, Tone = StatusTone.Agent } : line;
    }

    static StatusLine Pick(Session s)
    {
        var q = s.Question;
        var atMachine = q?.AnswerAtMachine == true ? " · answer at the machine" : "";
        if (s.Status == SessionStatus.Ending) { return new(Ending, StatusGlyph.Ended, "ending", "Ending session…", StatusTone.Muted, false); }
        if (s.Status == SessionStatus.Waiting)
        {
            // Rule 2: a delegate's question, named for the delegate. The session's own question
            // wins over it at the host, which then sends no waitingOn.
            if ((s.WaitingOn ?? q?.Agent) is { } asker)
            {
                var name = s.Subagents?.FirstOrDefault(a => a.Id == asker)?.Name ?? "A delegate";
                return new(2, StatusGlyph.Needs, "waiting", $"{name} asks{atMachine}", StatusTone.Needs, false);
            }
            var n = RunningDelegates(s);
            return new(1, StatusGlyph.Needs, "waiting", $"Waiting on you{(n > 0 ? $" · {Words.Plural(n, "delegate", "delegates")} still running" : "")}{atMachine}", StatusTone.Needs, false);
        }
        if (s.Status == SessionStatus.Working)
        {
            var task = ForegroundTask(s);
            return new(3, StatusGlyph.Working, "working", task is null ? "Working" : $"Working · task: {task.Name}", StatusTone.Plain, false);
        }
        var b = s.Activity.Background;
        if (b is { Agents: > 0 })
        {
            // The host's count is at any depth and never trimmed: a nested delegate counts once,
            // and the count holds when the list is cut short.
            var text = $"Delegating to {Words.Plural(b.Agents, "agent", "agents")}";
            // Rule 8: delegation the host counts but can't list (Claude and Codex until #84's
            // later slices, or a host older than the list). Nothing is attributed.
            if (s.Subagents is not { Count: > 0 } && s.SubagentsMore is not > 0) { return new(8, StatusGlyph.Delegating, "delegating", $"{text} · details not reported", StatusTone.Plain, false); }
            return new(4, StatusGlyph.Delegating, "delegating", text, StatusTone.Plain, false);
        }
        if (b is { Shells: > 0 }) { return new(5, StatusGlyph.Delegating, "delegating", Words.Plural(b.Shells, "background command", "background commands"), StatusTone.Plain, false); }
        return new(6, StatusGlyph.Idle, "idle", "Idle", StatusTone.Plain, false);
    }

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
