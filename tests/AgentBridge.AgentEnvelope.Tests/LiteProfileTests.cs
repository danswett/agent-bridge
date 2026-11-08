using System.Text;
using AgentBridge.AgentEnvelope;
using Xunit;

public class LiteProfileTests
{
    static readonly byte[] Text = Encoding.UTF8.GetBytes("{\"text\":\"hello\"}");
    static readonly string Account = new('a', 32);
    const long Now = 1_800_000_000;

    static readonly LiteSender Sender = new(Account, new string('1', 32), Codec.Base64Url(new byte[32].Select(_ => (byte)2).ToArray()),
        "agent-a1", Codec.Base64Url(new byte[12].Select(_ => (byte)3).ToArray()), Codec.Base64Url(new byte[16].Select(_ => (byte)4).ToArray()), "copilot", "agent-bridge");
    static readonly LiteTarget Target = new(new string('2', 32), "agent-b2", Codec.Base64Url(new byte[12].Select(_ => (byte)5).ToArray()));
    const string Request = "3f2b8c1e-5a4d-4e6f-9b7a-1c2d3e4f5a6b";

    static byte[] Seal(System.Security.Cryptography.ECDsa key, int lifetime = 120) =>
        LiteProfile.Seal(Sender, Target, Request, Text, Now, new EcdsaEnvelopeSigner(key), lifetime);

    static string Code(Action a) => Assert.Throws<EnvelopeException>(a).Code;

    [Fact]
    public void A_sealed_lite_reply_parses_and_passes_its_check()
    {
        using var key = Sample.NewKey();
        var env = Envelope.Parse(Seal(key));
        Assert.Equal(LiteProfile.Generation, env.Body.Generation);
        Assert.Equal(OEligible.No, env.Body.Provenance.OEligible);
        Assert.Null(env.Body.Provenance.Origins);
        Assert.Equal(Account, env.Body.Target.Account);
        Assert.Equal(Now + 120, env.Body.ExpiresAt);
        LiteProfile.Check(env, Sample.Point(key), Account, Text, Now + 1);
    }

    [Fact]
    public void A_lifetime_over_120_seconds_is_not_sealed()
    {
        using var key = Sample.NewKey();
        Assert.Equal(Refusal.Lifetime, Code(() => Seal(key, 121)));
    }

    [Fact]
    public void Each_defect_is_refused_with_its_own_code()
    {
        using var key = Sample.NewKey();
        var point = Sample.Point(key);
        var good = Envelope.Parse(Seal(key));

        UnverifiedEnvelope Resigned(Func<EnvelopeBody, EnvelopeBody> edit) =>
            Envelope.Parse(Envelope.Seal(edit(good.Body), new EcdsaEnvelopeSigner(key)));

        Assert.Equal(LiteRefusal.Markers, Code(() => LiteProfile.Check(Resigned(b => b with { Generation = new string('b', 32) }), point, Account, Text, Now)));
        Assert.Equal(LiteRefusal.Markers, Code(() => LiteProfile.Check(Resigned(b => b with { Heads = [Codec.Base64Url(new byte[32].Select(_ => (byte)1).ToArray())] }), point, Account, Text, Now)));
        Assert.Equal(LiteRefusal.Account, Code(() => LiteProfile.Check(good, point, new string('c', 32), Text, Now)));
        Assert.Equal(LiteRefusal.Account, Code(() => LiteProfile.Check(Resigned(b => b with { Target = b.Target with { Account = new string('c', 32) } }), point, Account, Text, Now)));
        Assert.Equal(LiteRefusal.Action, Code(() => LiteProfile.Check(Resigned(b => b with { Action = "session.read", Target = b.Target with { Instance = null } }), point, Account, Text, Now)));
        Assert.Equal(LiteRefusal.OEligible, Code(() => LiteProfile.Check(Resigned(b => b with { Provenance = b.Provenance with { OEligible = OEligible.Yes } }), point, Account, Text, Now)));
        Assert.Equal(LiteRefusal.OEligible, Code(() => LiteProfile.Check(Resigned(b => b with { Provenance = b.Provenance with { OEligible = OEligible.Unknown } }), point, Account, Text, Now)));
        Assert.Equal(Refusal.Lifetime, Code(() => LiteProfile.Check(good, point, Account, Text, Now + 120)));
        Assert.Equal(Refusal.Lifetime, Code(() => LiteProfile.Check(good, point, Account, Text, Now - 6)));
        Assert.Equal(Refusal.Lifetime, Code(() => LiteProfile.Check(good, point, Account, Text, Now - 121)));
        using var other = Sample.NewKey();
        Assert.Equal(Refusal.Signature, Code(() => LiteProfile.Check(good, Sample.Point(other), Account, Text, Now)));
        Assert.Equal(Refusal.Data, Code(() => LiteProfile.Check(good, point, Account, Encoding.UTF8.GetBytes("{\"text\":\"other\"}"), Now)));
    }

    [Fact]
    public void The_roster_path_refuses_a_lite_envelope()
    {
        using var key = Sample.NewKey();
        var body = Envelope.Parse(Seal(key)).Body;
        Assert.Equal(Refusal.Value, Code(() => LiteProfile.RequireRoster(body, LiteProfile.Generation, [LiteProfile.Head])));
        var real = Sample.Body();
        LiteProfile.RequireRoster(real, real.Generation, real.Heads);
    }
}
