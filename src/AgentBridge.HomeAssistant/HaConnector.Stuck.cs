using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

// One notice per stuck episode, decided on data (the host's recognised wedge, or Not responding),
// never on how long the session has been in error. It is withdrawn when the session takes a new
// event or acknowledgement. There is no time limit (#1511): a session that gets stuck again is told of
// again only after a recovery was observed, so one notice per episode bounds a flood.
// Nothing here restarts anything: the runbook (docs/runbooks/stuck-session.md) is the recovery.
// This notice rides the HA connector, which #1396 retires; it moves to the Slipgates push path.
public sealed partial class HaConnector
{
    // By session id. Not kept in the ledger: after a host restart a session still stuck is told of
    // once more, which is wanted.
    readonly Dictionary<string, SignInNotice> stucks = new(StringComparer.Ordinal);

    async Task StuckAsync(DateTimeOffset now, CancellationToken ct)
    {
        var stuck = source.Snapshot().Where(HaPayloads.Stuck).ToList();
        var channels = Notify.ButtonsOn || Notify.Enabled;
        foreach (var s in stuck)
        {
            if (!channels) { break; }
            SignInNotice? n;
            var push = Notify.Enabled && HaNotify.Split(Notify.Service) is not null ? "push:" + Notify.Service : null;
            lock (gate)
            {
                if (!stucks.TryGetValue(s.Id, out n)) { stucks[s.Id] = n = new SignInNotice { Tag = HaNotify.StuckTag(Slug, s.Id) }; }
            }
            // Once told, a destination is not told again for the same episode.
            bool Due(string destination) => !n.Delivered.ContainsKey(destination);
            List<string> phones;
            bool pushDue;
            lock (gate)
            {
                phones = Notify.ButtonsOn ? [.. Notify.AnswerButtonServices.Where(Due)] : [];
                pushDue = push is not null && Due(push);
            }
            if ((phones.Count == 0 && !pushDue) || now < n.RetryAt) { continue; }
            var agent = s.Attention!.Agent ?? s.Agent;
            var (title, message) = HaNotify.Stuck(options.MachineName, AgentName(agent), HaPayloads.SessionName(s), HaPayloads.IsHostWedge(s));
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
