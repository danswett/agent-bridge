using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Local;

// The host refused this process (another user, or a different elevation), and said why.
public sealed class LocalRefusedException(string reason) : IOException(reason);

// No host is listening for this profile: it is not running, or is still starting.
public sealed class HostNotRunningException(string message, Exception? inner = null) : IOException(message, inner);

// The app's end of the local API.
public static class LocalClient
{
    // Connects and checks the host took this process: a refused one hears why in the first
    // event, before any response, and gets LocalRefusedException.
    public static async Task<LocalChannel> ConnectAsync(LocalEndpoint endpoint, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(2);
        Stream stream;
        if (OperatingSystem.IsWindows())
        {
            // CurrentUserOnly: the connection fails unless the pipe is owned by this user, so a
            // pipe another user created under the name is never talked to.
            var pipe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(wait);
                await pipe.ConnectAsync(cts.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException && !ct.IsCancellationRequested)
            {
                await pipe.DisposeAsync();
                throw new HostNotRunningException("the Bridge Host is not running for this profile", e);
            }
            catch (UnauthorizedAccessException e)
            {
                await pipe.DisposeAsync();
                throw new LocalRefusedException($"the host's pipe is not this user's ({e.Message})");
            }
            stream = pipe;
        }
        else
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), ct).AsTask().WaitAsync(wait, ct); }
            catch (Exception e) when (e is SocketException or TimeoutException)
            {
                socket.Dispose();
                throw new HostNotRunningException("the Bridge Host is not running for this profile", e);
            }
            stream = new NetworkStream(socket, ownsSocket: true);
        }
        var channel = new LocalChannel(stream, "host", "host", "host", null, ProtocolJson.Options);
        channel.Start();
        return channel;
    }

    // Sends a request and returns its result, or throws HostError with the host's reason.
    public static async Task<JsonElement> CallAsync(this LocalChannel channel, string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var response = await channel.RequestAsync(op, data ?? new { }, timeout, ct);
        if (response.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
        {
            return response.TryGetProperty("result", out var result) ? result : default;
        }
        throw new HostErrorException(response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString()! : "the host failed the request");
    }

    public static async Task<T> CallAsync<T>(this LocalChannel channel, string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var result = await channel.CallAsync(op, data, timeout, ct);
        return result.Deserialize<T>(ProtocolJson.Options) ?? throw new HostErrorException($"{op} returned nothing");
    }

    // hello, the first request on every connection. A host that refused this process closes
    // the pipe after saying why; that reason is what the caller gets.
    public static async Task<HelloResult> HelloAsync(this LocalChannel channel, CancellationToken ct = default)
    {
        try { return await channel.CallAsync<HelloResult>(Ops.Hello, ct: ct); }
        catch (Exception e) when (e is IOException or HostErrorException && Refusal(channel) is { } reason) { throw new LocalRefusedException(reason); }
    }

    static string? Refusal(LocalChannel channel)
    {
        while (channel.Events.TryRead(out var e))
        {
            if (e.Name == LocalServer.RefusedEvent && e.Data.ValueKind == JsonValueKind.Object && e.Data.TryGetProperty("reason", out var r)) { return r.GetString(); }
        }
        return null;
    }
}

// The host answered a request with ok: false. Not a lost response: the request was received
// and turned down, so resending it changes nothing.
public sealed class HostErrorException(string reason) : Exception(reason);
