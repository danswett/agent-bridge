using System.Text.Json;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// A device this host notifies: its browser's push subscription, which only that browser
// can decrypt for, and where a tap should open the app.
public sealed record PushSubscriber(string Device, string Name, string Endpoint, string P256dh, string Auth, string App, DateTimeOffset Since)
{
    public PushSubscription Subscription => new(Endpoint, P256dh, Auth);
}

// The push subscriptions this host holds, kept in the profile's own folder
// (push-subscriptions.json beside the device key), never agent-ha-bridge's. They persist so
// a host that restarts can still reach a phone whose app is closed: the phone has no reason
// to register again until it is next opened.
//
// They belong to one account. A push goes straight to a phone, around the relay's
// account-scoped routing, so a subscription kept across a change of account would deliver
// the new account's sessions to the old account's phones (spike S5 finding 4). The file
// names its account, and one written for any other is ignored and replaced.
public sealed class PushStore
{
    // A phone that reinstalls the app comes back as a new device; the oldest go past this.
    public const int MaxSubscribers = 16;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly string? path;
    readonly Action<string> log;
    readonly object gate = new();
    readonly Dictionary<string, PushSubscriber> byDevice = new(StringComparer.Ordinal);
    string? account;

    sealed record Saved(string? Account, List<PushSubscriber>? Subscribers);

    PushStore(string? path, string? account, Action<string>? log)
    {
        this.path = path;
        this.account = account;
        this.log = log ?? (_ => { });
    }

    // Reads what was saved for this account; anything saved for another, or unreadable, is
    // ignored and replaced at the next change.
    public static PushStore Load(string? path, string? account, Action<string>? log = null)
    {
        var store = new PushStore(path, account, log);
        if (path is null || !File.Exists(path)) { return store; }
        Saved? saved = null;
        try { saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            store.log($"push subscriptions unreadable ({e.Message}); starting with none");
        }
        if (saved?.Account is { } a && a == account)
        {
            foreach (var s in saved.Subscribers ?? [])
            {
                if (s is { Device: { Length: > 0 }, Endpoint: { Length: > 0 } }) { store.byDevice[s.Device] = s; }
            }
        }
        else if (saved is not null)
        {
            store.log("push subscriptions belong to another account; ignoring them");
        }
        return store;
    }

    public string? Account { get { lock (gate) { return account; } } }

    public int Count { get { lock (gate) { return byDevice.Count; } } }

    public IReadOnlyList<PushSubscriber> All { get { lock (gate) { return [.. byDevice.Values.OrderBy(s => s.Since)]; } } }

    public PushSubscriber? Get(string device) { lock (gate) { return byDevice.GetValueOrDefault(device); } }

    // The account the host is signed in as now. Subscriptions from any other are dropped:
    // returns how many.
    public int Bind(string accountId)
    {
        lock (gate)
        {
            if (account == accountId) { return 0; }
            var dropped = byDevice.Count;
            byDevice.Clear();
            account = accountId;
            Save();
            return dropped;
        }
    }

    // One subscription per device, and per endpoint: a device that subscribes again replaces
    // its own, and an endpoint registered again from a reinstalled app replaces the old one.
    public void Put(PushSubscriber s)
    {
        lock (gate)
        {
            foreach (var old in byDevice.Values.Where(o => o.Endpoint == s.Endpoint && o.Device != s.Device).ToList()) { byDevice.Remove(old.Device); }
            byDevice[s.Device] = s;
            while (byDevice.Count > MaxSubscribers)
            {
                byDevice.Remove(byDevice.Values.Where(o => o.Device != s.Device).MinBy(o => o.Since)!.Device);
            }
            Save();
        }
    }

    // The device's subscription, and any with the given endpoint.
    public bool Remove(string device, string? endpoint = null)
    {
        lock (gate)
        {
            var gone = byDevice.Values.Where(o => o.Device == device || (endpoint is not null && o.Endpoint == endpoint)).Select(o => o.Device).ToList();
            foreach (var d in gone) { byDevice.Remove(d); }
            if (gone.Count > 0) { Save(); }
            return gone.Count > 0;
        }
    }

    void Save()
    {
        if (path is null) { return; }
        try
        {
            var text = JsonSerializer.Serialize(new Saved(account, [.. byDevice.Values.OrderBy(s => s.Since)]), Json);
            // Written whole and then moved into place, so a crash mid-write never leaves a
            // half file that would lose every subscription. Owner-only: an endpoint is a
            // capability to notify that phone.
            var temp = path + ".tmp";
            if (OperatingSystem.IsWindows()) { File.WriteAllText(temp, text); }
            else
            {
                const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var fs = new FileStream(temp, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = ownerOnly }))
                using (var w = new StreamWriter(fs)) { w.Write(text); }
                File.SetUnixFileMode(temp, ownerOnly);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"could not save push subscriptions: {e.Message}");
        }
    }
}
