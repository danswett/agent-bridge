using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// The signed command a person's device gives a machine (docs/protocol/v1-transfer.md, "The signed
// command"), as shared vectors: fixed keys, the commands they sign and the MAC each gives, and
// commands the machine they are for must refuse, with why. pwa/test/unit/protocol-vectors.test.mjs
// signs the valid ones again and must get the same bytes; a host in any language must read them
// and refuse the rest. Generated here, beside SignedCommands, since only the host holds the keys'
// arithmetic; docs/protocol/vectors/README.md says how to regenerate them.
public sealed class SignedCommandVectorTests
{
    const string File = "signed-command.json";
    const string Generator = "tests/AgentBridge.Host.Tests/SignedCommandVectorTests.cs";

    // The keys SignedCommandTests uses: a phone, and the machine it signs for.
    static readonly (string D, string X, string Y) PhoneKey = ("905900d4260fa339e62fdd5e34ca9db632ff099271432b0b957d44c389d4576e", "accab67f642cf9f9c35c24175fb780e76d00f0a2782a5af26dba7ac634647b7f", "73d0820dc17ec237ae270a993b2b7400d662284d1b921c83c1b839136163138f");
    static readonly (string D, string X, string Y) MachineKey = ("5f659de036a5d83c281f6c80a44633fc4cae31a37a140c73c21bff1d917b3597", "5d7f19384abde7014a17903abac0264f1add24f8639ab69fb9da7bbfaf24f910", "48ceda706c4697a48859a8fd3cc690992b208a4dcdd4ae0b2c32f3c65cfcde4a");
    static P256KeyPair Key((string D, string X, string Y) k) => P256KeyPair.FromParameters(Convert.FromHexString(k.D), Convert.FromHexString(k.X), Convert.FromHexString(k.Y));
    static readonly P256KeyPair Phone = Key(PhoneKey);
    static readonly P256KeyPair Machine = Key(MachineKey);
    static readonly string PhoneId = Wire.DeviceId(Phone.Public);
    static readonly string MachineId = Wire.DeviceId(Machine.Public);

    const string Target = "00112233445566778899aabbccddeeff";
    const string Session = "0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6";
    const string Question = "5d0c9f3e-2b1a-4c8d-9e7f-6a5b4c3d2e1f";
    const string Request = "7d0f3a2e-1c4b-4d5e-8f60-112233445566";
    const long Issued = 1_793_300_000;
    // The PWA's lifetime (COMMAND_LIFETIME_S), so it signs these very bodies.
    const long Expires = Issued + 300;

    static CommandBody Body(string action, string session, string to) => new()
    {
        V = 1, Action = action, Request = Request,
        Principal = new CommandPrincipal { Kind = "user", Device = PhoneId },
        Target = new CommandTarget { Machine = MachineId, Session = session },
        To = to, IssuedAt = Issued, ExpiresAt = Expires,
    };

    static readonly CommandBody Transfer = Body(TransferProtocol.Action, Session, Target);

