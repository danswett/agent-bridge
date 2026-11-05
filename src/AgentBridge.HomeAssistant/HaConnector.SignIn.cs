using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

// Needs you for an agent that needs signing in again (#800), through the channels a question's
// Needs you already takes in Home Assistant (stage 5b): a notice on the phones answer buttons go to
// (answerButtons.services), with no buttons, and the hooks' push on notifications.service when it
// is on. As the host's own signIn push (§3 of #800's 6646501884): one per machine, agent and
// account, when the first of its sessions needs sign-in and again at most hourly while any still
// does; never one per session. No account, code or URL is in it. It is withdrawn from the phones
// once none of its sessions needs a person any more. An error or Not responding sends nothing: they
// show on the card's status only, as on the clients.
public sealed partial class HaConnector
{
    sealed class SignInNotice
    {
        public required string Tag;
        // When sending or withdrawing is next tried.
        public DateTimeOffset RetryAt;
        // The phones that hold it, to withdraw it from.
        public HashSet<string> Phones = new(StringComparer.Ordinal);
        // When each destination (a phone, or "push:<service>") last took it while this need lasts.
        // Each is told again an hour after its own delivery, and one that missed it is tried again
        // shortly, so no destination hears it twice within the hour and none is left out because
        // another took it (found in review). Forgotten once the need is over: a new need is news.
        public Dictionary<string, DateTimeOffset> Delivered = new(StringComparer.Ordinal);
    }

    // By "<agent>:<account digest>". Kept in the ledger, so a restart neither sends one again within
    // the hour nor forgets a phone it must be withdrawn from.
    readonly Dictionary<string, SignInNotice> signIns = new(StringComparer.Ordinal);

    static string AgentWire(AgentKind agent) => agent switch
    {
        AgentKind.Copilot => "copilot",
        AgentKind.Claude => "claude",
        AgentKind.Codex => "codex",
        _ => "agent",
    };

    // AuthState.AgentName: an agent this connector does not know is not named.
    static string AgentName(AgentKind agent) => agent switch
    {
        AgentKind.Copilot => "Copilot",
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        _ => "The agent",
    };

