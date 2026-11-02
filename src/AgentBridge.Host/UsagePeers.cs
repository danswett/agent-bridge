using System.Text.Json;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Asks the account's other hosts for their usage readings (#172), so this one can serve them when
// their machine is offline. Through whichever relay connection the host has now (Attach on every
// connect and disconnect), each on a short channel of its own, as session transfers open one
// (docs/protocol/v1-transfer.md): hello, usage.get, close. Read-only: usage.get carries no command
// and no authority, and what comes back is only ever shown.
//
// Paced so a fleet of hosts costs the relay little: each other host is asked when it appears, then
// every five minutes while both are online, never more than once a minute, one at a time. Only
// devices in the account's presence (the relay checked their key and account) that are hosts, and
// whose channel's handshake proved that device's key, are asked or believed.
public sealed class UsagePeers : IAsyncDisposable
{
    public const string HostKind = "host";

    readonly PeerUsage store;
    readonly string me;
    readonly Action<string> log;
    readonly TimeProvider time;
    readonly Dictionary<string, DateTimeOffset> asked = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset> seen = new(StringComparer.Ordinal);
    // The link seen was recorded on.
    Link? seenOn;
    readonly Dictionary<string, string> said = new(StringComparer.Ordinal);
    readonly SemaphoreSlim pass = new(1, 1);
    readonly SemaphoreSlim wake = new(0, 1);
    readonly CancellationTokenSource stop = new();
    Link? link;
    Task? loop;

    sealed record Link(RelayClient Relay, ChannelHub Hub);

    public UsagePeers(PeerUsage store, string me, Action<string>? log = null, TimeProvider? time = null)
    {
        this.store = store;
        this.me = me;
        this.log = log ?? (_ => { });
        this.time = time ?? TimeProvider.System;
    }

