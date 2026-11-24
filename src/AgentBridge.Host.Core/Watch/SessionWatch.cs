using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Watch;

public enum WatchRule { Prompt, Idle }

// One notice for the person (always) and for Owner (the agent that launched the session), when the host has an owner record.
// Final is the one last "still stuck, no further reminders" notice after the episode cap.
public sealed record WatchNotice(WatchRule Rule, string Session, string? Owner, TimeSpan InState, bool Final);

public sealed record WatchSettings
{
    public bool Enabled { get; init; } = true;
    public TimeSpan PromptAfter { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan IdleAfter { get; init; } = TimeSpan.FromMinutes(20);
    // The same episode is not noticed again within this long (#4262: 30 minutes).
    public TimeSpan Repeat { get; init; } = TimeSpan.FromMinutes(30);
    // At most this many notices for one episode, then one final notice, then silence until the state changes.
    public int MaxPerEpisode { get; init; } = 3;
}

// The pure decision for R1 (at a prompt) and R2 (idle with work that never delivered) in docs/design/session-watch.md.
// It reads sessions and says what to notice; it never sends, answers, types or ends anything.
// ownerOf is the host's durable owner record (S1b); null means no owner, so the person only.
public sealed class SessionWatch(WatchSettings settings, Func<string, string?> ownerOf)
{
    enum Delivery { Unknown, No, Yes }

    readonly Dictionary<string, (SessionStatus Status, Delivery Delivery)> seen = [];
    readonly Dictionary<(WatchRule, string), (DateTimeOffset Since, DateTimeOffset Last, int Count)> ledger = [];

    // held: discovery is held, so absence decides nothing and nothing is judged or forgotten this pass.
    public IReadOnlyList<WatchNotice> Evaluate(IReadOnlyList<Session> sessions, DateTimeOffset now, bool held = false)
    {
        if (held) return [];
        if (!settings.Enabled) { foreach (var s in sessions) Track(s); return []; }
        var notices = new List<WatchNotice>();
        var live = new HashSet<(WatchRule, string)>();
        foreach (var s in sessions)
        {
            var delivery = Track(s);
            var inState = now - s.StatusSince;
            var owner = ownerOf(s.Id);
            WatchRule? rule = s.Status switch
            {
                SessionStatus.Waiting when inState >= settings.PromptAfter => WatchRule.Prompt,
                SessionStatus.Idle when owner is not null && delivery == Delivery.No && inState >= settings.IdleAfter => WatchRule.Idle,
                _ => null,
            };
            if (rule is not { } r) continue;
            var key = (r, s.Id);
            live.Add(key);
            var final = false;
            if (ledger.TryGetValue(key, out var was) && was.Since == s.StatusSince)
            {
                if (was.Count > Math.Max(1, settings.MaxPerEpisode) || now - was.Last < settings.Repeat) continue;
                final = was.Count == Math.Max(1, settings.MaxPerEpisode);
                ledger[key] = (was.Since, now, was.Count + 1);
            }
            else ledger[key] = (s.StatusSince, now, 1);
            notices.Add(new WatchNotice(r, s.Id, owner, inState, final));
        }
        // A session that left the state, or went away, starts a fresh episode next time.
        foreach (var k in ledger.Keys.Where(k => !live.Contains(k)).ToList()) ledger.Remove(k);
        var ids = sessions.Select(s => s.Id).ToHashSet();
        foreach (var id in seen.Keys.Where(id => !ids.Contains(id)).ToList()) seen.Remove(id);
        return notices;
    }

    // "Delivered" is the transition PushNotifier already treats as finished: Working or Waiting to Idle with a response.
    // Seen first as Idle (a host restart) the answer is unknown, and then neither R2 nor R4 may fire.
    Delivery Track(Session s)
    {
        var known = seen.TryGetValue(s.Id, out var was);
        var delivery = !known
            ? s.Status == SessionStatus.Idle ? Delivery.Unknown : Delivery.No
            : s.Status == SessionStatus.Working ? Delivery.No
            : s.Status == SessionStatus.Idle && was.Status != SessionStatus.Idle
                ? string.IsNullOrWhiteSpace(s.Activity.Response) ? Delivery.No : Delivery.Yes
            : was.Delivery;
        seen[s.Id] = (s.Status, delivery);
        return delivery;
    }
}
