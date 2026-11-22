using System.Text.Json;
using AgentBridge.App.Core.Grants;

namespace AgentBridge.App.Tests;

sealed class FakeGrantReceiver : IGrantIssueReceiver
{
    public string Receiver { get; set; } = new('1', 32);
    public bool Online { get; set; } = true;
    public string? Problem { get; set; }
    public GrantPrepareResult Prepared { get; set; } = new(null, "unavailable");
    public GrantIssueResult Issued { get; set; } = new(null, "unavailable");
    public TaskCompletionSource<GrantIssueResult>? Pending { get; set; }
    public string? ConfirmedDigest { get; private set; }
    public int Confirmations { get; private set; }
    public Task<GrantReadResult> ReadAsync(CancellationToken ct = default) => Task.FromResult(new GrantReadResult([], Problem));
    public Task<GrantPrepareResult> PrepareIssueAsync(JsonElement document, CancellationToken ct = default) => Task.FromResult(Prepared);
    public Task<GrantIssueResult> ConfirmIssueAsync(Guid id, long generation, string digest, CancellationToken ct = default)
    {
        Confirmations++;
        ConfirmedDigest = digest;
        return Pending?.Task ?? Task.FromResult(Issued);
    }
    public Task<GrantRevokePreview> PrepareRevokeAsync(Guid id, long generation, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<GrantRevokeReceipt> ConfirmRevokeAsync(Guid id, long generation, string digest, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
