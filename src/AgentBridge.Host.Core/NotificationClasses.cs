using AgentBridge.Protocol;

namespace AgentBridge.Host.Core;

// session.class (#913, v1-sessions.md "Notification class"): one policy for every client, decided
// here from what the session shows, so the Windows app, the PWA and Home Assistant never each
// grow their own rules again (#253, #610, #754, #764 were each one client's).
public static class NotificationClasses
{
    // First match wins:
    //   Ending: the person asked it to end, so nothing about it is news.
    //   A question (its own or, through status rule 2, a delegate's) or attention (sign-in, an
    //     errored turn, not responding): it needs the person. So does a session restored after a
    //     crash, while its restored notice is shown and nobody has acted on it (restoredAfterCrash,
    //     which the source decides: a prompt since the restore, or a turn started after it, ends it).
    //     After a reboot or a host restart it does not: those sessions need the person only through
    //     their own state (Product, 6689838536 Q1).
    //   Idle with an answer of the main agent's own: its turn is done. A delegate's text never
    //     counts, so a delegate finishing is never the session finishing (subagents.md §7).
    //   Anything else (working, or idle with nothing of the main agent's own said): FYI.
    // Watch, Mute and an agent-driven turn (#489) decide who is alerted for a class, not the class.
    public static NotificationClass Of(Session s, bool restoredAfterCrash = false)
    {
        if (s.Status == SessionStatus.Ending) { return NotificationClass.Fyi; }
        if (s.Question is not null || s.Attention is not null || CrashRestored(s, restoredAfterCrash)) { return NotificationClass.NeedsYou; }
        if (s.Status == SessionStatus.Idle && !string.IsNullOrWhiteSpace(MainResponse(s))) { return NotificationClass.Done; }
        return NotificationClass.Fyi;
    }

    // session.classSince: when the session entered its present tier, the start of the episode. While
    // the tier stays the same it keeps the start the host last sent (previous), whatever moves under
    // it: a delegate's question arriving beside the session's own, one answered as the next is asked,
    // working and idle within fyi. Each of those once restarted it, and so the 15-minute reminder
    // (#913 q9), which a new question while already needsYou must not (#975 review). Only a change of
    // tier, or a session the host has not sent before, takes it from the fields that put the session
    // there: for needsYou its oldest pending reason (a crash restore's being its time), otherwise
    // statusSince.
    public static DateTimeOffset Since(Session s, NotificationClass c, Session? previous = null, bool restoredAfterCrash = false)
    {
        if (previous is { Class: { } was, ClassSince: { } began } && was == c) { return began; }
        if (c != NotificationClass.NeedsYou) { return s.StatusSince; }
        DateTimeOffset? oldest = null;
        foreach (var at in new[] { s.Question?.AskedAt, s.Attention?.Since, CrashRestored(s, restoredAfterCrash) ? (DateTimeOffset?)s.Restored!.At : null })
        {
            if (at is { } t && (oldest is null || t < oldest)) { oldest = t; }
        }
        return oldest ?? s.StatusSince;
    }

    // Only while the session still carries the crash's restored notice: the flag alone says no one
    // has acted since, never which restore it was.
    static bool CrashRestored(Session s, bool restoredAfterCrash) => restoredAfterCrash && s.Restored is { Cause: RestoreCause.Crash };

    // The main agent's own latest answer, as PushNotifier reads it for a finished push: the
    // top-level response unless source names a delegate for it, then own's.
    static string? MainResponse(Session s) =>
        s.Activity.Source?.Response is null ? s.Activity.Response ?? s.Activity.Own?.Response : s.Activity.Own?.Response;
}
