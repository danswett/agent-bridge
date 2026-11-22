using System.Text;
using System.Text.Json;
using AgentBridge.AgentEnvelope;

namespace AgentBridge.App.Core.Grants;

public sealed class LocalGrantWireDispatcher(HostConnection connection, string receiver, string profile) : IGrantWireDispatcher
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public bool Online => connection.State == LinkState.Connected && connection.Hello?.Machine.Id == receiver;
    public Task<FleetGrantBindingResult> BindAsync(FleetGrantBindingRequest request, CancellationToken ct) =>
        request.Receiver == receiver && request.Profile == profile && request.Request != Guid.Empty
            ? SendAsync<FleetGrantBindingResult>("grant.binding", request, ct)
            : Task.FromException<FleetGrantBindingResult>(new InvalidDataException("grant-binding-request"));
    public Task<FleetGrantSignedPayload> CallAsync(FleetGrantCall call, CancellationToken ct) =>
        Task.FromException<FleetGrantSignedPayload>(new IOException("unavailable"));
    public Task<FleetGrantReadResult> ReadProblemAsync(FleetGrantCall call, CancellationToken ct) =>
        SendAsync<FleetGrantReadResult>("grant.read", Checked(call, "grant.read", false), ct);
    public Task<FleetGrantIssueReceiptResult> ConfirmIssueAsync(FleetGrantCall call, CancellationToken ct) =>
        SendAsync<FleetGrantIssueReceiptResult>("grant.confirmIssue", Checked(call, "grant.confirmIssue", true), ct);

    FleetGrantCall Checked(FleetGrantCall call, string action, bool person)
    {
        if (call?.Context is not { } context || context.Action != action || context.Profile != profile
            || context.Receiver != receiver || context.Request == Guid.Empty
            || call.Args.ValueKind != JsonValueKind.Object
            || (person ? call.Command?.ValueKind != JsonValueKind.Object : call.Command is not null))
        { throw new InvalidDataException("grant-call-context"); }
        return call;
    }

    async Task<T> SendAsync<T>(string operation, object data, CancellationToken ct)
    {
        if (!Online) { throw new IOException("target-offline"); }
        var host = connection.HostInstance;
        var number = connection.ConnectionNumber;
        JsonElement reply;
        try
        {
            reply = await connection.CallAsync(operation, data, Timeout, knownHost: true, onlyTo: host, out _, ct)
                .WaitAsync(Timeout, ct);
        }
        catch (HostRestartedException error) { throw new IOException("target-restarted", error); }
        if (!Online || connection.HostInstance != host || connection.ConnectionNumber != number)
        { throw new IOException("target-restarted"); }
        var bytes = Encoding.UTF8.GetBytes(reply.GetRawText());
        if (bytes.Length > 128 * 1024) { throw new InvalidDataException("grant-schema"); }
        return GrantWireReceiver.Parse<T>(bytes);
    }
}
