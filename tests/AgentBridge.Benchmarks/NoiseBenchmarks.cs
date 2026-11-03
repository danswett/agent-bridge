using System.Text;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using BenchmarkDotNet.Attributes;

namespace AgentBridge.Benchmarks;

// Opening a channel: the whole Noise XX handshake, both sides, as ChannelHub runs it (an empty
// first message, then each side's introduction). Every reconnect of every device pays it.
public class NoiseHandshakeBenchmarks
{
    P256KeyPair _phone = null!;
    P256KeyPair _host = null!;
    readonly byte[] _prologue = Encoding.ASCII.GetBytes("agent-bridge-s4|0123456789abcdef0123456789abcdef|fedcba9876543210fedcba9876543210|\0\0\0\0\0\0\0\x01");
    readonly byte[] _intro = Encoding.UTF8.GetBytes("{\"name\":\"DEV-BOX-1\",\"kind\":\"host\"}");

    [GlobalSetup]
    public void Setup()
    {
        _phone = P256KeyPair.Generate();
        _host = P256KeyPair.Generate();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _phone.Dispose();
        _host.Dispose();
    }

    [Benchmark]
    public byte[] Handshake()
    {
        var initiator = new XXHandshake(true, _phone, _prologue);
        var responder = new XXHandshake(false, _host, _prologue);
        responder.ReadMessage(initiator.WriteMessage([]));
        initiator.ReadMessage(responder.WriteMessage(_intro));
        responder.ReadMessage(initiator.WriteMessage(_intro));
        return initiator.HandshakeHash;
    }
}

// One transport message each way, at the sizes an upsert is (design.md §5.9): every frame a
// channel carries is one of these.
public class NoiseTransportBenchmarks
{
    readonly CipherState _send = new();
    readonly CipherState _receive = new();
    byte[] _key = null!;
    byte[] _plain = null!;
    byte[] _cipher = null!;

    [Params(Shape.Typical, Shape.P99, Shape.Oversized)]
    public Shape Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _key = new byte[32];
        new Random(1).NextBytes(_key);
        _plain = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { ev = Events.SessionUpsert, seq = 42L, data = Fixtures.Upsert(Size) }, ProtocolJson.Options);
        _send.InitializeKey(_key);
        _cipher = _send.EncryptWithAd([], _plain);
    }

    // A fresh nonce count each time, so the receiver always expects the message it is given.
    [Benchmark]
    public byte[] Encrypt()
    {
        _send.InitializeKey(_key);
        return _send.EncryptWithAd([], _plain);
    }

    [Benchmark]
    public byte[] Decrypt()
    {
        _receive.InitializeKey(_key);
        return _receive.DecryptWithAd([], _cipher);
    }
}
