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
    public void Committed_vectors_are_replayed_by_every_implementation()
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
            var ex = Assert.Throws<EnvelopeException>(() => Envelope.Parse(Utf8((string)c!["envelope"]!)).Verify(point));
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
