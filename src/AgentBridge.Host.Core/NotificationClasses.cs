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
    //     errored turn, not responding): it needs the person.
    //   Idle with an answer of the main agent's own: its turn is done. A delegate's text never
    //     counts, so a delegate finishing is never the session finishing (subagents.md §7).
    //   Anything else (working, or idle with nothing of the main agent's own said): FYI.
    // Watch, Mute and an agent-driven turn (#489) decide who is alerted for a class, not the class.
    public static NotificationClass Of(Session s)
    {
        if (s.Status == SessionStatus.Ending) { return NotificationClass.Fyi; }
        if (s.Question is not null || s.Attention is not null) { return NotificationClass.NeedsYou; }
        if (s.Status == SessionStatus.Idle && !string.IsNullOrWhiteSpace(MainResponse(s))) { return NotificationClass.Done; }
        return NotificationClass.Fyi;
    }

    // session.classSince: when the tier began, from the fields that put the session in it, so it
    // needs no memory of earlier commits and moves only when they do. Needs you counts from its
    // oldest pending reason, which is what the 15-minute reminder waits on (#913 q9).
    public static DateTimeOffset Since(Session s, NotificationClass c)
    {
        if (c != NotificationClass.NeedsYou) { return s.StatusSince; }
        return (s.Question?.AskedAt, s.Attention?.Since) switch
        {
            ({ } asked, { } since) => asked < since ? asked : since,
            ({ } asked, null) => asked,
            (null, { } since) => since,
            _ => s.StatusSince,
        };
    }

    // The main agent's own latest answer, as PushNotifier reads it for a finished push: the
    // top-level response unless source names a delegate for it, then own's.
    static string? MainResponse(Session s) =>
        s.Activity.Source?.Response is null ? s.Activity.Response ?? s.Activity.Own?.Response : s.Activity.Own?.Response;
}
