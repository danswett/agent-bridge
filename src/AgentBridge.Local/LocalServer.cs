using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Local;

public sealed class EndpointInUseException(string message) : IOException(message);

// The host's end of the local API: accepts the apps on this machine, checks who each is,
// and serves their requests through the same handler the relay's channels use.
public sealed class LocalServer : IAsyncDisposable
{
    public const string RefusedEvent = "local.refused";

    readonly LocalEndpoint endpoint;
    readonly Func<IProtocolChannel, string, JsonElement, Task<object?>> handler;
    readonly Action<string> log;
    readonly JsonSerializerOptions json;
    readonly CancellationTokenSource stop = new();
    readonly HashSet<LocalChannel> channels = [];
    readonly object gate = new();
    Task loop = Task.CompletedTask;
    Socket? listener;
    int unixPeers;

    public LocalEndpoint Endpoint => endpoint;
    public int Connections { get { lock (gate) { return channels.Count; } } }
    public event Action<LocalChannel>? Accepted;
    public event Action<string>? Refused;
    // Tests decide who is refused; the real policy is PeerPolicy's.
    internal Func<PipePeer, string?>? Check { get; set; }

    LocalServer(LocalEndpoint endpoint, Func<IProtocolChannel, string, JsonElement, Task<object?>> handler, Action<string>? log, JsonSerializerOptions? json)
    {
        this.endpoint = endpoint;
        this.handler = handler;
        this.log = log ?? (_ => { });
        this.json = json ?? ProtocolJson.Options;
    }

    // Starts listening, or throws EndpointInUseException when another process already serves
    // this endpoint: a second host for the same profile, or anything squatting on its name.
    public static LocalServer Start(LocalEndpoint endpoint, Func<IProtocolChannel, string, JsonElement, Task<object?>> handler,
        Action<string>? log = null, JsonSerializerOptions? json = null)
    {
        var server = new LocalServer(endpoint, handler, log, json);
        if (OperatingSystem.IsWindows())
        {
            server.StartPipe();
        }
        else
        {
            server.listener = server.Bind();
            server.loop = Task.Run(server.SocketLoopAsync);
        }
        return server;
    }

    [SupportedOSPlatform("windows")]
    void StartPipe()
    {
        var first = CreatePipe(first: true);
        loop = Task.Run(() => PipeLoopAsync(first));
    }

    [SupportedOSPlatform("windows")]
    NamedPipeServerStream CreatePipe(bool first)
    {
        var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var security = new PipeSecurity();
        // Owned by the token's default owner, which is what a CurrentUserOnly client compares
        // with its own: the user, or Administrators in an elevated administrator's token. So an
        // app and a host at the same elevation agree, and a mismatch fails at the client before
        // the host's own elevation check. Only this user, never over the network.
        security.SetOwner(identity.Owner ?? user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        try
        {
            return NamedPipeServerStreamAcl.Create(endpoint.Address, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 0, 0, security);
        }
        catch (Exception e) when (first && e is IOException or UnauthorizedAccessException)
        {
            throw new EndpointInUseException($"another process is already listening on \\\\.\\pipe\\{endpoint.Address}: a host for this profile is running");
        }
    }

    [SupportedOSPlatform("windows")]
    async Task PipeLoopAsync(NamedPipeServerStream next)
    {
        while (!stop.IsCancellationRequested)
        {
            var pipe = next;
            try { await pipe.WaitForConnectionAsync(stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { await pipe.DisposeAsync(); return; }
            catch (IOException e)
            {
                // A client that connected and left before it was accepted; listen again.
                log($"local pipe: {e.Message}");
                await pipe.DisposeAsync();
                next = CreatePipe(first: false);
                continue;
            }
            // The next instance before this one is served, so the name is never without a
            // listener while the host runs, and nobody else can take it in between.
            try { next = CreatePipe(first: false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"local pipe: could not listen again: {e.Message}");
                await pipe.DisposeAsync();
                return;
            }
            var peer = WindowsPeers.Of(pipe);
            var refusal = Check?.Invoke(peer) ?? PeerPolicy.Refusal(peer, WindowsPeers.CurrentUser(), WindowsPeers.CurrentElevated());
            if (refusal is not null)
            {
                _ = RefuseAsync(pipe, refusal, $"process {peer.ProcessId} ({peer.ProcessName ?? "?"})");
                continue;
            }
            Serve(pipe, $"local-{peer.ProcessId}", peer.ProcessName ?? "app", peer);
        }
        await next.DisposeAsync();
    }

    Socket Bind()
    {
        var path = endpoint.Address;
        if (File.Exists(path))
        {
            // A socket file left by a host that is still listening is in use; one left by a
            // host that died is not, and is replaced.
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                probe.Connect(new UnixDomainSocketEndPoint(path));
                throw new EndpointInUseException($"another process is already listening on {path}: a host for this profile is running");
            }
            catch (SocketException) { File.Delete(path); }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        // The profile folder is the user's alone (0700); the socket is too.
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        socket.Listen(16);
        return socket;
    }

    async Task SocketLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener!.AcceptAsync(stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            // The socket is in the user's own 0700 folder, so only the user reaches it.
            var stream = new NetworkStream(client, ownsSocket: true);
            if (Check?.Invoke(new PipePeer(0, null, null, null)) is { } refusal) { _ = RefuseAsync(stream, refusal, "a socket client"); continue; }
            Serve(stream, $"local-u{Interlocked.Increment(ref unixPeers)}", "app", new PipePeer(0, null, null, null));
        }
    }

    void Serve(Stream stream, string peerId, string name, PipePeer peer)
    {
        var channel = new LocalChannel(stream, peerId, name, "local", handler, json) { Peer = peer };
        lock (gate) { channels.Add(channel); }
        _ = channel.Closed.ContinueWith(_ => { lock (gate) { channels.Remove(channel); } }, TaskScheduler.Default);
        channel.Start();
        Accepted?.Invoke(channel);
    }

    // Tells the process why, so the app can say it: the reason as an event first, then as the
    // error of whatever it asks (its hello), then the pipe closes.
    async Task RefuseAsync(Stream stream, string reason, string who)
    {
        log($"local pipe: refused {who}: {reason}");
        Refused?.Invoke(reason);
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new LocalChannel(stream, "refused", "", "local", (_, _, _) =>
        {
            answered.TrySetResult();
            throw new InvalidOperationException(reason);
        }, json);
        try
        {
            await channel.SendEventAsync(RefusedEvent, new { reason }).WaitAsync(TimeSpan.FromSeconds(2));
            channel.Start();
            await answered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The error goes out after the handler throws; give it a moment to be written.
            await Task.Delay(200);
        }
        catch (Exception) { }
        await channel.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener?.Dispose();
        try { await loop; } catch (Exception) { }
        LocalChannel[] open;
        lock (gate) { open = [.. channels]; }
        foreach (var c in open) { await c.DisposeAsync(); }
        if (!OperatingSystem.IsWindows())
        {
            try { File.Delete(endpoint.Address); } catch (IOException) { }
        }
    }
}
