using System.Net.WebSockets;

namespace AgentBridge.Relay;

// What the disconnect log line says about why a socket ended (#970), so a reset, a clean close
// and a missed pong no longer look alike. Only transport facts: no identity, account or token
// (privacy §5.10), and no exception message, which can carry a peer address.
public static class DisconnectCause
{
    public static string Describe(Exception? ended, WebSocketCloseStatus? closeStatus)
    {
        var type = ended?.GetType().Name ?? "none";
        var error = ended is WebSocketException w ? w.WebSocketErrorCode.ToString() : "none";
        var close = closeStatus is { } s ? ((int)s).ToString() : "none";
        return $"exception={type} wserror={error} close={close} keepalive={(IsKeepAliveTimeout(ended) ? "timeout" : "no")}";
    }

    // The runtime reports a missed pong only in its message (SR.net_Websockets_KeepAlivePingTimeout),
    // with no distinct error code, so this matches that text; a runtime that words it differently
    // reads as "no" and the cause is then told apart by close=none with wserror.
    static bool IsKeepAliveTimeout(Exception? e) =>
        e is WebSocketException { Message: var m } && m.Contains("KeepAliveTimeout", StringComparison.Ordinal);
}
