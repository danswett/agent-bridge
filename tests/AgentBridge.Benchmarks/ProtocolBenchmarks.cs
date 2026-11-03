using System.Text.Json;
using AgentBridge.Protocol;
using BenchmarkDotNet.Attributes;

namespace AgentBridge.Benchmarks;

// A session.upsert as it crosses a channel: the host serializes the event (SecureChannel's
// {ev, seq, data}), and a client parses it and reads the session (HostConnection).
public class ProtocolBenchmarks
{
    Session _session = null!;
    byte[] _json = null!;

    [Params(Shape.Typical, Shape.P99, Shape.Oversized)]
    public Shape Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _session = Fixtures.Upsert(Size);
        _json = Serialize();
    }

    [Benchmark]
    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(new { ev = Events.SessionUpsert, seq = 42L, data = _session }, ProtocolJson.Options);

    [Benchmark]
    public Session? Parse()
    {
        using var doc = JsonDocument.Parse(_json);
        return doc.RootElement.GetProperty("data").Deserialize<Session>(ProtocolJson.Options);
    }
}
