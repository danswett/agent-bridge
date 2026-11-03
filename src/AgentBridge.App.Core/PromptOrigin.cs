using System.Globalization;
using System.Text;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// A prompt the person didn't type (#489, ui.md §6.4): a schedule's, another agent's, or one the
// host knows only as not the person's (activity.userMessageFrom). It is never drawn as the
// person's bubble, but as a line of its own in agent colour, collapsed; the person's own prompt,
// and every prompt from a host that doesn't send the field, is unchanged. The PWA's
// pwa/js/prompt-origin.js follows the same rules, case for case.
//   Schedule: a clock, then the label line's facts ("Scheduled prompt", "#4", "same as last time").
//   Driven: the driven-badge diamond (an agent's, or other).
//   By: secondary text after the facts (the agent driving, when the host knows).
//   Spoken: what the label reads as aloud, before the disclosure's words.
//   Preview: the prompt on one line, for the two-line preview; Text: all of it, for Show prompt.
// Every word an agent chose has gone through Fact or NeedsYouEntry.Reveal: no hidden or bidi
// character is drawn as nothing, and no label can read as the person's or the main agent's.
public sealed record PromptOrigin(UserMessageFrom From, IReadOnlyList<string> Facts, string? By, string Spoken, bool Repeat, string Text, string Preview)
{
    // The protocol's cap on userMessageLabel, held here too: an agent's text, from a host that
    // might not.
    public const int LabelMaxBytes = 64;

    public bool Schedule => From == UserMessageFrom.Schedule;
    public string Label => string.Join(" · ", Facts);

    // The disclosure's name (ui.md §9): whose prompt, then the words on it, "Scheduled prompt 4,
    // Show prompt"; the Expander says collapsed or expanded after it. The words shown stay in the
    // name (WCAG 2.5.3, label in name), and the state is said once.
    public string DisclosureName(bool open) => $"{Spoken}, {(open ? "Hide prompt" : "Show prompt")}";

    public static PromptOrigin? Of(Session s, bool repeat = false)
    {
        var a = s.Activity;
        var from = a.UserMessageFrom ?? UserMessageFrom.Person;
        if (from == UserMessageFrom.Person || string.IsNullOrEmpty(a.UserMessage)) { return null; }
        var text = NeedsYouEntry.Reveal(a.UserMessage);
        var preview = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        switch (from)
        {
            case UserMessageFrom.Schedule:
                var name = ScheduleName(a.UserMessageLabel);
                var facts = new List<string> { "Scheduled prompt" };
                if (name is not null) { facts.Add(name); }
                if (repeat) { facts.Add("same as last time"); }
                return new(from, facts, null, DelegateLabel.Aloud(name is null ? "Scheduled prompt" : $"Scheduled prompt {name.TrimStart('#')}"), repeat, text, preview);
            case UserMessageFrom.Agent:
                var by = s.Driver == DriverKind.Agent && Fact(s.DriverPrincipal, 128) is { Length: > 0 } p ? p : null;
                return new(from, ["Sent by an agent"], by, "Prompt sent by an agent", false, text, preview);
            default:
                return new(UserMessageFrom.Other, ["Automatic prompt"], null, "Automatic prompt", false, text, preview);
        }
    }

    // A schedule's label as drawn: "#4" for a number, and anything else in quotes, so no label of
    // an agent's reads as words of the line's own ("Scheduled prompt · “Main agent”"), whatever
    // letters it draws.
    internal static string? ScheduleName(string? raw)
    {
        var label = Fact(raw);
        return label.Length == 0 ? null : label.All(char.IsAsciiDigit) ? $"#{label}" : DelegateLabel.Quoted(label);
    }

    // An agent's short text, drawn as a fact on a label line: nothing hidden or steering, on one
    // line, no middle dot of its own (which would add a fact: "4 · same as last time"), and cut to
    // max UTF-8 bytes at a whole character, the ellipsis included.
    public static string Fact(string? text, int max = LabelMaxBytes)
    {
        var clean = DelegateLabel.WithoutDots(DelegateLabel.Clean(text, int.MaxValue));
        if (Encoding.UTF8.GetByteCount(clean) <= max) { return clean; }
        var b = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(clean);
        while (e.MoveNext())
        {
            var g = e.GetTextElement();
            if (Encoding.UTF8.GetByteCount(b + g + "…") > max) { break; }
            b.Append(g);
        }
        return b.ToString().TrimEnd() + "…";
    }
}

