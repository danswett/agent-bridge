using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

// A session stuck on Copilot CLI's own 120 s event-delivery watchdog (#687) tells the owner so, on
// the same phones and push service a sign-in notice takes. The CLI never recovers from it by
// itself (every later session.send fails until --resume), and its card only said "Agent error"
// or "Not responding", so the owner found out by trying and then went to the machine.
// One notice per session, repeated at most hourly, withdrawn once the session is not stuck.
// Nothing here restarts anything: the runbook (docs/runbooks/stuck-session.md) is the recovery.
public sealed partial class HaConnector
{
    // By session id. Not kept in the ledger: after a host restart a session still stuck is told of
    // once more, which is wanted.
    readonly Dictionary<string, SignInNotice> stucks = new(StringComparer.Ordinal);

    async Task StuckAsync(DateTimeOffset now, CancellationToken ct)
    {
        var stuck = source.Snapshot().Where(s => HaPayloads.Stuck(s, now, options.StuckErrorAfter)).ToList();
        var channels = Notify.ButtonsOn || Notify.Enabled;
        foreach (var s in stuck)
        {
            if (!channels) { break; }
            SignInNotice? n;
            lock (gate)
            {
                if (!stucks.TryGetValue(s.Id, out n)) { stucks[s.Id] = n = new SignInNotice { Tag = HaNotify.StuckTag(Slug, s.Id) }; }
            }
            var push = Notify.Enabled && HaNotify.Split(Notify.Service) is not null ? "push:" + Notify.Service : null;
            bool Due(string destination) => !n.Delivered.TryGetValue(destination, out var at) || now - at >= options.SignInRepeat;
            List<string> phones;
            bool pushDue;
            lock (gate)
            {
                phones = Notify.ButtonsOn ? [.. Notify.AnswerButtonServices.Where(Due)] : [];
                pushDue = push is not null && Due(push);
            }
            if ((phones.Count == 0 && !pushDue) || now < n.RetryAt) { continue; }
            var agent = s.Attention!.Agent ?? s.Agent;
            var (title, message) = HaNotify.Stuck(options.MachineName, AgentName(agent), HaPayloads.SessionName(s));
            var missed = false;
            void Took(string destination)
            {
                lock (gate) { n.Delivered[destination] = now; }
            }
            if (phones.Count > 0)
            {
                var payload = HaNotify.StuckPayload(n.Tag, title, message, options.DashboardUrlPath, options.MachineName,
                    Notify.Icon, Notify.IconColor, Notify.Color, Notify.InterruptionLevel);
                var sent = await SendAsync("stuck", phones, payload, "stuck notice", phone =>
                {
                    Took(phone);
                    lock (gate) { n.Phones.Add(phone); }
                }, ct);
                missed |= sent.Count < phones.Count;
            }
            if (pushDue)
            {
                if (await TryPushAsync(title, message, ct, $"stuck push:{Notify.Service}")) { Took(push!); }
                else { missed = true; }
            }
            if (!missed) { log($"Home Assistant connector told the owner session {s.Id} looks stuck ({s.Attention!.Kind}); it was not restarted"); }
            else { n.RetryAt = now + options.ButtonsRetry; }
        }
        // A session discovery has yet to find again is missing, not recovered.
        if (source.Held is not null) { return; }
        List<(string Id, SignInNotice N)> over;
        lock (gate) { over = [.. stucks.Where(x => !stuck.Any(s => s.Id == x.Key)).Select(x => (x.Key, x.Value))]; }
        foreach (var (id, n) in over)
        {
            if (now < n.RetryAt && n.Phones.Count > 0) { continue; }
            List<string> phones;
            lock (gate) { phones = [.. n.Phones]; }
            if (phones.Count > 0)
            {
                await SendAsync("stuck", phones, HaNotify.ClearSignIn(n.Tag), "stuck notice clear", phone =>
                {
                    lock (gate) { n.Phones.Remove(phone); }
                }, ct);
            }
            bool removed;
            lock (gate)
            {
                removed = n.Phones.Count == 0 && stucks.Remove(id);
                if (!removed) { n.RetryAt = now + options.ButtonsRetry; }
            }
            if (removed) { log($"Home Assistant connector withdrew the stuck notice for session {id}: it is not stuck any more"); }
        }
    }
}
