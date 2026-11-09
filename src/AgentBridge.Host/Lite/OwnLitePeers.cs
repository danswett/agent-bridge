using AgentBridge.Transport;

namespace AgentBridge.Host.Lite;

// The receiver's view of the pinned own-peers (L2b). Empty when the mode is off, the file is unsigned or
// damaged, or the peer is of another account; every call re-reads the file.
public sealed class OwnLitePeers(OwnPeers store) : ILitePeers
{
    public bool TryFind(string deviceId, out LitePeer peer)
    {
        peer = All().FirstOrDefault(p => p.DeviceId == deviceId)!;
        return peer is not null;
    }

    // A failing settings or key read must mean "no peers", never an exception into the receiver.
    public IReadOnlyList<LitePeer> All()
    {
        try { return [.. store.List().Select(p => new LitePeer(p.DeviceId, BlobFormat.FromBase64Url(p.PublicKey)!, p.Name))]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return [];
        }
    }
}
