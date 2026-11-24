using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host.Mcp;

// What went wrong, in words an agent can act on (mcp/src/pipe.mjs PipeError): the host is not there, the host said no, or something else.
public enum McpFailure { Unavailable, Refused, Error }

public sealed class McpToolException(string message, McpFailure kind = McpFailure.Error) : Exception(message)
{
    public McpFailure Kind { get; } = kind;
}

// The MCP server's one connection to the host's local pipe, with the authority of the agent session that started it and nothing more:
// it says hello and, with that session's own AGENT_BRIDGE_TOKEN, agent.hello. It never uses the app's or a person's authority; the host
// classes it by ancestry as an agent, and refuses the app-only ops. It holds no relay credential and writes nothing to disk.
public sealed class McpHost : IAsyncDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    readonly LocalChannel channel;
    readonly TimeSpan timeout;
    readonly HashSet<string> capabilities;

    McpHost(LocalChannel channel, HelloResult hello, string? principal, TimeSpan timeout)
    {
        this.channel = channel;
        this.timeout = timeout;
        Hello = hello;
        Principal = principal;
        capabilities = new HashSet<string>(hello.Capabilities, StringComparer.Ordinal);
    }

    public HelloResult Hello { get; }
    // The agent principal the host accepted, or null: an unattributed connection, for which the host decides what is allowed.
    public string? Principal { get; }
    public bool Has(string capability) => capabilities.Contains(capability);

    public static string? TokenFrom(Func<string, string?> env) => env("AGENT_BRIDGE_TOKEN") is { } t && !string.IsNullOrWhiteSpace(t) ? t.Trim() : null;

    // Connects, says hello, and with a token proves which session this is. A host that is not listening, or refuses this process or the
    // token, fails the open; the token is never part of a message.
    public static async Task<McpHost> OpenAsync(LocalEndpoint endpoint, string? token, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var wait = timeout ?? RequestTimeout;
        LocalChannel channel;
        try { channel = await LocalClient.ConnectAsync(endpoint, wait < TimeSpan.FromSeconds(5) ? wait : TimeSpan.FromSeconds(5), ct, requireOwner: true); }
        catch (HostNotRunningException) { throw Unreachable(); }
        catch (LocalRefusedException e) { throw new McpToolException($"The {Product.HostName} refused this process: {e.Message}", McpFailure.Refused); }
        try
        {
            var hello = await channel.HelloAsync(ct);
            string? principal = null;
            if (token is not null)
            {
                var accepted = await Guard("agent.hello", wait, () => channel.CallAsync("agent.hello", new { token }, wait, ct));
                principal = accepted.ValueKind == JsonValueKind.Object && accepted.TryGetProperty("principal", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            }
            return new McpHost(channel, hello, principal, wait);
        }
        catch (LocalRefusedException e)
        {
            await channel.DisposeAsync();
            throw new McpToolException($"The {Product.HostName} refused this process: {e.Message}", McpFailure.Refused);
        }
        catch (Exception e) when (e is IOException or TimeoutException or HostErrorException)
        {
            await channel.DisposeAsync();
            throw e is HostErrorException ? new McpToolException(e.Message, McpFailure.Refused) : Unreachable();
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    public Task<JsonElement> CallAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        Guard(op, timeout ?? this.timeout, () => channel.CallAsync(op, data ?? new { }, timeout ?? this.timeout, ct));

    public ValueTask DisposeAsync() => channel.DisposeAsync();

    static McpToolException Unreachable() => new($"The {Product.HostName} is not reachable.", McpFailure.Unavailable);

    // The host's own words for a refusal; an unavailable host or a closed pipe is "unavailable", so a caller never reads it as a refusal.
    static async Task<JsonElement> Guard(string op, TimeSpan wait, Func<Task<JsonElement>> call)
    {
        try { return await call(); }
        catch (HostErrorException e) { throw new McpToolException(e.Message, McpFailure.Refused); }
        catch (TimeoutException) { throw new McpToolException($"The {Product.HostName} did not answer {op} in time.", McpFailure.Unavailable); }
        catch (IOException) { throw new McpToolException($"The connection to the {Product.HostName} closed.", McpFailure.Unavailable); }
        catch (MessageTooLargeException) { throw new McpToolException("The message is too large for the host pipe."); }
    }
}
