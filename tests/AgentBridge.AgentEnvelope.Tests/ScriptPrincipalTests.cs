using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.AgentEnvelope;
using Xunit;

public class ScriptPrincipalTests
{
    public static byte[] Body(string identity, string kind = "script")
    {
        var sample = Sample.Body();
        var root = JsonNode.Parse(sample.Encode())!;
        root["principal"] = new JsonObject
        {
            ["kind"] = kind, ["machine"] = sample.Principal.Machine,
            ["add"] = sample.Principal.Add, ["identity"] = identity,
            ["credential"] = sample.Principal.Credential,
        };
        root["chain"]!["via"]![0]!["session"] = identity;
        return Encoding.UTF8.GetBytes(root.ToJsonString(new() { WriteIndented = false }));
    }

    [Fact]
    public void Shared_script_vectors_are_accepted_or_refused_without_changing_canonical_bytes()
    {
        var machine = Sample.Body().Principal.Machine;
        foreach (var item in VectorFiles.Read("script-principals.json")["cases"]!.AsArray())
        {
            var identity = ((string)item!["identity"]!).Replace("{machine}", machine);
            var bytes = Body(identity, (string?)item["kind"] ?? "script");
            if ((bool)item["accepted"]!)
            {
                var parsed = EnvelopeBody.Parse(bytes);
                Assert.Equal(bytes, parsed.Encode());
                using var key = Sample.NewKey();
                Envelope.Parse(Envelope.Seal(parsed, new EcdsaEnvelopeSigner(key))).Verify(Sample.Point(key), parsed.IssuedAt);
            }
            else { Assert.Throws<EnvelopeException>(() => EnvelopeBody.Parse(bytes)); }
        }
    }

    [Fact]
    public void Script_name_and_total_identity_limits_are_enforced()
    {
        var machine = Sample.Body().Principal.Machine;
        EnvelopeBody.Parse(Body($"script:{machine}/{new string('x', 64)}"));
        Assert.Throws<EnvelopeException>(() => EnvelopeBody.Parse(Body($"script:{machine}/{new string('x', 65)}")));
        Assert.Throws<EnvelopeException>(() => EnvelopeBody.Parse(Body($"script:{new string('x', 65)}/pump")));
    }

    [Fact]
    public void A_script_cannot_carry_agent_fields_or_an_agent_carry_a_script_identity()
    {
        var identity = $"script:{Sample.Body().Principal.Machine}/pump";
        var script = JsonNode.Parse(Body(identity))!;
        script["principal"]!["session"] = "agent-a1";
        Assert.Throws<EnvelopeException>(() => EnvelopeBody.Parse(Encoding.UTF8.GetBytes(script.ToJsonString())));
        var agent = JsonNode.Parse(Sample.Body().Encode())!;
        agent["principal"]!["identity"] = identity;
        Assert.Throws<EnvelopeException>(() => EnvelopeBody.Parse(Encoding.UTF8.GetBytes(agent.ToJsonString())));
    }
}