// What a session's view remembers of its prompts, to fold a schedule's repeats and date them. A
// session carries only its latest prompt, so "the same as last time" is what this view saw. A new
// prompt is one whose words or sender changed, or, with the same words, a finished turn more than
// this view saw once that prompt had settled (been seen idle, its own turn counted). turnCount is
// read only while idle, where it is every prompt sent; while a turn runs it can be one less or
// not (a turn no prompt started, such as Claude's for a notice, takes none off). So a schedule
// sending the same text again shows once its turn is done, and turns that end before a prompt
// has settled are never put down to it, since they can be its own or an earlier prompt's whose end
// this view missed. It repeats when its schedule's previous prompt, seen here, had the same text.
// Seen for the first time, nothing is folded: a wrong "same as last time" would hide a prompt
// that changed. Its age is when its turn began (statusSince when it started running, seen here);
// a prompt first seen after its turn has no age to show. pwa/js/prompt-origin.js, case for case.
public sealed class PromptMemory
{
    // The prompt shown.
    (UserMessageFrom From, string? Label, string Text)? current;
    bool repeat;
    bool settled;
    DateTimeOffset? at;
    // The most turns seen finished, while idle; whether the session ran when last seen; and when the
    // turn seen running began, until a turn's end is accounted for.
    int? idle;
    bool wasRunning;
    // The turn count last seen, in any state.
    int? lastCount;
    DateTimeOffset? ran;
    readonly Dictionary<string, string> bySchedule = [];

    public readonly record struct Seen(bool Repeat, DateTimeOffset? At, bool Fresh);

    public Seen Observe(Session s)
    {
        var a = s.Activity;
        var from = a.UserMessageFrom ?? UserMessageFrom.Person;
        var text = a.UserMessage;
        var running = s.Status is SessionStatus.Working or SessionStatus.Waiting;
        int? count = a.TurnCount is >= 0 and int c ? c : null;
        // From when it began, not from a question answered or a resume within it; a count that
        // rose while it ran is a later turn begun, its idle between missed here.
        var later = wasRunning && count > lastCount;
        if (running && (!wasRunning || later)) { ran = s.StatusSince; }
        wasRunning = running;
        if (count is not null) { lastCount = count; }
        // A host that omits the words (SessionBudget can) says nothing of which prompt this is: the
        // one shown stays it, and the turns that end meanwhile wait to be put down to the words
        // that return.
        if (text is null) { return new(false, null, false); }
        int? turns = s.Status == SessionStatus.Idle && a.TurnCount is >= 0 and int n ? n : null;
        var more = 0;
        if (turns is { } t)
        {
            if (idle is { } before) { more = Math.Max(0, t - before); }
            idle = Math.Max(idle ?? -1, t);
        }
        var began = ran;
        if (turns is not null) { ran = null; }
        DateTimeOffset? when;
        bool nowSettled;
        var key = (from, a.UserMessageLabel, text);
        if (current != key)
        {
            when = running ? s.StatusSince : null;
            // Seen idle, its own turn is among those counted.
            nowSettled = turns is not null;
        }
        else
        {
            if (!settled || more == 0)
            {
                if (turns is not null) { settled = true; }
                return new(repeat, at, false);
            }
            // The same words sent again, its turn over now: dated by when that turn began.
            when = began;
            nowSettled = true;
        }
        repeat = false;
        if (from == UserMessageFrom.Schedule)
        {
            // By the label as drawn: two an agent wrote apart but drawn alike are one to the person,
            // and "same as last time" means the words last shown under it.
            var label = $"{from}\n{PromptOrigin.ScheduleName(a.UserMessageLabel)}";
            repeat = bySchedule.TryGetValue(label, out var last) && last == text;
            bySchedule[label] = text;
        }
        at = when;
        settled = nowSettled;
        var fresh = current is not null;
        current = key;
        return new(repeat, at, fresh);
    }
}
