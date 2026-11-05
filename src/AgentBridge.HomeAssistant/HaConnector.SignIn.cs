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
        // When this round first reached a destination; when sending or withdrawing is next tried.
        public DateTimeOffset? SentAt;
        public DateTimeOffset RetryAt;
        // The phones that hold it, to withdraw it from.
        public HashSet<string> Phones = new(StringComparer.Ordinal);
        // The destinations this round reached (each phone, and "push:<service>"): only the others are
        // tried again, so one that took it is not told twice, and one that failed is not left out
        // for the hour because another took it (found in review).
        public HashSet<string> Delivered = new(StringComparer.Ordinal);
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
            // A round is over an hour after it first reached anyone: the next goes to every destination.
            lock (gate)
            {
                if (n.SentAt is { } at && now - at >= options.SignInRepeat)
                {
                    n.SentAt = null;
                    n.Delivered.Clear();
                }
            }
            var push = Notify.Enabled && HaNotify.Split(Notify.Service) is not null ? "push:" + Notify.Service : null;
            List<string> phones;
            lock (gate) { phones = Notify.ButtonsOn ? [.. Notify.AnswerButtonServices.Where(s => !n.Delivered.Contains(s))] : []; }
            var pushDue = push is not null && !n.Delivered.Contains(push);
            if ((phones.Count == 0 && !pushDue) || now < n.RetryAt) { continue; }
            var (title, message) = HaNotify.SignIn(options.MachineName, AgentName(g.Agent), g.Names);
            var missed = false;
            // Each destination on record as soon as it has it, the round's time with it: a pass
            // stopped part-way (cancelled, or agent-ha-bridge starting) must not send it again to one
            // that took it, after a restart or a resume either (found in review).
            void Took(string destination)
            {
                lock (gate)
                {
                    n.Delivered.Add(destination);
                    n.SentAt ??= now;
                }
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
            var entry = new JsonObject
            {
                ["tag"] = n.Tag,
                ["phones"] = new JsonArray([.. n.Phones.Order(StringComparer.Ordinal).Select(p => (JsonNode)p)]),
                ["delivered"] = new JsonArray([.. n.Delivered.Order(StringComparer.Ordinal).Select(p => (JsonNode)p)]),
            };
            if (n.SentAt is { } at) { entry["sentAt"] = at.ToString("o", System.Globalization.CultureInfo.InvariantCulture); }
            o[key] = entry;
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
            if (e.TryGetProperty("sentAt", out var at) && at.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(at.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var sent)) { n.SentAt = sent; }
            if (e.TryGetProperty("phones", out var phones) && phones.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in phones.EnumerateArray())
                {
                    if (p.ValueKind == JsonValueKind.String && HaNotify.Split(p.GetString()!) is not null) { n.Phones.Add(p.GetString()!); }
                }
            }
            if (e.TryGetProperty("delivered", out var delivered) && delivered.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in delivered.EnumerateArray())
                {
                    if (d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } to) { n.Delivered.Add(to); }
                }
            }
            signIns[entry.Name] = n;
        }
    }
}
