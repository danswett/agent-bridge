using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentBridge.Transport.Tests;

public class NoiseTests
{
    static byte[] H(JsonElement e) => Convert.FromHexString(e.GetString()!);

    static P256KeyPair Key(JsonElement k) => P256KeyPair.FromParameters(H(k.GetProperty("d")), H(k.GetProperty("x")), H(k.GetProperty("y")));

    // testdata/noise-xx-p256-vectors.json is spike S2's vector file, which the WebCrypto
    // implementation the PWA uses replays in Node, Chromium and WebKit. Matching it byte for
    // byte keeps this copy of the channel interoperable with the browser.
    [Fact]
    public void The_handshake_and_transport_messages_reproduce_the_vectors_the_browser_implementation_is_checked_against()
    {
        var v = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "noise-xx-p256-vectors.json"))).RootElement;
        Assert.Equal(NoiseConst.ProtocolName, v.GetProperty("protocol").GetString());
        var keys = v.GetProperty("keys");
        var prologue = H(v.GetProperty("prologue"));
        var ini = new XXHandshake(true, Key(keys.GetProperty("initStatic")), prologue, Key(keys.GetProperty("initEphemeral")));
        var res = new XXHandshake(false, Key(keys.GetProperty("respStatic")), prologue, Key(keys.GetProperty("respEphemeral")));

        foreach (var m in v.GetProperty("handshake").EnumerateArray())
        {
            var (w, r) = m.GetProperty("from").GetString() == "i" ? (ini, res) : (res, ini);
            var payload = H(m.GetProperty("payload"));
            var message = w.WriteMessage(payload);
            Assert.Equal(H(m.GetProperty("message")), message);
            Assert.Equal(payload, r.ReadMessage(message));
        }
        Assert.Equal(H(v.GetProperty("handshakeHash")), ini.HandshakeHash);
        Assert.Equal(ini.HandshakeHash, res.HandshakeHash);
        foreach (var m in v.GetProperty("transport").EnumerateArray())
        {
            var (w, r) = m.GetProperty("from").GetString() == "i" ? (ini, res) : (res, ini);
            var payload = H(m.GetProperty("payload"));
            var message = w.Send!.EncryptWithAd([], payload);
            Assert.Equal(H(m.GetProperty("message")), message);
            Assert.Equal(payload, r.Receive!.DecryptWithAd([], message));
        }
    }

    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    static string VectorsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentBridge.slnx"))) { return Path.Combine(dir.FullName, "docs", "protocol", "vectors"); }
        }
        throw new DirectoryNotFoundException("the repository (AgentBridge.slnx) is not above " + AppContext.BaseDirectory);
    }

    // IK (#603) with the XX vectors' keys and prologue: docs/protocol/vectors/noise-ik-p256.json,
    // which the PWA's unit tests replay with WebCrypto. Generated here: after a deliberate
    // change, UPDATE_PROTOCOL_VECTORS=1 writes it, as for the other vectors.
    [Fact]
    public void The_IK_handshake_and_transport_messages_are_the_vectors_the_browser_implementation_replays()
    {
        var xx = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "noise-xx-p256-vectors.json"))).RootElement;
        var keys = xx.GetProperty("keys");
        var prologue = H(xx.GetProperty("prologue"));
        using var initStatic = Key(keys.GetProperty("initStatic"));
        using var respStatic = Key(keys.GetProperty("respStatic"));
        var ini = new IKHandshake(true, initStatic, prologue, respStatic.Public, Key(keys.GetProperty("initEphemeral")));
        var res = new IKHandshake(false, respStatic, prologue, fixedEphemeral: Key(keys.GetProperty("respEphemeral")));

        var handshake = new JsonArray();
        foreach (var (from, text) in new[] { ("i", """{"name":"Phone","kind":"pwa","early":{"op":"hello","data":{}}}"""), ("r", """{"name":"DEV-BOX-1","kind":"host","ik":["hello"],"early":{"ok":true,"result":{"protocol":1}}}""") })
        {
            var (w, r) = from == "i" ? (ini, res) : (res, ini);
            var payload = Encoding.UTF8.GetBytes(text);
            var message = w.WriteMessage(payload);
            Assert.Equal(payload, r.ReadMessage(message));
            handshake.Add(new JsonObject { ["from"] = from, ["payload"] = Hex(payload), ["message"] = Hex(message) });
        }
        Assert.True(ini.IsComplete && res.IsComplete);
        Assert.Equal(ini.HandshakeHash, res.HandshakeHash);
        Assert.Equal(initStatic.Public, res.RemoteStatic);
        var transport = new JsonArray();
        foreach (var (from, text) in new[] { ("i", """{"id":1,"op":"sessions.subscribe","data":{}}"""), ("r", """{"re":1,"ok":true,"result":{"revision":1,"count":0}}"""), ("r", "café – 世界 ✓") })
        {
            var (w, r) = from == "i" ? (ini, res) : (res, ini);
            var payload = Encoding.UTF8.GetBytes(text);
            var message = w.Send!.EncryptWithAd([], payload);
            Assert.Equal(payload, r.Receive!.DecryptWithAd([], message));
            transport.Add(new JsonObject { ["from"] = from, ["payload"] = Hex(payload), ["message"] = Hex(message) });
        }
        var generated = new JsonObject
        {
            ["protocol"] = IKHandshake.ProtocolName,
            ["generatedBy"] = "tests/AgentBridge.Transport.Tests/NoiseTests.cs",
            ["keys"] = JsonNode.Parse(keys.GetRawText()),
            ["prologue"] = Hex(prologue),
            ["handshake"] = handshake,
            ["handshakeHash"] = Hex(ini.HandshakeHash),
            ["transport"] = transport,
        };

        var path = Path.Combine(VectorsFolder(), "noise-ik-p256.json");
        var text2 = generated.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
        var committed = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
        if (committed == text2) { return; }
        if (Environment.GetEnvironmentVariable("UPDATE_PROTOCOL_VECTORS") == "1")
        {
            File.WriteAllText(path, text2);
            return;
        }
        Assert.Fail($"docs/protocol/vectors/noise-ik-p256.json is not what IK gives; with UPDATE_PROTOCOL_VECTORS=1 this test writes it. It gives:\n{text2}");
    }

    [Fact]
    public void An_IK_first_message_to_a_key_the_responder_does_not_hold_does_not_decrypt()
    {
        using var a = P256KeyPair.Generate();
        using var b = P256KeyPair.Generate();
        using var stale = P256KeyPair.Generate();
        var ini = new IKHandshake(true, a, [], stale.Public);
        var res = new IKHandshake(false, b, []);

        Assert.ThrowsAny<CryptographicException>(() => res.ReadMessage(ini.WriteMessage([])));
    }

    [Fact]
    public void An_IK_opener_refuses_a_responder_key_off_the_curve_before_any_ECDH()
    {
        using var a = P256KeyPair.Generate();
        using var b = P256KeyPair.Generate();
        var off = (byte[])b.Public.Clone();
        off[64] ^= 1;

        Assert.Throws<CryptographicException>(() => new IKHandshake(true, a, [], off));
    }

    [Fact]
    public void Public_keys_off_the_curve_or_outside_the_field_are_refused_and_ECDH_agrees_both_ways()
    {
        using var good = P256KeyPair.Generate();
        using var other = P256KeyPair.Generate();
        var off = (byte[])good.Public.Clone();
        off[64] ^= 1;
        var outside = new byte[65];
        outside[0] = 4;
        Convert.FromHexString("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff").CopyTo(outside, 1);

        P256KeyPair.AssertOnCurve(good.Public);
        Assert.Throws<CryptographicException>(() => P256KeyPair.AssertOnCurve(off));
        Assert.Throws<CryptographicException>(() => P256KeyPair.AssertOnCurve(outside));
        Assert.Throws<CryptographicException>(() => good.Dh(off));
        Assert.Equal(good.Dh(other.Public), other.Dh(good.Public));
    }

    [Fact]
    public void A_tampered_transport_message_is_refused_and_the_receiver_can_still_read_the_genuine_one()
    {
        using var a = P256KeyPair.Generate();
        using var b = P256KeyPair.Generate();
        var ini = new XXHandshake(true, a, []);
        var res = new XXHandshake(false, b, []);
        res.ReadMessage(ini.WriteMessage([]));
        ini.ReadMessage(res.WriteMessage([]));
        res.ReadMessage(ini.WriteMessage([]));

        var message = ini.Send!.EncryptWithAd([], [1, 2, 3]);
        var tampered = (byte[])message.Clone();
        tampered[0] ^= 1;

        Assert.ThrowsAny<CryptographicException>(() => res.Receive!.DecryptWithAd([], tampered));
        Assert.Equal([1, 2, 3], res.Receive!.DecryptWithAd([], message));
    }
}
