using System.Security.Cryptography;
using System.Text.Json;
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
