using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Launch;

// Brings a session's bundle from the machine it is on (docs/host-transfer.md): the target's
// half of a transfer, which the host implements over its relay connection.
public interface ISessionFetcher
{
    // Null when this host can bring sessions in now; otherwise why not, as a refusal says it.
    string? Unavailable { get; }

    // Asks the machine the transfer names for the bundle, on behalf of peer (the device that
    // asked this host to launch it, and signed the command), and fetches it into this host's
    // private staging. Throws TransferRefusedException, with the reason, when it cannot.
    Task<FetchedBundle> FetchAsync(LaunchTransfer transfer, string peer, long maxBytes, CancellationToken cancellationToken);
}

// A fetched bundle, checked against its SHA-256: deleted with Dispose, whatever became of it.
public sealed class FetchedBundle(string zip, TransferResult about, string from) : IDisposable
{
    public string Zip => zip;
    public TransferResult About => about;
    public string From => from;

    public void Dispose()
    {
        try { File.Delete(zip); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class TransferRefusedException(string reason) : Exception(reason);
