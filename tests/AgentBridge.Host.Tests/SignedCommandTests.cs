using System.Security.Cryptography;
using System.Text;
using AgentBridge.Host;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The signed command a person's device gives a machine to hand a session to another
// (SignedCommands, docs/protocol/v1-transfer.md): made only by that device for that machine,
// read only by that machine, for one action, within its lifetime, once.
public sealed class SignedCommandTests
{
    // Fixed keys, shared with pwa/test/unit/transfer.test.mjs, which signs the same vector.
    static readonly P256KeyPair Phone = P256KeyPair.FromParameters(
        Convert.FromHexString("905900d4260fa339e62fdd5e34ca9db632ff099271432b0b957d44c389d4576e"),
        Convert.FromHexString("accab67f642cf9f9c35c24175fb780e76d00f0a2782a5af26dba7ac634647b7f"),
        Convert.FromHexString("73d0820dc17ec237ae270a993b2b7400d662284d1b921c83c1b839136163138f"));
    static readonly P256KeyPair Source = P256KeyPair.FromParameters(
        Convert.FromHexString("5f659de036a5d83c281f6c80a44633fc4cae31a37a140c73c21bff1d917b3597"),
        Convert.FromHexString("5d7f19384abde7014a17903abac0264f1add24f8639ab69fb9da7bbfaf24f910"),
        Convert.FromHexString("48ceda706c4697a48859a8fd3cc690992b208a4dcdd4ae0b2c32f3c65cfcde4a"));
    const string Target = "00112233445566778899aabbccddeeff";
    const string Session = "0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6";
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_793_300_000);

    static CommandBody Body(Func<CommandBody, CommandBody>? change = null)
    {
        var body = new CommandBody
        {
            V = 1,
            Action = TransferProtocol.Action,
            Request = Guid.NewGuid().ToString(),
            Principal = new CommandPrincipal { Kind = "user", Device = Wire.DeviceId(Phone.Public) },
            Target = new CommandTarget { Machine = Wire.DeviceId(Source.Public), Session = Session },
            To = Target,
            IssuedAt = Now.ToUnixTimeSeconds(),
            ExpiresAt = Now.AddMinutes(5).ToUnixTimeSeconds(),
        };
        return change is null ? body : change(body);
    }

    static string? Problem(SignedCommand c, DateTimeOffset? at = null) => SignedCommands.Verify(Source, c, TransferProtocol.Action, at ?? Now).Problem;

    [Fact]
    public void The_same_bytes_and_keys_give_the_MAC_the_PWA_computes()
    {
        var body = $"{{\"v\":1,\"action\":\"session.transfer\",\"request\":\"7d0f3a2e-1c4b-4d5e-8f60-112233445566\",\"principal\":{{\"kind\":\"user\",\"device\":\"{Wire.DeviceId(Phone.Public)}\"}},\"target\":{{\"machine\":\"{Wire.DeviceId(Source.Public)}\",\"session\":\"{Session}\"}},\"to\":\"{Target}\",\"issuedAt\":1793300000,\"expiresAt\":1793300300}}";
        var key = SignedCommands.Key(Phone, Source.Public, Wire.DeviceId(Phone.Public), Wire.DeviceId(Source.Public));
        var mac = BlobFormat.Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body)));

        Assert.Equal(Vector, mac);
        // And the machine it is for reads it.
        var command = new SignedCommand { Body = BlobFormat.Base64Url(Encoding.UTF8.GetBytes(body)), By = BlobFormat.Base64Url(Phone.Public), Mac = mac };
        var (read, problem) = SignedCommands.Verify(Source, command, TransferProtocol.Action, Now);
        Assert.Null(problem);
        Assert.Equal((Session, Target), (read!.Target.Session, read.To));
    }

    const string Vector = "_PJ9LEydaL4_cLc4dUQ-wkKfhFj1DfQi-9K7Bl_0fxs";

    [Fact]
    public void A_folder_trust_answer_the_PWA_signs_reads_back_for_its_choice_only()
    {
        // pwa/test/unit/transfer.test.mjs signs exactly these bytes with signTrust.
        const string question = "5d0c9f3e-2b1a-4c8d-9e7f-6a5b4c3d2e1f";
        var machine = Wire.DeviceId(Source.Public);
        var body = $"{{\"v\":1,\"action\":\"launch.trust:trust\",\"request\":\"7d0f3a2e-1c4b-4d5e-8f60-112233445566\",\"principal\":{{\"kind\":\"user\",\"device\":\"{Wire.DeviceId(Phone.Public)}\"}},\"target\":{{\"machine\":\"{machine}\",\"session\":\"{question}\"}},\"to\":\"{machine}\",\"issuedAt\":1793300000,\"expiresAt\":1793300300}}";
        var command = new SignedCommand { Body = BlobFormat.Base64Url(Encoding.UTF8.GetBytes(body)), By = BlobFormat.Base64Url(Phone.Public), Mac = "OO_ZAWL1C15dpDTn1IcnT-RZXcsEtdN-9poKPLlx5bA" };

        var (read, problem) = SignedCommands.Verify(Source, command, TrustProtocol.Action(TrustProtocol.Trust), Now);
        Assert.Null(problem);
        Assert.Equal((question, machine), (read!.Target.Session, read.To));
        Assert.Equal("the command is for launch.trust:trust, not launch.trust:quit", SignedCommands.Verify(Source, command, TrustProtocol.Action(TrustProtocol.Quit), Now).Problem);
    }

    [Fact]
    public void A_command_signed_for_this_machine_reads_back_as_signed()
    {
        var body = Body();
        var (read, problem) = SignedCommands.Verify(Source, SignedCommands.Sign(Phone, Source.Public, body), TransferProtocol.Action, Now);

        Assert.Null(problem);
        Assert.Equal(body, read);
    }

    [Fact]
    public void A_changed_byte_another_key_or_another_machine_is_refused()
    {
        var signed = SignedCommands.Sign(Phone, Source.Public, Body());
        var bytes = BlobFormat.FromBase64Url(signed.Body)!;
        // The machine that carries it rewrites who receives the session.
        var moved = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(Target, "ffeeddccbbaa99887766554433221100"));
        Assert.Equal("the command was not signed for this machine by the device it names", Problem(signed with { Body = BlobFormat.Base64Url(moved) }));

        // Another device claiming to be the phone, with its own key: the key names it.
        using var other = P256KeyPair.Generate();
        var forged = SignedCommands.Sign(other, Source.Public, Body());
        Assert.Equal("the command names a device other than the one that signed it", Problem(forged));
        Assert.Equal("the command was not signed for this machine by the device it names", Problem(forged with { By = signed.By }));

        // Signed for another machine, and carried here.
        using var elsewhere = P256KeyPair.Generate();
        Assert.Equal("the command was not signed for this machine by the device it names", Problem(SignedCommands.Sign(Phone, elsewhere.Public, Body())));
        // Signed for this machine, naming another as the source.
        Assert.Equal("the command is for another machine", Problem(SignedCommands.Sign(Phone, Source.Public, Body(b => b with { Target = b.Target with { Machine = Target } }))));
        Assert.Equal("the signed command is malformed", Problem(signed with { Mac = "not a mac!" }));
    }

    [Fact]
    public void A_command_is_good_for_its_action_and_its_lifetime_only()
    {
        Assert.Equal("the command is for session.stop, not session.transfer", Problem(SignedCommands.Sign(Phone, Source.Public, Body(b => b with { Action = "session.stop" }))));
        Assert.Equal("only a person's device can ask for this", Problem(SignedCommands.Sign(Phone, Source.Public, Body(b => b with { Principal = b.Principal with { Kind = "agent" } }))));
        Assert.Equal("the signed command is version 2; this host reads version 1", Problem(SignedCommands.Sign(Phone, Source.Public, Body(b => b with { V = 2 }))));

        var signed = SignedCommands.Sign(Phone, Source.Public, Body());
        Assert.Null(Problem(signed, Now.AddMinutes(4)));
        Assert.Equal("the command has expired; press again", Problem(signed, Now.AddMinutes(6)));
        // A clock a little ahead is allowed for; one far ahead is not.
        Assert.Null(Problem(signed, Now.AddMinutes(-1)));
        Assert.Equal("the command was signed in the future; check the clocks of the two devices", Problem(signed, Now.AddMinutes(-3)));
        Assert.Equal("a command lasts at most 10 minutes", Problem(SignedCommands.Sign(Phone, Source.Public, Body(b => b with { ExpiresAt = b.IssuedAt + 11 * 60 }))));
    }

    [Fact]
    public void A_command_is_acted_on_once_and_forgotten_only_after_it_has_expired()
    {
        var time = new FakeTimeProvider(Now);
        var memory = new CommandMemory(time);
        var body = Body();

        Assert.True(memory.Take(body));
        Assert.False(memory.Take(body));
        Assert.True(memory.Take(Body()));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(memory.Take(body));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(memory.Take(body));
    }
}
