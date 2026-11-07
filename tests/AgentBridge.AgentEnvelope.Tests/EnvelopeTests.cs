using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.AgentEnvelope;
using Xunit;

public class EnvelopeVectorTests
{
    const string File = "agent-envelope.json";

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static JsonObject Case(string name, byte[] envelope, string? refusedBy)
    {
        var o = new JsonObject { ["name"] = name, ["envelope"] = Encoding.UTF8.GetString(envelope) };
        if (refusedBy is not null) { o["refusedBy"] = refusedBy; }
        return o;
    }

    static JsonObject Generate()
    {
        using var key = Sample.NewKey();
        var good = Sample.Body();
        var goodBytes = good.Encode();
        var (rootless, positives) = (good, new JsonArray());

        foreach (var action in EnvelopeBody.Actions)
        {
            var body = Sample.Body(action);
            positives.Add(Case($"valid {action}", Envelope.Seal(body, new EcdsaEnvelopeSigner(key)), null));
        }
        var first = Sample.Body("session.read", b => b with { Chain = b.Chain with { Prev = Codec.Base64Url(new byte[32]), Root = "11111111-2222-4333-8444-555555555555", Depth = 2, Messages = 3 } });
        positives.Add(Case("valid reply that follows an earlier message", Envelope.Seal(first, new EcdsaEnvelopeSigner(key)), null));

        var negatives = new JsonArray();
        void Neg(string name, byte[] envelope, string by) => negatives.Add(Case(name, envelope, by));
        byte[] Edited(string from, string to) => Sample.Wrap(key, Utf8(Sample.Replace(goodBytes, from, to)));

        Neg("body with whitespace is not canonical", Sample.Wrap(key, Utf8(Sample.Replace(goodBytes, "\"v\":1,", "\"v\": 1,"))), Refusal.NonCanonical);
        Neg("body with an unknown member", Edited("\"v\":1,", "\"x\":1,\"v\":1,"), Refusal.Unknown);
        Neg("body missing dataSha256", Edited($",\"dataSha256\":\"{good.DataSha256}\"", ""), Refusal.Missing);
        Neg("body with a duplicate member", Edited("\"v\":1,", "\"v\":1,\"v\":1,"), Refusal.Duplicate);
        Neg("body with version 2", Edited("\"v\":1,", "\"v\":2,"), Refusal.Version);
        Neg("action that never verifies", Edited("\"action\":\"session.reply\"", "\"action\":\"session.answer\""), Refusal.Action);
        Neg("person principal", Edited("\"kind\":\"agent\"", "\"kind\":\"person\""), Refusal.Value);
        Neg("lifetime over 120 seconds", Edited("\"expiresAt\":1800000060", "\"expiresAt\":1800000121"), Refusal.Lifetime);
        Neg("target names a link", Edited("\"machine\":\"22222222222222222222222222222222\",", "\"link\":\"x\",\"machine\":\"22222222222222222222222222222222\","), Refusal.Unknown);
        Neg("read without a target session", Sample.Wrap(key, Sample.Body("session.read", b => b with { Target = b.Target with { Session = null } }).Encode()), Refusal.Target);
        Neg("first message whose root is not its request", Sample.Wrap(key, Sample.Body(edit: b => b with { Chain = b.Chain with { Root = "11111111-2222-4333-8444-555555555555" } }).Encode()), Refusal.Chain);
        Neg("sender entry under another account", Sample.Wrap(key, Sample.Body(edit: b => b with { Chain = b.Chain with { Via = [b.Chain.Via[0] with { Account = new string('c', 32) }] } }).Encode()), Refusal.Chain);
        Neg("high-S signature", Sample.Wrap(key, goodBytes, sig: d => P256.Flip(d)), Refusal.HighS);
        Neg("non-minimal DER", Sample.Wrap(key, goodBytes, sig: d => [d[0], (byte)(d[1] + 1), d[2], (byte)(d[3] + 1), 0, .. d[4..]]), Refusal.Der);
        Neg("signature over another purpose's label", Sample.Wrap(key, goodBytes, Framing.F(Framing.Label("agent-bridge roster entry v1"), goodBytes)), Refusal.Signature);
        Neg("signature over the raw body", Sample.Wrap(key, goodBytes, goodBytes), Refusal.Signature);
        using (var other = Sample.NewKey()) { Neg("signature by another key", Sample.Wrap(other, goodBytes), Refusal.Signature); }
        Neg("body with padding", Utf8($"{{\"body\":\"{Codec.Base64Url(goodBytes)}=\",\"sig\":\"{Codec.Base64Url(P256.Normalize(key.SignData(Framing.AgentCommand(goodBytes), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)))}\"}}"), Refusal.Base64);
        Neg("members swapped", SwapMembers(Sample.Wrap(key, goodBytes)), Refusal.NonCanonical);
        Neg("envelope over 2 KiB", new byte[2049].Select(_ => (byte)' ').ToArray(), Refusal.TooLarge);

        Neg("version written 1.0", Edited("\"v\":1,", "\"v\":1.0,"), Refusal.Value);
        Neg("number with a leading zero", Edited("\"v\":1,", "\"v\":01,"), Refusal.Json);
        Neg("number with an exponent", Edited("\"rootAt\":1800000000", "\"rootAt\":18e8"), Refusal.Value);
        Neg("number above 2^53 - 1", Edited("\"rootAt\":1800000000", "\"rootAt\":9007199254740992"), Refusal.Value);
        Neg("body with a byte-order mark", Sample.Wrap(key, [0xEF, 0xBB, 0xBF, .. goodBytes]), Refusal.Json);
        Neg("body that is not UTF-8", Sample.Wrap(key, [.. goodBytes[..^1], 0xC3, goodBytes[^1]]), Refusal.Json);
        Neg("body with data after it", Sample.Wrap(key, [.. goodBytes, .. "x"u8]), Refusal.Json);
        Neg("body with a lone surrogate", Edited("\"account\":\"" + good.Account + "\"", "\"account\":\"\\ud800\""), Refusal.Json);
        Neg("body nested past 16 levels", Sample.Wrap(key, Utf8(new string('[', 17) + new string(']', 17))), Refusal.Json);
        Neg("envelope with data after it", [.. Sample.Wrap(key, goodBytes), .. "x"u8], Refusal.Json);
        {
            var sealedEnvelope = Encoding.UTF8.GetString(Sample.Wrap(key, goodBytes));
            var tail = sealedEnvelope.IndexOf("\",\"sig\"", StringComparison.Ordinal) - 1;
            var spelled = Codec.Base64Url(goodBytes);
            // The last body character with its unused low bits set: another spelling of the same bytes.
            var last = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".IndexOf(spelled[^1]);
            var flipped = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"[last ^ 1];
            if (Codec.TryBase64Url(spelled[..^1] + flipped, out _)) { throw new InvalidOperationException("sample has no spare bits"); }
            Neg("body base64 with non-zero trailing bits", Utf8(sealedEnvelope[..tail] + flipped + sealedEnvelope[(tail + 1)..]), Refusal.Base64);
        }
        Neg("session.list with a target session", Sample.Wrap(key, Sample.Body("sessions.list", b => b with { Target = b.Target with { Session = "agent-b2" } }).Encode()), Refusal.Target);
        Neg("session.launch with a target instance", Sample.Wrap(key, Sample.Body("session.launch", b => b with { Target = b.Target with { Session = "agent-b2", Instance = Codec.Base64Url(new byte[12]) } }).Encode()), Refusal.Target);
        Neg("session.get with a target instance and no session", Sample.Wrap(key, Sample.Body("session.get", b => b with { Target = b.Target with { Session = null, Instance = Codec.Base64Url(new byte[12]) } }).Encode()), Refusal.Target);
        Neg("session.reply without a target instance", Sample.Wrap(key, Sample.Body(edit: b => b with { Target = b.Target with { Instance = null } }).Encode()), Refusal.Target);
        Neg("launch.result without a target instance", Sample.Wrap(key, Sample.Body("launch.result", b => b with { Target = b.Target with { Instance = null } }).Encode()), Refusal.Target);
        Neg("launch.result without a target session", Sample.Wrap(key, Sample.Body("launch.result", b => b with { Target = b.Target with { Session = null } }).Encode()), Refusal.Target);
        {
            var offCurve = Sample.Point(key);
            offCurve[^1] ^= 1;
            negatives.Add(new JsonObject
            {
                ["name"] = "hostSign point that is not on the curve",
                ["envelope"] = Encoding.UTF8.GetString(Sample.Wrap(key, goodBytes)),
                ["hostSign"] = Codec.Base64Url(offCurve),
                ["refusedBy"] = Refusal.Point,
            });
        }

        return new JsonObject
        {
            ["description"] = "Signed agent-command envelopes (docs/design/agent-principals.md §4.3). Each envelope is verified with hostSign. A case with refusedBy is refused with that code; one without verifies.",
            ["hostSign"] = Codec.Base64Url(Sample.Point(key)),
            ["valid"] = positives,
            ["refused"] = negatives,
        };
    }

