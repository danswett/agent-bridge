using System.Diagnostics.Metrics;

namespace AgentBridge.Relay;

// The hook the observability slice (design §5.10) exports from. Aggregates only, and every
// tag is low-cardinality: a kind, an outcome or a close code. Never an account, device,
// address or name, hashed or not; RelayMetricsTests holds every tag key to that allowlist.
public sealed class RelayMetrics
{
    public const string MeterName = "AgentBridge.Relay";
    public static readonly IReadOnlySet<string> AllowedTags = new HashSet<string> { "kind", "outcome", "code" };

    readonly Counter<long> connects, disconnects, refusals, frames, bytes, undeliverable, pushes, signIns, blobs, blobBytes;

    public RelayMetrics(IMeterFactory factory, Func<int> online)
    {
        var meter = factory.Create(MeterName);
        meter.CreateObservableGauge("relay.connections", online, description: "Admitted sockets on this replica");
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
    }

    public void Connected(string kind) => connects.Add(1, new KeyValuePair<string, object?>("kind", kind));
    public void Disconnected(string outcome) => disconnects.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    public void Refused(int code) => refusals.Add(1, new KeyValuePair<string, object?>("code", code));
    public void Forwarded(int length) { frames.Add(1); bytes.Add(length); }
    public void Undeliverable() => undeliverable.Add(1);
    // Push outcomes: the HTTP status class from the push service, or the relay's refusal.
    public void Pushed(PushGateway.Result r) => pushes.Add(1, new KeyValuePair<string, object?>("outcome", r.Error ?? (r.Status / 100) + "xx"));
    public void SignedIn(string outcome) => signIns.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void Blob(string kind, string outcome, long bytes)
    {
        blobs.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("outcome", outcome));
        if (bytes > 0) { blobBytes.Add(bytes); }
    }
}