    async Task SignInAsync(DateTimeOffset now, CancellationToken ct)
    {
        var groups = source.Snapshot().Where(HaPayloads.NeedsPerson)
            .GroupBy(s => (Agent: s.Attention!.Agent ?? s.Agent, s.Attention.Account))
            .Select(g => (Key: $"{AgentWire(g.Key.Agent)}:{HaNotify.AccountDigest(g.Key.Account)}", g.Key.Agent, g.Key.Account,
                Names: (IReadOnlyList<string>)[.. g.OrderByDescending(s => s.Attention!.Since).Select(HaPayloads.SessionName)]))
            .ToList();
        var channels = Notify.ButtonsOn || Notify.Enabled;
        foreach (var g in groups)
        {
            if (!channels) { break; }
            SignInNotice? n;
            lock (gate)
            {
                if (!signIns.TryGetValue(g.Key, out n)) { signIns[g.Key] = n = new SignInNotice { Tag = HaNotify.SignInTag(Slug, AgentWire(g.Agent), g.Account) }; }
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
            var (title, message) = HaNotify.SignIn(options.MachineName, AgentName(g.Agent), g.Names);
            var missed = false;
            // Each destination on record as soon as it has it: a pass stopped part-way (cancelled, or
            // agent-ha-bridge starting) must not send it again to one that took it, after a restart
            // or a resume either (found in review).
            void Took(string destination)
            {
                lock (gate) { n.Delivered[destination] = now; }
            }
            if (phones.Count > 0)
            {
                var payload = HaNotify.SignInPayload(n.Tag, title, message, options.DashboardUrlPath, options.MachineName,
                    Notify.Icon, Notify.IconColor, Notify.Color, Notify.InterruptionLevel);
                var sent = await SendAsync("sign-in", phones, payload, "sign-in notice", phone =>
                {
                    Took(phone);
                    lock (gate) { n.Phones.Add(phone); }
                    SaveLedger();
                }, ct);
                missed |= sent.Count < phones.Count;
            }
            if (pushDue)
            {
                if (await TryPushAsync(title, message, ct, $"sign-in push:{Notify.Service}"))
                {
                    Took(push!);
                    SaveLedger();
                }
                else { missed = true; }
            }
            if (!missed) { log($"Home Assistant connector sent a sign-in notice for {AgentName(g.Agent)} ({g.Names.Count} session(s))"); }
            // Those it missed are tried again shortly, each failing service said once until it works.
            else { n.RetryAt = now + options.ButtonsRetry; }
        }
        // Withdrawn once none of its sessions needs a person: signed in, recovered, or ended. A new
        // need after that is news again.
        List<(string Key, SignInNotice N)> over;
        lock (gate) { over = [.. signIns.Where(x => !groups.Any(g => g.Key == x.Key)).Select(x => (x.Key, x.Value))]; }
        foreach (var (key, n) in over)
        {
            // The need is over: what was delivered for it is forgotten at once, so a need that comes
            // back is told everywhere again even while a withdrawal is still being retried (found in
            // review). The phones are kept until each has been cleared.
            bool forgot;
            lock (gate)
            {
                forgot = n.Delivered.Count > 0;
                n.Delivered.Clear();
            }
            if (forgot) { SaveLedger(); }
            if (now < n.RetryAt && n.Phones.Count > 0) { continue; }
            List<string> phones;
            lock (gate) { phones = [.. n.Phones]; }
            if (phones.Count > 0)
            {
                await SendAsync("sign-in", phones, HaNotify.ClearSignIn(n.Tag), "sign-in notice clear", phone =>
                {
                    lock (gate) { n.Phones.Remove(phone); }
                    SaveLedger();
                }, ct);
            }
            bool removed;
            lock (gate)
            {
                removed = n.Phones.Count == 0 && signIns.Remove(key);
                if (!removed) { n.RetryAt = now + options.ButtonsRetry; }
            }
            if (removed) { SaveLedger(); }
        }
    }

    JsonObject SignInLedger()
    {
        var o = new JsonObject();
        foreach (var (key, n) in signIns.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var delivered = new JsonObject();
            foreach (var (to, at) in n.Delivered.OrderBy(x => x.Key, StringComparer.Ordinal)) { delivered[to] = at.ToString("o", System.Globalization.CultureInfo.InvariantCulture); }
            o[key] = new JsonObject
            {
                ["tag"] = n.Tag,
                ["phones"] = new JsonArray([.. n.Phones.Order(StringComparer.Ordinal).Select(p => (JsonNode)p)]),
                ["delivered"] = delivered,
            };
        }
        return o;
    }

    void LoadSignIns(JsonElement root)
    {
        if (!root.TryGetProperty("signIn", out var o) || o.ValueKind != JsonValueKind.Object) { return; }
        foreach (var entry in o.EnumerateObject())
        {
            var e = entry.Value;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("tag", out var tag) || tag.ValueKind != JsonValueKind.String
                || tag.GetString() is not { } t || !t.StartsWith("bridge_signin_", StringComparison.Ordinal)) { continue; }
            var n = new SignInNotice { Tag = t };
            if (e.TryGetProperty("phones", out var phones) && phones.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in phones.EnumerateArray())
                {
                    if (p.ValueKind == JsonValueKind.String && HaNotify.Split(p.GetString()!) is not null) { n.Phones.Add(p.GetString()!); }
                }
            }
            if (e.TryGetProperty("delivered", out var delivered) && delivered.ValueKind == JsonValueKind.Object)
            {
                foreach (var d in delivered.EnumerateObject())
                {
                    if (d.Name.Length > 0 && d.Value.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(d.Value.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                    {
                        n.Delivered[d.Name] = at;
                    }
                }
            }
            signIns[entry.Name] = n;
        }
    }
}