    static byte[] SwapMembers(byte[] envelope)
    {
        var text = Encoding.UTF8.GetString(envelope);
        var i = text.IndexOf(",\"sig\":", StringComparison.Ordinal);
        return Utf8("{" + text[(i + 1)..^1] + "," + text[1..i] + "}");
    }

    // Signatures are not deterministic, so the committed file is the source of truth: it is written
    // only when asked for, and every other run replays it.
    [Fact]
    public void Committed_vectors_are_replayed_by_the_csharp_library()
    {
        if (Environment.GetEnvironmentVariable(VectorFiles.UpdateVariable) == "1")
        {
            Directory.CreateDirectory(VectorFiles.Folder);
            System.IO.File.WriteAllText(Path.Combine(VectorFiles.Folder, File), VectorFiles.Text(Generate()));
        }
        var vectors = VectorFiles.Read(File);
        Assert.True(Codec.TryBase64Url((string)vectors["hostSign"]!, out var point));

        foreach (var c in vectors["valid"]!.AsArray())
        {
            var verified = Envelope.Parse(Utf8((string)c!["envelope"]!)).Verify(point);
            Assert.Equal(Utf8((string)c["envelope"]!), Envelope.Encode(verified.BodyBytes, Envelope.Parse(Utf8((string)c["envelope"]!)).Signature));
        }
        foreach (var c in vectors["refused"]!.AsArray())
        {
            var key = c!["hostSign"] is { } own && Codec.TryBase64Url((string)own!, out var ownPoint) ? ownPoint : point;
            var ex = Assert.Throws<EnvelopeException>(() => Envelope.Parse(Utf8((string)c!["envelope"]!)).Verify(key));
            Assert.True((string)c!["refusedBy"]! == ex.Code, $"{c["name"]}: expected {c["refusedBy"]}, got {ex.Code} ({ex.Where})");
        }
    }