    static JsonObject Generate()
    {
        static JsonObject KeyJson((string D, string X, string Y) k, P256KeyPair pair) => new()
        {
            ["d"] = k.D, ["x"] = k.X, ["y"] = k.Y,
            ["public"] = BlobFormat.Base64Url(pair.Public),
            ["device"] = Wire.DeviceId(pair.Public),
        };
        var valid = new JsonArray();
        void Valid(string name, CommandBody body, string action)
        {
            var command = SignedCommands.Sign(Phone, Machine.Public, body);
            valid.Add(new JsonObject
            {
                ["name"] = name, ["action"] = action, ["now"] = Issued + 60,
                ["body"] = JsonSerializer.SerializeToNode(body, ProtocolJson.Options),
                ["command"] = JsonSerializer.SerializeToNode(command, ProtocolJson.Options),
            });
        }
        Valid("a-transfer", Transfer, TransferProtocol.Action);
        Valid("a-folder-trust-answer", Body(TrustProtocol.Action(TrustProtocol.Trust), Question, MachineId), TrustProtocol.Action(TrustProtocol.Trust));
        Valid("a-folder-trust-quit", Body(TrustProtocol.Action(TrustProtocol.Quit), Question, MachineId), TrustProtocol.Action(TrustProtocol.Quit));
        // Naming the question and exactly what was read, so no other question's Approve rides it (#803).
        var confirm = Body(EscapedApproval.Action, Session, MachineId);
        Valid("an-escaped-approval-confirmed", confirm with { Target = confirm.Target with { Question = "req-42#9b1c", Shown = EscapedApproval.Shown("Allow **shell**?") } }, EscapedApproval.Action);

        var refused = new JsonArray();
        var signed = SignedCommands.Sign(Phone, Machine.Public, Transfer);
        void Refused(string name, SignedCommand command, long now = Issued + 60, string action = TransferProtocol.Action)
        {
            var problem = SignedCommands.Verify(Machine, command, action, DateTimeOffset.FromUnixTimeSeconds(now)).Problem
                ?? throw new InvalidOperationException($"{name} is meant to be refused");
            refused.Add(new JsonObject
            {
                ["name"] = name, ["action"] = action, ["now"] = now,
                ["command"] = JsonSerializer.SerializeToNode(command, ProtocolJson.Options), ["reason"] = problem,
            });
        }
        SignedCommand Sign(Func<CommandBody, CommandBody> change) => SignedCommands.Sign(Phone, Machine.Public, change(Transfer));
        var moved = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(BlobFormat.FromBase64Url(signed.Body)!).Replace(Target, "ffeeddccbbaa99887766554433221100", StringComparison.Ordinal));
        Refused("a-changed-byte", signed with { Body = BlobFormat.Base64Url(moved) });
        // The machine signing a command as if from the phone: its own key gives it away.
        Refused("signed-by-a-key-other-than-the-device-it-names", SignedCommands.Sign(Machine, Machine.Public, Transfer));
        Refused("signed-for-another-machine", SignedCommands.Sign(Phone, Phone.Public, Transfer));
        Refused("for-another-action", signed, action: "session.stop");
        Refused("expired", signed, now: Expires + 1);
        Refused("signed-more-than-two-minutes-ahead", signed, now: Issued - 121);
        Refused("lasting-over-ten-minutes", Sign(b => b with { ExpiresAt = b.IssuedAt + 601 }));
        Refused("version-2", Sign(b => b with { V = 2 }));
        Refused("from-an-agent", Sign(b => b with { Principal = b.Principal with { Kind = "agent" } }));
        Refused("naming-another-machine-as-the-source", Sign(b => b with { Target = b.Target with { Machine = Target } }));
        Refused("a-request-that-is-not-a-uuid", Sign(b => b with { Request = "again" }));
        Refused("a-malformed-mac", signed with { Mac = "not a mac!" });
        Refused("a-mac-of-the-wrong-length", signed with { Mac = signed.Mac[..20] });

        return new JsonObject
        {
            ["about"] = "Signed commands: a person's device signs one for one machine, which acts on it once, for its action, within its lifetime.",
            ["spec"] = "v1-transfer.md",
            ["generatedBy"] = Generator,
            ["keys"] = new JsonObject { ["signer"] = KeyJson(PhoneKey, Phone), ["machine"] = KeyJson(MachineKey, Machine) },
            ["valid"] = valid,
            ["refused"] = refused,
            ["knownDifferences"] = new JsonArray(),
        };
    }

    [Fact]
    public void The_committed_signed_command_vectors_are_the_ones_the_host_generates()
    {
        var stale = VectorFiles.Stale(new Dictionary<string, JsonObject> { [File] = Generate() });
        Assert.True(stale.Count == 0, $"docs/protocol/vectors/{File} differs from what {Generator} generates. If the change is meant, regenerate it ({VectorFiles.UpdateVariable}=1, docs/protocol/vectors/README.md) and commit it with it.");
    }

    [Fact]
    public void A_valid_signed_command_reads_back_as_its_body_on_the_machine_it_is_for()
    {
        var json = VectorFiles.Read(File);
        var machine = Key(Hex(json["keys"]!["machine"]!));
        foreach (var v in json["valid"]!.AsArray())
        {
            var command = v!["command"].Deserialize<SignedCommand>(ProtocolJson.Options)!;
            var (body, problem) = SignedCommands.Verify(machine, command, v["action"]!.GetValue<string>(), DateTimeOffset.FromUnixTimeSeconds(v["now"]!.GetValue<long>()));

            Assert.Null(problem);
            Assert.True(JsonNode.DeepEquals(v["body"], JsonSerializer.SerializeToNode(body, ProtocolJson.Options)), v["name"]!.GetValue<string>());
        }
    }

    [Fact]
    public void A_refused_signed_command_is_refused_for_the_reason_it_gives()
    {
        var json = VectorFiles.Read(File);
        var machine = Key(Hex(json["keys"]!["machine"]!));
        foreach (var v in json["refused"]!.AsArray())
        {
            var command = v!["command"].Deserialize<SignedCommand>(ProtocolJson.Options)!;
            var problem = SignedCommands.Verify(machine, command, v["action"]!.GetValue<string>(), DateTimeOffset.FromUnixTimeSeconds(v["now"]!.GetValue<long>())).Problem;
            Assert.Equal(v["reason"]!.GetValue<string>(), problem);
        }
    }

    static (string D, string X, string Y) Hex(JsonNode k) => (k["d"]!.GetValue<string>(), k["x"]!.GetValue<string>(), k["y"]!.GetValue<string>());
}
