using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Watch;

public enum WatchRule { Prompt, Idle, Missing, Finished }

// One notice for the person (always) and for Owner (the agent that launched the session), when the host has an owner record.
// Final is the one last "still stuck, no further reminders" notice after the episode cap.
public sealed record WatchNotice(WatchRule Rule, string Session, string? Owner, TimeSpan InState, bool Final);

public sealed record WatchSettings
{
    public bool Enabled { get; init; } = true;
    public TimeSpan PromptAfter { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan IdleAfter { get; init; } = TimeSpan.FromMinutes(20);
    // R4: a delivered session idle this long is OFFERED for ending (the host ends nothing).
    public TimeSpan FinishedAfter { get; init; } = TimeSpan.FromMinutes(30);
    // R3: a missing session is forgotten this long after it was first noticed missing.
    public TimeSpan MissingForget { get; init; } = TimeSpan.FromHours(24);
    // The same episode is not noticed again within this long (#4262: 30 minutes).
    public TimeSpan Repeat { get; init; } = TimeSpan.FromMinutes(30);
    // At most this many notices for one episode, then one final notice, then silence until the state changes.
    public int MaxPerEpisode { get; init; } = 3;
}

// The pure decision for R1 (at a prompt), R2 (idle with work that never delivered), R3 (an owned session that vanished) and R4 (delivered,
// then idle: an offer) in docs/design/session-watch.md. It reads sessions and says what to notice; it never sends, answers, types or ends
// anything. ownerOf is the host's durable owner record (S1b); null means no owner, so the person only. ended says whether a session's
// end is explained (a stop or `fleet end` on record, or a clean exit); with no end source (null) R3 stays off.
public sealed class SessionWatch(WatchSettings settings, Func<string, string?> ownerOf, Func<string, bool>? ended = null)
{
    enum Delivery { Unknown, No, Yes }

    readonly Dictionary<string, (SessionStatus Status, Delivery Delivery)> seen = [];
    readonly Dictionary<(WatchRule, string), (DateTimeOffset Since, DateTimeOffset Last, int Count)> ledger = [];
    readonly HashSet<string> owned = [];
    readonly Dictionary<string, DateTimeOffset> missingSince = [];

    // held: discovery is held, so absence decides nothing and nothing is judged or forgotten this pass.
    public IReadOnlyList<WatchNotice> Evaluate(IReadOnlyList<Session> sessions, DateTimeOffset now, bool held = false)
    {
        if (held) return [];
        if (!settings.Enabled) { foreach (var s in sessions) Track(s); Forget(sessions); return []; }
        var notices = new List<WatchNotice>();
        var live = new HashSet<(WatchRule, string)>();
        foreach (var s in sessions)
        {
            var delivery = Track(s);
            var inState = now - s.StatusSince;
            var owner = ownerOf(s.Id);
            if (owner is not null && ended is not null) { owned.Add(s.Id); missingSince.Remove(s.Id); }
            WatchRule? rule = s.Status switch
            {
                SessionStatus.Waiting when inState >= settings.PromptAfter => WatchRule.Prompt,
                SessionStatus.Idle when owner is not null && delivery == Delivery.No && inState >= settings.IdleAfter => WatchRule.Idle,
                SessionStatus.Idle when delivery == Delivery.Yes && inState >= settings.FinishedAfter => WatchRule.Finished,
                _ => null,
            };
            if (rule is not { } r) continue;
            var key = (r, s.Id);
            live.Add(key);
            if (Due(key, s.StatusSince, now, out var final)) notices.Add(new WatchNotice(r, s.Id, owner, inState, final));
        }
        var ids = sessions.Select(s => s.Id).ToHashSet();
        if (ended is not null)
        {
            foreach (var id in owned.Where(id => !ids.Contains(id)).ToList())
            {
                if (ended(id)) { owned.Remove(id); missingSince.Remove(id); continue; }
                if (!missingSince.TryGetValue(id, out var since)) missingSince[id] = since = now;
                if (now - since > settings.MissingForget) { owned.Remove(id); missingSince.Remove(id); continue; }
                var key = (WatchRule.Missing, id);
                live.Add(key);
                if (Due(key, since, now, out var final)) notices.Add(new WatchNotice(WatchRule.Missing, id, ownerOf(id), now - since, final));
            }
        }
        // A session that left the state, or went away, starts a fresh episode next time.
        foreach (var k in ledger.Keys.Where(k => !live.Contains(k)).ToList()) ledger.Remove(k);
        Forget(sessions);
        return notices;
    }

    // What a session that is gone leaves behind in the delivery tracker is dropped with it.
    void Forget(IReadOnlyList<Session> sessions)
    {
        var ids = sessions.Select(s => s.Id).ToHashSet();
        foreach (var id in seen.Keys.Where(id => !ids.Contains(id)).ToList()) seen.Remove(id);
    }

    // One notice per episode (the same state since the same moment), then every Repeat, MaxPerEpisode in all, then one final one, then silence.
    bool Due((WatchRule, string) key, DateTimeOffset since, DateTimeOffset now, out bool final)
    {
        final = false;
        var cap = Math.Max(1, settings.MaxPerEpisode);
        if (ledger.TryGetValue(key, out var was) && was.Since == since)
        {
            if (was.Count > cap || now - was.Last < settings.Repeat) return false;
            final = was.Count == cap;
            ledger[key] = (was.Since, now, was.Count + 1);
            return true;
        }
        ledger[key] = (since, now, 1);
        return true;
    }

    // "Delivered" is the transition PushNotifier already treats as finished: Working or Waiting to Idle with the MAIN agent's own answer (never a delegate's text).
    // Seen first as Idle (a host restart) the answer is unknown, and then neither R2 nor R4 may fire.
    Delivery Track(Session s)
    {
        var known = seen.TryGetValue(s.Id, out var was);
        var delivery = !known
            ? s.Status == SessionStatus.Idle ? Delivery.Unknown : Delivery.No
            : s.Status == SessionStatus.Working ? Delivery.No
            : s.Status == SessionStatus.Idle && was.Status != SessionStatus.Idle
                ? string.IsNullOrWhiteSpace(NotificationClasses.MainResponse(s)) ? Delivery.No : Delivery.Yes
            : was.Delivery;
        seen[s.Id] = (s.Status, delivery);
        return delivery;
    }
}