    [Fact]
    public void Every_listed_action_has_a_valid_vector_and_nothing_else_verifies()
    {
        var names = VectorFiles.Read(File)["valid"]!.AsArray().Select(c => (string)c!["name"]!).ToList();
        foreach (var action in EnvelopeBody.Actions) { Assert.Contains($"valid {action}", names); }
        foreach (var action in new[] { "session.answer", "launch.trust.answer", "session.stop", "session.transfer", "attachment.put", "update.apply", "push.send", "host.restart", "sessions.subscribe" })
        {
            var body = Sample.Body(edit: b => b with { Action = action });
            var ex = Assert.Throws<EnvelopeException>(() => EnvelopeBody.Parse(body.Encode()));
            Assert.Equal(Refusal.Action, ex.Code);
        }
    }

    [Theory]
    [InlineData("account")]
    [InlineData("session")]
    [InlineData("action")]
    public void Seal_refuses_a_body_that_would_not_parse_back(string field)
    {
        using var key = Sample.NewKey();
        var body = field switch
        {
            "account" => Sample.Body(edit: b => b with { Account = "a\",\"x\":\"b" }),
            "session" => Sample.Body(edit: b => b with { Target = b.Target with { Session = "a\"b" } }),
            _ => Sample.Body(edit: b => b with { Action = "session.answer" }),
        };
        Assert.Throws<EnvelopeException>(() => Envelope.Seal(body, new EcdsaEnvelopeSigner(key)));
    }

    [Fact]
    public void A_sealed_envelope_round_trips_and_binds_its_data()
    {
        using var key = Sample.NewKey();
        var data = Utf8("{\"text\":\"hello\"}");
        var verified = Envelope.Parse(Envelope.Seal(Sample.Body(), new EcdsaEnvelopeSigner(key))).Verify(Sample.Point(key));
        Assert.True(verified.Body.BindsData(data));
        Assert.False(verified.Body.BindsData(Utf8("{\"text\":\"HELLO\"}")));
        Assert.Equal(Sample.Body(), verified.Body, EqualityComparer<EnvelopeBody>.Create((a, b) => a!.Encode().AsSpan().SequenceEqual(b!.Encode())));
    }
}
