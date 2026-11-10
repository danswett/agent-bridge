using System.Diagnostics.Metrics;

namespace AgentBridge.Relay;

// The hook the observability slice (design §5.10) exports from. Aggregates only, and every
// tag is low-cardinality: a kind, an outcome or a close code. Never an account, device,
// address or name, hashed or not; RelayMetricsTests holds every tag key to that allowlist.
public sealed class RelayMetrics
{
    public const string MeterName = "AgentBridge.Relay";
    public static readonly IReadOnlySet<string> AllowedTags = new HashSet<string> { "kind", "outcome", "code", "site" };

    readonly Histogram<double> forwardLatency, connectTime, drainDuration;
    readonly Counter<long> connects, disconnects, refusals, frames, bytes, undeliverable, pushes, signIns, blobs, blobBytes, roster, rateLimited;

    public RelayMetrics(IMeterFactory factory, Func<int> online, Func<IEnumerable<(string Kind, string Site, int Count)>>? onlineByKind = null, Func<bool>? draining = null)
    {
        var meter = factory.Create(MeterName);
        meter.CreateObservableGauge("relay.draining", () => draining?.Invoke() == true ? 1 : 0, description: "1 while this replica is draining for shutdown, else 0");
        drainDuration = meter.CreateHistogram<double>("relay.drain.duration", unit: "ms", description: "Time to tell every socket to reconnect when this replica drains");
        rateLimited = meter.CreateCounter<long>("relay.ratelimited", description: "Requests and frames refused by a rate limit, by kind (auth, connect, frames)");
        meter.CreateObservableGauge("relay.connections", online, description: "Admitted sockets on this replica");
        meter.CreateObservableGauge("relay.sockets.live", () => (onlineByKind?.Invoke() ?? []).Select(k => new Measurement<int>(k.Count, new KeyValuePair<string, object?>("kind", k.Kind), new KeyValuePair<string, object?>("site", k.Site))), description: "Admitted sockets on this replica, by device kind and the configured hostname they arrived on (or other)");
        forwardLatency = meter.CreateHistogram<double>("relay.forward.latency", unit: "ms", description: "Data frame received to written to its target, by the sender's device kind");
        connectTime = meter.CreateHistogram<double>("relay.connect.duration", unit: "ms", description: "Socket opened to admitted (ready sent), by device kind");
        connects = meter.CreateCounter<long>("relay.connects", description: "Admitted sockets, by device kind");
        disconnects = meter.CreateCounter<long>("relay.disconnects", description: "Ended sockets, by how they ended");
        refusals = meter.CreateCounter<long>("relay.refusals", description: "Sockets refused before or at admission, by close code");
        frames = meter.CreateCounter<long>("relay.frames.forwarded", description: "Data frames delivered to a device");
        bytes = meter.CreateCounter<long>("relay.bytes.forwarded", unit: "By");
        undeliverable = meter.CreateCounter<long>("relay.frames.undeliverable", description: "Data frames for a device not connected");
        pushes = meter.CreateCounter<long>("relay.pushes", description: "Push forwards, by outcome");
        signIns = meter.CreateCounter<long>("relay.signins", description: "Device-flow completions, by outcome");
        blobs = meter.CreateCounter<long>("relay.blobs", description: "Blob store requests, by kind (put, get, delete) and outcome");
        blobBytes = meter.CreateCounter<long>("relay.blobs.bytes", unit: "By", description: "Stored bytes of blobs issued for upload");
        roster = meter.CreateCounter<long>("relay.roster", description: "Machine roster loads, writes and forgets, by kind and outcome");
    }

    public void Connected(string kind, string site) => connects.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("site", site));
    public void Disconnected(string outcome) => disconnects.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    public void Refused(int code) => refusals.Add(1, new KeyValuePair<string, object?>("code", code));
    public void Forwarded(int length) { frames.Add(1); bytes.Add(length); }
    public void ForwardLatency(string kind, TimeSpan elapsed) => forwardLatency.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("kind", kind));
    public void ConnectTime(string kind, TimeSpan elapsed) => connectTime.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("kind", kind));
    public void Undeliverable() => undeliverable.Add(1);
    public void RateLimited(string kind) => rateLimited.Add(1, new KeyValuePair<string, object?>("kind", kind));
    public void Drained(TimeSpan elapsed) => drainDuration.Record(elapsed.TotalMilliseconds);
    // Push outcomes: "gone" for 404 and 410 (the subscription is dead, which an alert wants apart
    // from other 4xx), else the HTTP status class from the push service, or the relay's refusal.
    public void Pushed(PushGateway.Result r) => pushes.Add(1, new KeyValuePair<string, object?>("outcome", r.Error ?? (r.Status is 404 or 410 ? "gone" : (r.Status / 100) + "xx")));
    public void SignedIn(string outcome) => signIns.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void Roster(string kind, string outcome) =>
        roster.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("outcome", outcome));

    public void Blob(string kind, string outcome, long bytes)
    {
        blobs.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("outcome", outcome));
        if (bytes > 0) { blobBytes.Add(bytes); }
    }
}
