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
                var label = Fact(a.UserMessageLabel);
                // "#4" for a number, and anything else in quotes, so no label of an agent's reads as
                // words of the line's own ("Scheduled prompt · “Main agent”"), whatever letters it draws.
                var name = label.Length == 0 ? null : label.All(char.IsAsciiDigit) ? $"#{label}" : DelegateLabel.Quoted(label);
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
// this view has seen idle and can put down to the prompts it knows: a schedule sending the same
// text again shows once its turn is done, since a turn of the same prompt (an agent's reply to a
// delegate reporting back) looks the same while it runs. turnCount is read only while idle, where
// it is every prompt sent; while a turn runs it can be one less or not (a turn no prompt started,
// such as Claude's for a notice, takes none off). It repeats when its schedule's previous prompt,
// seen here, had the same text. Seen for the first time, nothing is folded: a wrong "same as last
// time" would hide a prompt that changed. Its age is when its turn began (statusSince, seen while
// it ran); a prompt first seen after its turn has no age to show. pwa/js/prompt-origin.js, case
// for case.
public sealed class PromptMemory
{
    string? current;
    bool currentSeen;
    bool repeat;
    DateTimeOffset? at;
    // The most turns seen finished, while idle; the prompts seen arrive whose turns it doesn't hold
    // yet; and when the latest turn seen running began.
    int? idle;
    int owed;
    DateTimeOffset? ran;
    readonly Dictionary<string, string> bySchedule = [];

    public readonly record struct Seen(bool Repeat, DateTimeOffset? At, bool Fresh);

    public Seen Observe(Session s)
    {
        var a = s.Activity;
        var from = a.UserMessageFrom ?? UserMessageFrom.Person;
        var text = a.UserMessage;
        var key = text is null ? null : $"{from}\n{a.UserMessageLabel}\n{text}";
        var running = s.Status is SessionStatus.Working or SessionStatus.Waiting;
        if (running) { ran = s.StatusSince; }
        // More than ever seen idle here: a host can say idle a moment before it counts the turn done.
        var more = 0;
        if (s.Status == SessionStatus.Idle && a.TurnCount is >= 0 and int turns)
        {
            if (idle is { } before) { more = Math.Max(0, turns - before); } else { owed = 0; }
            idle = Math.Max(idle ?? -1, turns);
        }
        DateTimeOffset? when;
        if (!currentSeen || current != key)
        {
            // New words: a turn it starts is still to be counted, or is among those just counted.
            if (running) { owed++; } else { more = Math.Max(0, more - 1); }
            owed = Math.Max(0, owed - more);
            when = running ? s.StatusSince : null;
        }
        else
        {
            var unexplained = more - owed;
            owed = Math.Max(0, owed - more);
            if (unexplained <= 0) { return new(repeat, at, false); }
            // The same words sent again, its turn over now: dated by the turn seen running before.
            when = ran;
            ran = null;
        }
        repeat = false;
        if (from == UserMessageFrom.Schedule && text is not null)
        {
            var label = a.UserMessageLabel ?? "";
            repeat = bySchedule.TryGetValue(label, out var last) && last == text;
            bySchedule[label] = text;
        }
        at = when;
        var fresh = currentSeen;
        (current, currentSeen) = (key, true);
        return new(repeat, at, fresh);
    }
}
