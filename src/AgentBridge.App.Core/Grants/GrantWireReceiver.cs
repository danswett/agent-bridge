using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.AgentEnvelope;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Roster;
using AgentBridge.Transport;

namespace AgentBridge.App.Core.Grants;

public interface IGrantWireDispatcher
{
    bool Online { get; }
    // The production implementation authenticates metadata on the trusted local app pipe.
    Task<FleetGrantBindingResult> BindAsync(FleetGrantBindingRequest request, CancellationToken ct);
    Task<FleetGrantSignedPayload> CallAsync(FleetGrantCall call, CancellationToken ct);
    Task<FleetGrantIssueReceiptResult> ConfirmIssueAsync(FleetGrantCall call, CancellationToken ct);
}

public sealed partial class GrantWireReceiver(IGrantWireDispatcher dispatcher, string receiver, string profile,
    Func<IGrantConfirmationSigner?> signer) : IGrantIssueReceiver
{
    static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, MaxDepth = 12
    };
    sealed record PreparedGrant(FleetGrantChallenge Challenge, FleetGrantBinding Binding);
    PreparedGrant? issue;
    public string Receiver => receiver;
    public bool Online => dispatcher.Online;
    public string? Problem => Online ? null : "target-offline";

    public Task<GrantReadResult> ReadAsync(CancellationToken ct = default) =>
        Task.FromResult(new GrantReadResult([], "unavailable"));

    public async Task<GrantPrepareResult> PrepareIssueAsync(JsonElement document, CancellationToken ct = default)
    {
        issue = null;
        try
        {
            var (context, bound, _) = await ContextAsync("grant.prepareIssue", ct);
            var prepared = Parse<FleetGrantPrepared>(await BytesAsync(context, bound, new { document }, null, ct));
            var preview = Parse<GrantIssuePreview>(Encoding.UTF8.GetBytes(prepared.Preview.GetRawText()));
            CheckChallenge(prepared.Challenge, context, "grant.issue", preview.Id, preview.Generation,
                preview.Receiver, preview.Profile, preview.Digest, preview.Document);
            if (!BoundDocument(JsonSerializer.Deserialize<JsonElement>(preview.Document), context.Account) || preview.UnavailableActions.IsDefault)
            { throw new InvalidDataException("grant-preview"); }
            if (preview.UnavailableActions.Length > 0) { return new(null, "action-unavailable"); }
            issue = new(prepared.Challenge, bound);
            return new(preview, null);
        }
        catch (Exception error) when (Handled(error))
        { return new(null, Reason(error)); }
    }

    public async Task<GrantIssueResult> ConfirmIssueAsync(Guid id, long generation, string digest, CancellationToken ct = default)
    {
        var challenge = issue;
        issue = null;
        try
        {
            var (context, bound, call) = await PersonCallAsync("grant.confirmIssue", challenge, id, generation, digest, ct);
            var result = await dispatcher.ConfirmIssueAsync(call, ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            var verified = FleetGrantReceiptProof.Verify(result, Codec.FromBase64Url(bound.HostSign, "HostSign"), context);
            var receipt = verified.Deserialize<GrantReceipt>(Strict) ?? throw new InvalidDataException("grant-receipt");
            if (receipt.Id != id || receipt.Generation != generation || receipt.Receiver != receiver || receipt.Digest != digest)
            { throw new InvalidDataException("grant-receipt"); }
            return new(receipt, null);
        }
        catch (HostErrorException error) { return new(null, error.Message); }
        catch (Exception error) when (Handled(error)) { throw new IOException(Reason(error), error); }
    }

    public Task<GrantRevokePreview> PrepareRevokeAsync(Guid id, long generation, CancellationToken ct = default) =>
        Task.FromException<GrantRevokePreview>(new IOException("unavailable"));

    public Task<GrantRevokeReceipt> ConfirmRevokeAsync(Guid id, long generation, string digest, CancellationToken ct = default) =>
        Task.FromException<GrantRevokeReceipt>(new IOException("unavailable"));

    async Task<(FleetGrantProofContext Context, FleetGrantBinding Binding, FleetGrantCall Call)> PersonCallAsync(
        string operation, PreparedGrant? prepared, Guid id, long generation,
        string digest, CancellationToken ct)
    {
        var challenge = prepared?.Challenge;
        if (challenge is null || challenge.Id != id || challenge.Generation != generation || challenge.Digest != digest
            || challenge.ExpiresAt <= DateTimeOffset.UtcNow)
        { throw new InvalidDataException("grant-person-confirmation"); }
        var (context, bound, receiverKey) = await ContextAsync(operation, ct, prepared!.Binding);
        var args = new FleetGrantConfirmation(challenge.Request, id, generation, digest);
        var command = signer()?.SignGrantConfirmation(receiverKey, context, args, DateTimeOffset.UtcNow)
            ?? throw new UnauthorizedAccessException("person-signing-unavailable");
        var body = Parse<CommandBody>(BlobFormat.FromBase64Url(command.Body)
            ?? throw new InvalidDataException("grant-command"));
        if (body.Action != operation || body.Request != context.Request.ToString("D") || body.To != receiver
            || body.Principal is not { Kind: "user" } || body.Target is not { Session: null } target
            || target.Machine != receiver || body.Args is not { } signedArgs
            || !JsonElement.DeepEquals(signedArgs, JsonSerializer.SerializeToElement(new { context, args }, ProtocolJson.Options)))
        { throw new InvalidDataException("grant-command-binding"); }
        var (latest, _) = await VerifyBindingAsync(ct);
        if (latest != bound) { throw new InvalidDataException("grant-binding-changed"); }
        return (context, bound, new(context, JsonSerializer.SerializeToElement(args, ProtocolJson.Options),
            JsonSerializer.SerializeToElement(command, ProtocolJson.Options)));
    }

    async Task<(FleetGrantProofContext Context, FleetGrantBinding Binding, byte[] ReceiverKey)> ContextAsync(string operation,
        CancellationToken ct, FleetGrantBinding? expected = null)
    {
        var (bound, receiverKey) = await VerifyBindingAsync(ct);
        if (expected is not null && expected != bound) { throw new InvalidDataException("grant-binding-changed"); }
        return (new(bound.Account, profile, receiver, operation, Guid.NewGuid()), bound, receiverKey);
    }

    async Task<(FleetGrantBinding Binding, byte[] ReceiverKey)> VerifyBindingAsync(CancellationToken ct)
    {
        if (!Online) { throw new IOException("target-offline"); }
        var request = new FleetGrantBindingRequest(receiver, profile, Guid.NewGuid());
        var reply = await dispatcher.BindAsync(request, ct);
        var latest = reply?.Binding ?? throw new InvalidDataException("grant-binding-unavailable");
        // The trusted pipe authenticates this metadata; the seal protects the receiver-key bytes.
        var receiverKey = FleetGrantBindingProof.Verify(reply!, Codec.FromBase64Url(latest.HostSign, "HostSign"),
            new(latest.Account, profile, receiver, "grant.binding", request.Request));
        return (latest, receiverKey);
    }

    async Task<byte[]> BytesAsync(FleetGrantProofContext context, FleetGrantBinding bound, object args,
        JsonElement? command, CancellationToken ct)
    {
        var (latest, _) = await VerifyBindingAsync(ct);
        if (latest != bound) { throw new InvalidDataException("grant-binding-changed"); }
        var proof = await dispatcher.CallAsync(new(context, JsonSerializer.SerializeToElement(args, ProtocolJson.Options), command), ct);
        return FleetGrantProof.Verify(proof, Codec.FromBase64Url(bound.HostSign, "HostSign"), context);
    }

    void CheckChallenge(FleetGrantChallenge challenge, FleetGrantProofContext context, string action, Guid id,
        long generation, string machine, string targetProfile, string digest, string document)
    {
        if (challenge is null || challenge.Request != context.Request || challenge.Id != id || id == Guid.Empty
            || challenge.Generation != generation || generation <= 0 || challenge.Account != context.Account
            || challenge.Profile != profile || targetProfile != profile || challenge.Receiver != receiver || machine != receiver
            || challenge.Action != action || challenge.Digest != digest || challenge.ExpiresAt <= DateTimeOffset.UtcNow
            || document is not { Length: > 0 and <= 64 * 1024 }
            || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document))) != digest)
        { throw new InvalidDataException("grant-challenge"); }
    }

    bool BoundDocument(JsonElement document, string expectedAccount) => document.ValueKind == JsonValueKind.Object
        && document.TryGetProperty("account", out var account) && account.GetString() == expectedAccount
        && document.TryGetProperty("audience", out var audience) && audience.ValueKind == JsonValueKind.Object
        && audience.TryGetProperty("device", out var machine) && machine.GetString() == receiver
        && audience.TryGetProperty("profile", out var targetProfile) && targetProfile.GetString() == profile;

    internal static T Parse<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 12 });
        if (!Unique(document.RootElement)) { throw new InvalidDataException("grant-schema"); }
        return JsonSerializer.Deserialize<T>(bytes, Strict) ?? throw new InvalidDataException("grant-schema");
    }
    static bool Unique(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.EnumerateArray().All(Unique),
        JsonValueKind.Object => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count()
            == value.GetPropertyCount() && value.EnumerateObject().All(p => Unique(p.Value)),
        _ => true
    };
    static bool Handled(Exception error) => error is IOException or InvalidDataException or JsonException or CryptographicException
        or HostErrorException or TimeoutException or OperationCanceledException or HttpRequestException or UnauthorizedAccessException
        or InvalidOperationException or FormatException or KeyNotFoundException;
    static string Reason(Exception error) => error is HostErrorException ? error.Message
        : error is InvalidDataException or JsonException or CryptographicException ? "receiver-verification-failed" : "unavailable";
}