    public TimeSpan Every { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MinimumGap { get; init; } = TimeSpan.FromMinutes(1);
    // How often the loop looks for a host that has come due, between presence changes.
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(30);
    // For the channel to open, and for each answer on it.
    public TimeSpan Wait { get; init; } = TimeSpan.FromSeconds(15);

    public void Attach(RelayClient? relay, ChannelHub? hub)
    {
        var next = relay is null || hub is null ? null : new Link(relay, hub);
        var old = Interlocked.Exchange(ref link, next);
        if (old is not null) { old.Relay.PresenceChanged -= OnPresence; }
        if (next is null) { return; }
        // The store is not bound here: the account is, as the host signs in (Program.cs, before the
        // new account is published), and a connection made before that sign-in, attached after it,
        // would bind it back to the account before. Its answers are refused instead (Take's forAccount).
        next.Relay.PresenceChanged += OnPresence;
        Wake();
    }

    void OnPresence(IReadOnlyList<PresenceEntry> _) => Wake();

    void Wake()
    {
        try { if (wake.CurrentCount == 0) { wake.Release(); } }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    public void Start()
    {
        loop ??= Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await PassAsync(stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e) { log($"usage: asking the other machines failed: {e.GetType().Name}"); }
                try { await wake.WaitAsync(Tick, stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    // Asks every other host that is due, one at a time. Returns how many were asked.
    public async Task<int> PassAsync(CancellationToken cancellationToken = default)
    {
        await pass.WaitAsync(cancellationToken);
        try
        {
            store.Prune();
            if (Volatile.Read(ref link) is not { } l) { return 0; }
            // A new link (a reconnect): every host on it has appeared anew, so each is asked once
            // the minimum gap since it was last asked allows, not at the next five minutes.
            if (!ReferenceEquals(seenOn, l)) { seen.Clear(); seenOn = l; }
            ForgetGone(l.Relay);
            var now = time.GetUtcNow();
            var hosts = l.Relay.Presence.Where(p => p.Kind == HostKind && p.Id != me && p.Id != l.Relay.DeviceId).ToList();
            // When each came into presence, by this clock: the relay's own stamp (Since) is another
            // clock, and a few minutes' skew between the two would read as a host appearing anew.
            foreach (var gone in seen.Keys.Where(id => hosts.All(p => p.Id != id)).ToList()) { seen.Remove(gone); }
            foreach (var p in hosts) { seen.TryAdd(p.Id, now); }
            var due = hosts.Where(p => Due(p.Id, now)).ToList();
            foreach (var p in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                asked[p.Id] = time.GetUtcNow();
                await AskAsync(l, p, cancellationToken);
            }
            // Hosts long gone are not remembered for ever.
            var present = l.Relay.Presence.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in asked.Keys.Where(id => !present.Contains(id) && now - asked[id] > Every).ToList()) { asked.Remove(id); said.Remove(id); }
            return due.Count;
        }
        finally { pass.Release(); }
    }

    // On appearing (in presence since it was last asked), then every Every; never within MinimumGap.
    // A clock that went back makes it due rather than holding it off.
    bool Due(string id, DateTimeOffset now)
    {
        if (!asked.TryGetValue(id, out var at)) { return true; }
        var since = now - at;
        if (since < TimeSpan.Zero || since >= Every) { return true; }
        return since >= MinimumGap && seen.GetValueOrDefault(id) > at;
    }

    // A machine forgotten from the account's roster takes the readings it took with it. Only with a
    // roster to say so: without one (an older relay, or one not yet read) nothing is inferred.
    void ForgetGone(RelayClient relay)
    {
        if (Members(relay) is not { } account) { return; }
        if (store.Forget(account.Contains) is > 0 and var n) { log($"usage: dropped {n} reading(s) taken by machines no longer the account's"); }
    }

    // The account's machines, online or not, as the relay's roster has them; null without one.
    // Where the relay keeps a roster, every machine the account has, online or not, and this one.
    // RelayClient sets Presence and then Known from each presence message. Known is read first, so
    // where those stores stay in order (x64), a machine coming online between the two reads is in
    // one list or the other, never neither, which would read as forgotten. Should it be in neither
    // anyway, it is online and due to be asked, so its readings are back within the pass.
    HashSet<string>? Members(RelayClient relay)
    {
        if (relay.Known is not { } known) { return null; }
        Interlocked.MemoryBarrier();
        return relay.Presence.Select(p => p.Id).Concat(known.Select(k => k.Id)).Append(me).ToHashSet(StringComparer.Ordinal);
    }

    async Task AskAsync(Link l, PresenceEntry peer, CancellationToken cancellationToken)
    {
        SecureChannel? ch = null;
        string outcome;
        try
        {
            ch = await l.Hub.OpenAsync(peer.Id, Wait);
            outcome = await AskOnAsync(l, peer, ch, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) { outcome = $"could not be asked ({Why(e)})"; }
        finally
        {
            if (ch is not null)
            {
                try { await l.Hub.CloseAsync(ch); }
                catch (Exception) { }
            }
        }
        // Said when it changes, not every five minutes.
        if (said.GetValueOrDefault(peer.Id) != outcome)
        {
            said[peer.Id] = outcome;
            if (outcome != Answered) { log($"usage: {peer.Name} ({peer.Id[..8]}) {outcome}"); }
        }
    }

    const string Answered = "answered";

    async Task<string> AskOnAsync(Link l, PresenceEntry peer, SecureChannel ch, CancellationToken cancellationToken)
    {
        // The handshake proved the key behind the device id; a device that does not call itself a
        // host on the channel either is not asked.
        if (ch.PeerKind != HostKind) { return "is not a host"; }
        var hello = Read<HelloResult>(await ch.RequestAsync(Ops.Hello, new { }, Wait, cancellationToken));
        if (hello.Machine?.Id != peer.Id) { return "answered as another machine"; }
        if (hello.Capabilities?.Contains(UsageProtocol.Capability) != true) { return "reports no usage"; }
        var shared = hello.Capabilities.Contains(UsageProtocol.SharedCapability);
        var report = Read<UsageReport>(await ch.RequestAsync(UsageProtocol.Get, new UsageRequest { Shared = shared }, Wait, cancellationToken));
        // Still a host of the account now: one that left, or connected again as something else,
        // while it was being asked is not believed.
        if (l.Relay.Presence.FirstOrDefault(p => p.Id == peer.Id) is not { Kind: HostKind } now) { return "left while being asked"; }
        // Nor what it passes on from a machine the account has since forgotten (it may not have
        // heard yet). Machines that are only offline are still the account's.
        var agents = (IEnumerable<AgentUsage>)(report.Agents ?? []);
        if (Members(l.Relay) is { } members) { agents = agents.Where(a => a?.ReadBy is not { } by || members.Contains(by.Id)); }
        // For the account this connection was signed in as: an answer that arrives after the host
        // signed in to another one is not taken for it.
        var took = store.Take(agents, new UsageReader(peer.Id, now.Name), l.Relay.AccountId is { Length: > 0 } a ? a : null);
        if (took > 0) { log($"usage: took {took} reading(s) from {now.Name} ({peer.Id[..8]})"); }
        return Answered;
    }

    static string Why(Exception e) => e switch
    {
        TimeoutException => "no answer in time",
        HostErrorException h => h.Message,
        JsonException => "its answer was not a usage report",
        _ => e.GetType().Name,
    };

    static T Read<T>(JsonElement response) where T : class
    {
        if (!response.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = response.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            throw new HostErrorException(Limits.KeepStart(error is null ? null : new string([.. error.Where(c => !char.IsControl(c))]), 200) ?? "refused");
        }
        return response.GetProperty("result").Deserialize<T>(ProtocolJson.Options) ?? throw new JsonException("no result");
    }

    public sealed class HostErrorException(string message) : Exception(message);

    public async ValueTask DisposeAsync()
    {
        Attach(null, null);
        stop.Cancel();
        if (loop is not null)
        {
            try { await loop; }
            catch (OperationCanceledException) { }
        }
        stop.Dispose();
    }
}
