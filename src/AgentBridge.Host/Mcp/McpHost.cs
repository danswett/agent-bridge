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
public sealed class McpHost : IMcpCalls, IAsyncDisposable
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    // What of the host's own words reaches an agent in an exception message (#4450): at most this many characters, flattened, with anything token-shaped redacted.
    public const int MaxHostText = 512;
    const int MinExactToken = 6;

    readonly LocalChannel channel;
    readonly TimeSpan timeout;
    readonly HashSet<string> capabilities;
    // The token this server sent, kept only to redact it from a later refusal (it is already this process's own AGENT_BRIDGE_TOKEN; nothing new is exposed).
    readonly string? sentToken;

    McpHost(LocalChannel channel, HelloResult hello, string? principal, TimeSpan timeout, string? token)
    {
        sentToken = token;
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
        if (!OperatingSystem.IsWindows() && endpoint.Folder.Length == 0) { RefuseSharedSocket(endpoint.Address); }
        LocalChannel channel;
        try { channel = await LocalClient.ConnectAsync(endpoint, wait < TimeSpan.FromSeconds(5) ? wait : TimeSpan.FromSeconds(5), ct); }
        catch (HostNotRunningException) { throw Unreachable(); }
        catch (LocalRefusedException e) { throw new McpToolException($"The {Product.HostName} refused this process: {Scrub(e.Message, token)}", McpFailure.Refused); }
        try
        {
            var hello = await channel.HelloAsync(ct);
            string? principal = null;
            if (token is not null)
            {
                var accepted = await Guard("agent.hello", wait, () => channel.CallAsync("agent.hello", new { token }, wait, ct), token);
                principal = accepted.ValueKind == JsonValueKind.Object && accepted.TryGetProperty("principal", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            }
            return new McpHost(channel, hello, principal, wait, token);
        }
        catch (LocalRefusedException e)
        {
            await channel.DisposeAsync();
            throw new McpToolException($"The {Product.HostName} refused this process: {Scrub(e.Message, token)}", McpFailure.Refused);
        }
        catch (Exception e) when (e is IOException or TimeoutException or HostErrorException)
        {
            await channel.DisposeAsync();
            throw e is HostErrorException ? new McpToolException(Scrub(e.Message, token), McpFailure.Refused) : Unreachable();
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    // An explicit Unix socket path (AGENT_BRIDGE_PIPE; the derived one lives in the host's own 0700 folder) is talked to only when neither the socket nor its
    // folder can be written by group or others, so another user cannot have put a socket there. Windows has CurrentUserOnly on the client instead.
    public static bool IsPrivateMode(UnixFileMode mode) => (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;

    static void RefuseSharedSocket(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) { return; } // no socket: the connect says the host is not running
        try
        {
            var folder = Path.GetDirectoryName(path);
            if ((folder is not null && !IsPrivateMode(File.GetUnixFileMode(folder))) || !IsPrivateMode(File.GetUnixFileMode(path)))
            {
                throw new McpToolException($"The {Product.HostName}'s socket can be written by other users, so it is not used.", McpFailure.Refused);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Unreachable(); }
    }

    public Task<JsonElement> CallAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        Guard(op, timeout ?? this.timeout, () => channel.CallAsync(op, data ?? new { }, timeout ?? this.timeout, ct), sentToken);

    public ValueTask DisposeAsync() => channel.DisposeAsync();

    // The host's words, made safe to hand to an agent: control characters flattened, the exact token this server sent and anything token-shaped (a
    // "abat1_" credential, or a run of 32 or more key characters with at least 10 different ones; a UUID is not a secret and stays) replaced by [redacted],
    // and the whole bounded to MaxHostText. The host does not echo tokens; this is the second line if one ever did.
    public static string Scrub(string? text, string? token)
    {
        if (string.IsNullOrEmpty(text)) { return ""; }
        var plain = AnsiSequence.Replace(text, "");
        var flat = System.Text.RegularExpressions.Regex.Replace(new string([.. plain.Select(c => char.IsControl(c) || IsInvisible(c) ? ' ' : c)]), @"\s{2,}", " ").Trim();
        if (token is { Length: >= MinExactToken }) { flat = flat.Replace(token, "[redacted]", StringComparison.Ordinal); } // a shorter value would redact ordinary letters; real tokens are far longer
        flat = TokenShaped.Replace(flat, m => m.Value.StartsWith("abat1_", StringComparison.Ordinal) || (!Uuid.IsMatch(m.Value) && m.Value.Distinct().Count() >= 10) ? "[redacted]" : m.Value);
        if (flat.Length <= MaxHostText) { return flat; }
        var cut = char.IsHighSurrogate(flat[MaxHostText - 1]) ? MaxHostText - 1 : MaxHostText; // never half of a surrogate pair
        return flat[..cut] + "…";
    }

    // Line and paragraph separators, bidi controls and zero-width characters are not words: they become spaces, and a terminal escape sequence goes whole.
    static bool IsInvisible(char c) => c is '\u2028' or '\u2029' or '\uFEFF' or (>= '\u200B' and <= '\u200F') or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');
    static readonly System.Text.RegularExpressions.Regex AnsiSequence = new(@"\u001B\[[0-9;?]*[ -/]*[@-~]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static readonly System.Text.RegularExpressions.Regex TokenShaped = new(@"abat1_[A-Za-z0-9_\-]+|[A-Za-z0-9_\-+/]{32,}={0,2}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    static readonly System.Text.RegularExpressions.Regex Uuid = new(@"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static McpToolException Unreachable() => new($"The {Product.HostName} is not reachable.", McpFailure.Unavailable);

    // The host's own words for a refusal; an unavailable host or a closed pipe is "unavailable", so a caller never reads it as a refusal.
    static async Task<JsonElement> Guard(string op, TimeSpan wait, Func<Task<JsonElement>> call, string? token = null)
    {
        try { return await call(); }
        catch (HostErrorException e) { throw new McpToolException(Scrub(e.Message, token), McpFailure.Refused); }
        catch (TimeoutException) { throw new McpToolException($"The {Product.HostName} did not answer {op} in time.", McpFailure.Unavailable); }
        catch (IOException) { throw new McpToolException($"The connection to the {Product.HostName} closed.", McpFailure.Unavailable); }
        catch (MessageTooLargeException) { throw new McpToolException("The message is too large for the host pipe."); }
    }
}
