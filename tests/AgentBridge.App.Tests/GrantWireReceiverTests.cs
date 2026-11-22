using System.Security.Cryptography;
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

public sealed class GrantWireReceiverTests
{
    [Theory]
    [InlineData("proof")]
    [InlineData("challenge")]
    [InlineData("receipt")]
    [InlineData("binding")]
    [InlineData("binding-proof")]
    [InlineData("missing-rotated")]
    [InlineData("rotated")]
    [InlineData("none")]
    public async Task Only_a_matching_verified_preparation_and_receipt_can_become_active(string change)
    {
        using var wire = new Dispatcher(change);
        using var app = P256KeyPair.Generate();
        var signed = 0;
        var receiver = new GrantWireReceiver(wire, wire.Binding.Receiver, "default", (context, args, receiverKey) =>
        {
            signed++;
            return Transfers.SignGrantConfirmation(app, receiverKey, context, args, DateTimeOffset.UtcNow);
        });
        var model = new GrantIssueViewModel(receiver);
        await model.PrepareAsync(JsonSerializer.SerializeToElement(new { }));
        Assert.Equal(0, signed);
        if (change is "proof" or "challenge" or "binding-proof") { Assert.False(model.CanConfirm); return; }
        await model.ConfirmAsync("not-the-reviewed-digest");
        Assert.Equal(0, signed);
        await model.ConfirmAsync(model.Digest);
        Assert.Equal(change == "binding" ? 0 : 1, signed);
        Assert.Equal(change is "none" or "rotated", model.Active);
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

    [Fact]
    public async Task A_changed_page_generation_is_refused_without_an_implicit_retry()
    {
        using var wire = new Dispatcher("pages");
        var receiver = new GrantWireReceiver(wire, wire.Binding.Receiver, "default", (_, _, _) => null);
        var result = await receiver.ReadAsync();
        Assert.NotNull(result.Problem);
        Assert.Empty(result.Grants);
        Assert.Equal(2, wire.Calls.Count);
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
            var latest = change == "binding" && Calls.Count > 0 ? Binding with { RosterGeneration = new('b', 32) } : Binding;
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
                "grant.read" => new FleetGrantReadPage(Calls.Count, DateTimeOffset.UtcNow,
                    JsonSerializer.SerializeToElement(new[] { new GrantSnapshot(JsonSerializer.Deserialize<JsonElement>(document),
                        new(0, 0, 0, 0, [], null, null, DateTimeOffset.UtcNow, [], []), false) }, ProtocolJson.Options),
                    Calls.Count == 1 ? 1 : null),
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
            var document = Document;
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));
            var receipt = new GrantReceipt(id, 1, Binding.Receiver, change == "receipt" ? new('0', 64) : digest, change == "rotated");
            var result = FleetGrantReceiptProof.Seal(JsonSerializer.SerializeToElement(receipt, ProtocolJson.Options),
                call.Context, new EcdsaEnvelopeSigner(key));
            if (change == "missing-rotated") { result = result with { Receipt = JsonSerializer.SerializeToElement(new { id,
                generation = 1, receiver = Binding.Receiver, digest }) }; }
            return Task.FromResult(result);
        }
        public void Dispose() { key.Dispose(); target.Dispose(); }
    }
}
