using System.Security.Cryptography;
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
    Task<FleetGrantReadResult> ReadProblemAsync(FleetGrantCall call, CancellationToken ct);
}

public sealed partial class GrantWireReceiver(IGrantWireDispatcher dispatcher, string receiver, string profile,
    Func<IGrantConfirmationSigner?> signer) : IGrantIssueReceiver, IVerifiedGrantReader
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
        ReadPagesAsync(null, ct);

    Task<GrantReadResult> ReadPagesAsync(FleetGrantBinding? expected, CancellationToken ct) =>
        Task.FromResult(new GrantReadResult([], "unavailable"));

    async Task<VerifiedGrantRead> IVerifiedGrantReader.ReadAsync(CancellationToken ct)
    {
        try
        {
            var (context, bound, _) = await ContextAsync("grant.read", ct);
            var call = new FleetGrantCall(context, JsonSerializer.SerializeToElement(new FleetGrantReadRequest(), ProtocolJson.Options));
            var reply = await dispatcher.ReadProblemAsync(call, ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            var problem = FleetGrantReadProof.Verify(reply, Codec.FromBase64Url(bound.HostSign, "HostSign"), context);
            var (latest, _) = await VerifyBindingAsync(ct);
            if (latest != bound) { throw new InvalidDataException("grant-binding-changed"); }
            var ready = problem is FleetGrantReadProblem.None or FleetGrantReadProblem.RotationRequired && signer() is not null;
            var data = problem == FleetGrantReadProblem.None ? await ReadPagesAsync(bound, ct)
                : new GrantReadResult([], problem == FleetGrantReadProblem.RotationRequired ? "RotationRequired" : "unavailable");
            return new(problem, ready, data);
        }
        catch (Exception error) when (Handled(error))
        { return new(FleetGrantReadProblem.Unavailable, false, new([], Reason(error))); }
    }

    public Task<GrantPrepareResult> PrepareIssueAsync(JsonElement document, CancellationToken ct = default)
    {
        issue = null;
        return Task.FromResult(new GrantPrepareResult(null, "unavailable"));
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
        catch (HostErrorException error)
        { return new(null, error.Message == "RotationRequired"
            ? "Grant refused: this machine's audit log needs rotation and could not be rotated automatically (RotationRequired). Nothing was issued."
            : error.Message); }
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
