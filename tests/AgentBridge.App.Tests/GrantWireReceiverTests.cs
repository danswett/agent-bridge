using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.App.Core;
using AgentBridge.App.Core.Grants;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Roster;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed partial class GrantWireReceiverTests
{
    [Fact]
    public async Task Production_preparation_is_unavailable_without_the_typed_contract()
    {
        using var wire = new Dispatcher("none");
        var receiver = new GrantWireReceiver(wire, wire.Binding.Receiver, "default", () => null);
        var result = await receiver.PrepareIssueAsync(JsonSerializer.SerializeToElement(new { }));
        Assert.Null(result.Preview);
        Assert.Equal("unavailable", result.Problem);
        Assert.Empty(wire.Calls);
    }

    [Theory]
    [InlineData("rotation", FleetGrantReadProblem.RotationRequired, true)]
    [InlineData("signer-missing", FleetGrantReadProblem.RotationRequired, false)]
    [InlineData("none", FleetGrantReadProblem.None, true)]
    [InlineData("other", FleetGrantReadProblem.Other, false)]
    [InlineData("unavailable", FleetGrantReadProblem.Unavailable, false)]
    [InlineData("read-context", FleetGrantReadProblem.Unavailable, false)]
    [InlineData("read-proof", FleetGrantReadProblem.Unavailable, false)]
    [InlineData("binding-proof", FleetGrantReadProblem.Unavailable, false)]
    [InlineData("read-binding", FleetGrantReadProblem.Unavailable, false)]
    public async Task Read_readiness_requires_verified_context_binding_and_a_person_signer(
        string change, FleetGrantReadProblem expected, bool ready)
    {
        using var wire = new Dispatcher(change);
        using var app = P256KeyPair.Generate();
        var person = new Signer(app, () => Assert.Fail("A read must not sign a command"));
        IGrantIssueReceiver receiver = new GrantWireReceiver(wire, wire.Binding.Receiver, "default",
            () => change == "signer-missing" ? null : person);
        var reader = receiver as IVerifiedGrantReader ?? new UnavailableVerifiedGrantReader();
        var result = await reader.ReadAsync();
        Assert.Equal(expected, result.Problem);
        Assert.Equal(ready, result.G7Ready);
        Assert.Equal(change == "binding-proof" ? 0 : 1, wire.Calls.Count);
        Assert.All(wire.Calls, call => Assert.Equal("grant.read", call.Context.Action));
    }

    [Theory]
    [InlineData("proof")]
    [InlineData("challenge")]
    [InlineData("receipt")]
    [InlineData("binding")]
    [InlineData("binding-proof")]
    [InlineData("missing-rotated")]
    [InlineData("rotated-type")]
    [InlineData("receipt-proof")]
    [InlineData("rotation-refused")]
    [InlineData("rotated")]
    [InlineData("none")]
    public async Task Only_a_matching_verified_preparation_and_receipt_can_become_active(string change)
    {
        using var wire = new Dispatcher(change);
        using var app = P256KeyPair.Generate();
        var signed = 0;
        var person = new Signer(app, () => signed++);
        var receiver = new GrantWireReceiver(wire, wire.Binding.Receiver, "default", () => person);
        var model = new GrantIssueViewModel(new PreparedReceiver(receiver, wire));
        await model.PrepareAsync(JsonSerializer.SerializeToElement(new { }));
        Assert.Equal(0, signed);
        if (change is "proof" or "challenge" or "binding-proof") { Assert.False(model.CanConfirm); return; }
        await model.ConfirmAsync("not-the-reviewed-digest");
        Assert.Equal(0, signed);
        await model.ConfirmAsync(model.Digest);
        Assert.Equal(change == "binding" ? 0 : 1, signed);
        Assert.Equal(change is "none" or "rotated", model.Active);
        if (change == "rotation-refused") { Assert.Equal(
            "Grant refused: this machine's audit log needs rotation and could not be rotated automatically (RotationRequired). Nothing was issued.", model.Problem); }
        if (model.Active) { Assert.Equal(change == "rotated", model.Receipt!.Rotated); }
        if (change == "binding") { Assert.Single(wire.Calls); return; }
        Assert.Equal(2, wire.Calls.Select(call => call.Context.Request).Distinct().Count());
        var confirm = wire.Calls[^1];
        Assert.Equal("grant.confirmIssue", confirm.Context.Action);
        var command = confirm.Command!.Value.Deserialize<SignedCommand>(ProtocolJson.Options)!;
        var body = JsonSerializer.Deserialize<CommandBody>(BlobFormat.FromBase64Url(command.Body)!, ProtocolJson.Options)!;
        Assert.Equal(confirm.Context.Action, body.Action);
        Assert.Equal(confirm.Context.Request.ToString("D"), body.Request);
        Assert.True(JsonElement.DeepEquals(body.Args!.Value,
            JsonSerializer.SerializeToElement(new { context = confirm.Context, args = confirm.Args }, ProtocolJson.Options)));
    }

    sealed class Signer(P256KeyPair key, Action signed) : IGrantConfirmationSigner
    {
        public SignedCommand SignGrantConfirmation(byte[] receiverKey, FleetGrantProofContext context,
            FleetGrantConfirmation args, DateTimeOffset now)
        {
            signed();
            return Transfers.SignGrantConfirmation(key, receiverKey, context, args, now);
        }
    }

    // Test-only preparation; production stays unavailable until the canonical contract exists.
    sealed class PreparedReceiver(GrantWireReceiver inner, Dispatcher wire) : IGrantIssueReceiver
    {
        public string Receiver => inner.Receiver;
        public bool Online => inner.Online;
        public string? Problem => inner.Problem;
        public Task<GrantReadResult> ReadAsync(CancellationToken ct = default) => inner.ReadAsync(ct);
        public Task<GrantRevokePreview> PrepareRevokeAsync(Guid id, long generation, CancellationToken ct = default) => inner.PrepareRevokeAsync(id, generation, ct);
        public Task<GrantRevokeReceipt> ConfirmRevokeAsync(Guid id, long generation, string digest, CancellationToken ct = default) => inner.ConfirmRevokeAsync(id, generation, digest, ct);
        public Task<GrantIssueResult> ConfirmIssueAsync(Guid id, long generation, string digest, CancellationToken ct = default) => inner.ConfirmIssueAsync(id, generation, digest, ct);
        public async Task<GrantPrepareResult> PrepareIssueAsync(JsonElement document, CancellationToken ct = default)
        {
            try
            {
                var request = new FleetGrantBindingRequest(Receiver, "default", Guid.NewGuid());
                var binding = await wire.BindAsync(request, ct);
                var point = Codec.FromBase64Url(binding.Binding.HostSign, "HostSign");
                FleetGrantBindingProof.Verify(binding, point, new(binding.Binding.Account, "default", Receiver, "grant.binding", request.Request));
                var context = new FleetGrantProofContext(binding.Binding.Account, "default", Receiver, "grant.prepareIssue", Guid.NewGuid());
                var proof = await wire.CallAsync(new(context, JsonSerializer.SerializeToElement(new { document })), ct);
                var prepared = JsonSerializer.Deserialize<FleetGrantPrepared>(FleetGrantProof.Verify(proof, point, context), ProtocolJson.Options)!;
                if (prepared.Challenge.Action != "grant.issue") { throw new InvalidDataException("test challenge"); }
                var type = typeof(GrantWireReceiver).GetNestedType("PreparedGrant", BindingFlags.NonPublic)!;
                var state = Activator.CreateInstance(type, prepared.Challenge, binding.Binding);
                typeof(GrantWireReceiver).GetField("issue", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(inner, state);
                return new(prepared.Preview.Deserialize<GrantIssuePreview>(ProtocolJson.Options), null);
            }
            catch (InvalidDataException) { return new(null, "test preparation refused"); }
        }
    }

    sealed class Dispatcher : IGrantWireDispatcher, IDisposable
    {
        readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        readonly P256KeyPair target = P256KeyPair.Generate();
        readonly string change;
        readonly Guid id = Guid.NewGuid();
        string Document => JsonSerializer.Serialize(new { id, account = Binding.Account, generation = 1,
            audience = new { device = Binding.Receiver, profile = "default" }, caps = new { hourly = 1, daily = 2, live = 1 } });
        public List<FleetGrantCall> Calls { get; } = [];
        public bool Online => true;
        public FleetGrantBinding Binding { get; }
        public Dispatcher(string change)
        {
            this.change = change;
            var point = key.ExportParameters(false).Q;
            Binding = new("account", "default", Wire.DeviceId(target.Public), new('a', 32),
                Codec.Base64Url([4, .. point.X!, .. point.Y!]), Codec.Base64Url(target.Public));
        }
        public Task<FleetGrantBindingResult> BindAsync(FleetGrantBindingRequest request, CancellationToken ct)
        {
            var latest = change is "binding" or "read-binding" && Calls.Count > 0 ? Binding with { RosterGeneration = new('b', 32) } : Binding;
            var reply = FleetGrantBindingProof.Seal(latest,
                new(latest.Account, request.Profile, request.Receiver, "grant.binding", request.Request), new EcdsaEnvelopeSigner(key));
            if (change == "binding-proof") { reply = reply with { Proof = reply.Proof with { Body = "e30" } }; }
            return Task.FromResult(reply);
        }
        public Task<FleetGrantSignedPayload> CallAsync(FleetGrantCall call, CancellationToken ct)
        {
            Calls.Add(call);
            var document = Document;
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
            object payload = call.Context.Action switch
            {
                "grant.prepareIssue" => new FleetGrantPrepared(new(call.Context.Request, id, 1, Binding.Account,
                    "default", Binding.Receiver, change == "challenge" ? "grant.revoke" : "grant.issue", digest,
                    DateTimeOffset.UtcNow.AddMinutes(1)), JsonSerializer.SerializeToElement(
                        new GrantIssuePreview(id, 1, Binding.Receiver, "default", document, digest, new(1, 2, 1), "ask", []), ProtocolJson.Options)),
                _ => throw new InvalidOperationException("Unexpected test operation")
            };
            var proof = FleetGrantProof.Seal(JsonSerializer.SerializeToUtf8Bytes(payload, ProtocolJson.Options),
                call.Context, new EcdsaEnvelopeSigner(key));
            if (change == "proof") { proof = proof with { Body = Codec.Base64Url(Encoding.UTF8.GetBytes("{}")) }; }
            return Task.FromResult(proof);
        }
        public Task<FleetGrantIssueReceiptResult> ConfirmIssueAsync(FleetGrantCall call, CancellationToken ct)
        {
            Calls.Add(call);
            if (change == "rotation-refused") { throw new HostErrorException("RotationRequired"); }
            var document = Document;
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
            var receipt = new GrantReceipt(id, 1, Binding.Receiver, change == "receipt" ? new('0', 64) : digest, change == "rotated");
            var result = FleetGrantReceiptProof.Seal(JsonSerializer.SerializeToElement(receipt, ProtocolJson.Options),
                call.Context, new EcdsaEnvelopeSigner(key));
            if (change == "missing-rotated") { result = result with { Receipt = JsonSerializer.SerializeToElement(new { id,
                generation = 1, receiver = Binding.Receiver, digest }) }; }
            if (change == "rotated-type") { result = result with { Receipt = JsonSerializer.SerializeToElement(new { id,
                generation = 1, receiver = Binding.Receiver, digest, rotated = "false" }) }; }
            if (change == "receipt-proof") { result = result with { Proof = result.Proof with { Body = "e30" } }; }
            return Task.FromResult(result);
        }
        public Task<FleetGrantReadResult> ReadProblemAsync(FleetGrantCall call, CancellationToken ct)
        {
            Calls.Add(call);
            var problem = change == "none" ? FleetGrantReadProblem.None : change == "other" ? FleetGrantReadProblem.Other
                : change == "unavailable" ? FleetGrantReadProblem.Unavailable : FleetGrantReadProblem.RotationRequired;
            var context = change == "read-context" ? call.Context with { Request = Guid.NewGuid() } : call.Context;
            var result = FleetGrantReadProof.Seal(FleetGrantReadProof.Reply(problem), context, new EcdsaEnvelopeSigner(key));
            if (change == "read-proof") { result = result with { Proof = result.Proof with { Body = "e30" } }; }
            return Task.FromResult(result);
        }
        public void Dispose() { key.Dispose(); target.Dispose(); }
    }
}
