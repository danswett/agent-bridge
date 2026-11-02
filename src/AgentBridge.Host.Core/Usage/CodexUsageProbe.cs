using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Usage;

// What one Codex probe came to. Reading: a fresh reading. SignedOut: Codex reported that it can no
// longer refresh its sign-in (perhaps beside a reading its unexpired access token still got).
// NotApplicable: a login no probe can read (an API key, another provider) or no codex here; the
// transcripts stand, with no error. Skipped: nothing was asked (a Codex session is running without
// the managed daemon). Failed: anything else, which backs off.
internal enum CodexProbeKind { Reading, SignedOut, NotApplicable, Skipped, Failed }

internal sealed record CodexProbeOutcome(CodexProbeKind Kind, AgentUsage? Reading = null, string? Why = null);

// Reported: what the probe found, at most CodexReportTimeout after it started. Finished: when the
// host has let go of it: standard input closed after the grace period, or the daemon connection
// closed, or the process given up on as hung at the ceiling.
internal sealed record CodexProbeRun(Task<CodexProbeOutcome> Reported, Task Finished);

// Codex's own reading of its allowance, asked of its app-server (#200, spike #197 X-1 to X-5).
// Codex answers account/rateLimits/read with its own credentials and refresh, and the request
// starts no thread or turn. The route follows what Codex runs here (X-3):
//   * no Codex process: a standalone `codex app-server` over stdio, JSON lines, which is then the
//     only Codex process reading or writing its sign-in;
//   * the managed daemon running: a WebSocket on its control socket, so the daemon's own sign-in
//     answers and no second writer exists;
//   * a Codex session without the daemon: nothing is asked, and its transcripts are fresh anyway.
//
// Standalone, standard input is closed only after every answer and then a grace period longer than
// Codex's own refresh limits: closing it earlier lost rotated tokens on Linux (E13), and a lost
// rotation can later sign Codex out. A process that has not answered within CodexReportTimeout
// reports nothing but is left running, and is given up on as hung only at CodexCeiling. Nothing
// here ends a Codex process.
internal sealed class CodexUsageProbe(UsageOptions options, UsageProbes probes, TimeProvider clock, Action<string> log)
{
    public const string SignedOutError = "Codex needs signing in again on this machine";

    // Only these: no thread, no turn, and never `supportsLunaReserve`, which is for clients that
    // can apply Reserve, not for passive readers (X-1).
    internal static readonly string[] Methods = ["initialize", "initialized", "account/read", "account/rateLimits/read"];

    public CodexProbeRun Start()
    {
        var reported = new TaskCompletionSource<CodexProbeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = Task.Run(async () =>
        {
            try { await RunAsync(reported); }
            catch (Exception e) { reported.TrySetResult(new(CodexProbeKind.Failed, Why: $"the probe failed: {e.GetType().Name}")); }
            finally { reported.TrySetResult(new(CodexProbeKind.Failed, Why: "the probe ended without an answer")); }
        });
        return new(reported.Task, finished);
    }

    async Task RunAsync(TaskCompletionSource<CodexProbeOutcome> reported)
    {
        if (probes.Find(AgentKind.Codex) is not { } exe)
        {
            reported.TrySetResult(new(CodexProbeKind.NotApplicable, Why: "codex could not be found"));
            return;
        }
        var observed = probes.Processes.Observe(AgentKind.Codex);
        if (!observed.Listed)
        {
            reported.TrySetResult(new(CodexProbeKind.Skipped, Why: "the codex processes could not be listed"));
            return;
        }
        if (observed.Processes.Count == 0)
        {
            await StandaloneAsync(exe, reported);
            return;
        }
        IRpcLink? daemon;
        using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            daemon = await probes.Daemon.ConnectAsync(options.ControlSocket, connect.Token);
        }
        if (daemon is null)
        {
            reported.TrySetResult(new(CodexProbeKind.Skipped, Why: "a Codex session is running here without the managed daemon"));
            return;
        }
        await DaemonAsync(daemon, reported);
    }

    async Task StandaloneAsync(string exe, TaskCompletionSource<CodexProbeOutcome> reported)
    {
        var started = clock.GetUtcNow();
        var environment = ProbeEnvironment.Home("CODEX_HOME", options.CodexHome, ".codex");
        Directory.CreateDirectory(probes.WorkingDirectory);
        var process = probes.Launcher.Start(new ProbeCommand(exe, ["app-server"], probes.WorkingDirectory, environment, Stdio: true));
        if (process is null || process.Input is null || process.Reader is null)
        {
            process?.Dispose();
            reported.TrySetResult(new(CodexProbeKind.Failed, Why: "codex app-server could not be started"));
            return;
        }
        var link = new JsonLinesLink(process.Input, process.Reader);
        var watch = new CodexWatch(probes, process.Id, clock);
        var conversation = ConverseAsync(link, watch);
        var answered = await SettleAsync(conversation, reported, started);
        var ceiling = started + options.CodexCeiling;
        if (!answered) { answered = await WithinAsync(conversation, ceiling - clock.GetUtcNow()); }
        if (answered)
        {
            // After the last answer, long enough for any refresh Codex started to finish or hit its
            // own limit before end of input tears its runtime down (X-4, E13).
            await Task.Delay(options.CodexGrace, clock);
        }
        else
        {
            log($"usage: Codex's app-server has not answered in {options.CodexCeiling.TotalMinutes:0} minutes; given up on as hung, with its input closed");
        }
        process.CloseInput();
        var left = ceiling - clock.GetUtcNow();
        if (left > TimeSpan.Zero) { await WithinAsync(process.Exited, left); }
        if (process.Exited.IsCompleted) { process.Dispose(); }
        else { _ = process.Exited.ContinueWith(_ => process.Dispose(), TaskScheduler.Default); }
    }

    async Task DaemonAsync(IRpcLink link, TaskCompletionSource<CodexProbeOutcome> reported)
    {
        var started = clock.GetUtcNow();
        try
        {
            var conversation = ConverseAsync(link, watch: null);
            if (!await SettleAsync(conversation, reported, started))
            {
                await WithinAsync(conversation, started + options.CodexCeiling - clock.GetUtcNow());
            }
        }
        finally
        {
            // The daemon answers a close frame by dropping the connection, never with a close of its
            // own (E9), so the close is sent and not waited for.
            await link.CloseAsync();
        }
    }

    // Reports the conversation's outcome once it has one, or nothing once CodexReportTimeout has
    // passed. Returns whether it answered in time.
    async Task<bool> SettleAsync(Task<CodexProbeOutcome> conversation, TaskCompletionSource<CodexProbeOutcome> reported, DateTimeOffset started)
    {
        if (await WithinAsync(conversation, started + options.CodexReportTimeout - clock.GetUtcNow()))
        {
            reported.TrySetResult(Outcome(conversation));
            return true;
        }
        reported.TrySetResult(new(CodexProbeKind.Failed, Why: $"Codex did not answer within {options.CodexReportTimeout.TotalSeconds:0} s"));
        return false;
    }

    static CodexProbeOutcome Outcome(Task<CodexProbeOutcome> conversation) => conversation.Status == TaskStatus.RanToCompletion
        ? conversation.Result
        : new(CodexProbeKind.Failed, Why: conversation.Exception?.InnerException is EndOfStreamException ? "codex app-server ended before answering"
            : $"the conversation with Codex failed: {conversation.Exception?.InnerException?.GetType().Name ?? "cancelled"}");

    async Task<bool> WithinAsync(Task task, TimeSpan limit)
    {
        if (task.IsCompleted) { return true; }
        if (limit <= TimeSpan.Zero) { return false; }
        using var stop = new CancellationTokenSource();
        var done = await Task.WhenAny(task, Task.Delay(limit, clock, stop.Token)) == task;
        await stop.CancelAsync();
        return done;
    }

    // initialize, initialized, account/read, account/rateLimits/read, account/read: each sent once
    // the one before has answered. The two account/reads bracket the usage read so a login that
    // changed during it is seen (X-1); account/updated notifications are not used, since Codex sends
    // one to every new connection whether or not anything changed.
    async Task<CodexProbeOutcome> ConverseAsync(IRpcLink link, CodexWatch? watch)
    {
        var rpc = new Rpc(link);
        var init = await rpc.CallAsync("initialize", new { clientInfo = new { name = "agent_bridge", title = "Agent Bridge", version = probes.ClientVersion } });
        if (init.Error is { } initError) { return Failure(initError); }
        await link.SendAsync(JsonSerializer.Serialize(new { method = "initialized" }));
        watch?.Begin();
        var first = await rpc.CallAsync("account/read", new { refreshToken = false });
        if (first.Error is { } firstError) { watch?.End(); return Failure(firstError); }
        if (Login(first.Result) is { } login) { watch?.End(); return login; }
        var limits = await rpc.CallAsync("account/rateLimits/read", new { excludeResetCreditDetails = true });
        var measured = clock.GetUtcNow();
        if (limits.Error is { } limitsError)
        {
            watch?.End();
            return limitsError.Contains("chatgpt authentication required", StringComparison.OrdinalIgnoreCase)
                ? new(CodexProbeKind.NotApplicable, Why: "Codex is not signed in with ChatGPT")
                : Failure(limitsError, rateLimits: true);
        }
        var second = await rpc.CallAsync("account/read", new { refreshToken = false });
        var others = watch?.End() ?? true;
        var reading = Map(limits.Result, measured, null, Plan(first.Result));
        if (reading is null) { return new(CodexProbeKind.Failed, Why: "Codex's answer had no rate-limit windows"); }
        if (second.Error is { } secondError)
        {
            // The usage read can still succeed on an unexpired access token after the refresh token
            // was lost: that is the late sign-out signal the Linux run found (E13, E14).
            return SignedOut(secondError) && !Slow(secondError) ? new(CodexProbeKind.SignedOut, reading, secondError) : new(CodexProbeKind.Reading, reading);
        }
        if (Login(second.Result) is { Kind: CodexProbeKind.SignedOut } gone) { return gone with { Reading = reading }; }
        var key = !others ? Key(Email(first.Result), Email(second.Result), limits.Result) : null;
        return new(CodexProbeKind.Reading, reading with { AccountKey = key });
    }

    // account/read's answer, when it is not a ChatGPT login: account null with OpenAI sign-in
    // required is a sign-out (E14(a) run B); with it not required, another provider; any other
    // type, a login no usage read applies to.
    static CodexProbeOutcome? Login(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } r) { return new(CodexProbeKind.Failed, Why: "account/read answered nothing"); }
        if (UsageRead.Property(r, "account") is not { ValueKind: JsonValueKind.Object } account)
        {
            return UsageRead.Property(r, "requiresOpenaiAuth") is { ValueKind: JsonValueKind.True }
                ? new(CodexProbeKind.SignedOut, Why: "Codex reported no account")
                : new(CodexProbeKind.NotApplicable, Why: "Codex is not signed in with ChatGPT");
        }
        return UsageRead.String(account, "type") == "chatgpt" ? null : new(CodexProbeKind.NotApplicable, Why: "Codex is not signed in with ChatGPT");
    }

    static string? Email(JsonElement? result) =>
        result is { } r && UsageRead.Property(r, "account") is { } account && UsageRead.String(account, "email") is { Length: > 0 } email ? email : null;

    static string? Plan(JsonElement? result) =>
        result is { } r && UsageRead.Property(r, "account") is { } account ? UsageRead.String(account, "planType") : null;

    // Codex's error texts (E13 on Linux, E14). These three come from a slow token endpoint, not a
    // sign-out, and are retried after the back-off.
    static bool Slow(string message) =>
        message.Contains("request timed out", StringComparison.OrdinalIgnoreCase)
        || message.Contains("routing discovery timed out", StringComparison.OrdinalIgnoreCase)
        || message.Contains("network policy is unavailable", StringComparison.OrdinalIgnoreCase);

    // A refresh token Codex can no longer use: revoked ("Your access token could not be refreshed.
    // Please log out and sign in again."), reused ("... because your refresh token was already
    // used ...", refresh_token_reused). Codex reloads its sign-in after one 401 and refreshes after
    // a second before it gives up, so what reaches here is its final answer.
    static bool SignedOut(string message) =>
        message.Contains("sign in again", StringComparison.OrdinalIgnoreCase)
        || message.Contains("refresh_token_reused", StringComparison.OrdinalIgnoreCase)
        || message.Contains("refresh token was already used", StringComparison.OrdinalIgnoreCase)
        || message.Contains("could not be refreshed", StringComparison.OrdinalIgnoreCase);

    static CodexProbeOutcome Failure(string message, bool rateLimits = false)
    {
        if (Slow(message)) { return new(CodexProbeKind.Failed, Why: "Codex's sign-in service was slow to answer"); }
        // A 401 from the usage read itself, once Codex has tried to recover from it (E14).
        if (SignedOut(message) || (rateLimits && (message.Contains("401", StringComparison.Ordinal) || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))))
        {
            return new(CodexProbeKind.SignedOut, Why: "Codex could not refresh its sign-in");
        }
        return new(CodexProbeKind.Failed, Why: "Codex answered with an error");
    }

    // The windows from rateLimitsByLimitId.codex, else rateLimits, as the transcripts give them.
    internal static AgentUsage? Map(JsonElement? result, DateTimeOffset measured, string? accountKey, string? accountPlan)
    {
        if (result is not { ValueKind: JsonValueKind.Object } r) { return null; }
        var all = UsageRead.Property(r, "rateLimits");
        var snapshot = UsageRead.Property(r, "rateLimitsByLimitId") is { ValueKind: JsonValueKind.Object } byId
            && UsageRead.Property(byId, "codex") is { ValueKind: JsonValueKind.Object } codex ? codex : all;
        if (snapshot is not { ValueKind: JsonValueKind.Object } s) { return null; }
        var windows = new List<UsageWindow>();
        foreach (var (name, fallback) in new[] { ("primary", "5h limit"), ("secondary", "Weekly") })
        {
            if (UsageRead.Property(s, name) is not { } window || UsageRead.Number(window, "usedPercent") is not { } percent) { continue; }
            DateTimeOffset? resets = UsageRead.Number(window, "resetsAt") is { } seconds and > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
            windows.Add(UsageRead.Window(name, CodexUsageCollector.WindowLabel(UsageRead.Number(window, "windowDurationMins"), fallback), percent, resets));
        }
        if (windows.Count == 0) { return null; }
        return new AgentUsage
        {
            Agent = AgentKind.Codex,
            Label = UsageRead.Label(AgentKind.Codex),
            AccountKey = accountKey,
            Plan = UsageRead.String(s, "planType") ?? (all is { } a ? UsageRead.String(a, "planType") : null) ?? accountPlan,
            Source = UsageSource.Api,
            Windows = windows,
            MeasuredAt = measured,
        };
    }

    // The user and the workspace: one ChatGPT workspace's members each have limits of their own, so
    // accountId alone cannot group readings. Only when the login held steady through the read: the
    // same email both times, and a snapshot the handler matched to the active login when it answered
    // (ordinaryUsageAllowed is non-null only then, X-1).
    internal static string? Key(string? before, string? after, JsonElement? limits)
    {
        if (before is null || after is null || !string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) { return null; }
        if (limits is not { ValueKind: JsonValueKind.Object } r || UsageRead.String(r, "accountId") is not { Length: > 0 } workspace) { return null; }
        if (UsageRead.Property(r, "ordinaryUsageAllowed") is not { ValueKind: JsonValueKind.True or JsonValueKind.False }) { return null; }
        return AccountKey(after, workspace);
    }

    internal static string AccountKey(string email, string accountId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"codex:{email.ToLowerInvariant()}:{accountId}")))[..12];

    sealed record Reply(JsonElement? Result, string? Error);

    // JSON-RPC 2.0 without the "jsonrpc" member, as Codex speaks it. Replies are matched by id;
    // notifications (account/updated among them) are read past.
    sealed class Rpc(IRpcLink link)
    {
        int next;

        public async Task<Reply> CallAsync(string method, object parameters)
        {
            var id = ++next;
            await link.SendAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }));
            while (true)
            {
                var text = await link.ReceiveAsync() ?? throw new EndOfStreamException();
                JsonDocument document;
                try { document = JsonDocument.Parse(text); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || UsageRead.Property(root, "method") is not null) { continue; }
                    if (UsageRead.Property(root, "id") is not { ValueKind: JsonValueKind.Number } got || !got.TryGetInt32(out var gotId) || gotId != id) { continue; }
                    if (UsageRead.Property(root, "error") is { } error)
                    {
                        return new(null, UsageRead.String(error, "message") ?? "an error with no message");
                    }
                    return new(UsageRead.Property(root, "result")?.Clone(), null);
                }
            }
        }
    }
}

// Whether any Codex process other than the probe's own ran while it read: from just before the
// first account/read to just after the second. A login that switched and switched back in that
// time leaves both reads naming the first account around a reading of the second, and on the
// standalone route only another Codex process can switch it (X-1).
internal sealed class CodexWatch(UsageProbes probes, int own, TimeProvider clock)
{
    static readonly TimeSpan Every = TimeSpan.FromMilliseconds(250);
    readonly CancellationTokenSource stop = new();
    Task? polling;
    volatile bool seen;

    public void Begin()
    {
        Look();
        polling = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(Every, clock, stop.Token);
                    Look();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    // Whether another Codex process was seen at any point.
    public bool End()
    {
        stop.Cancel();
        Look();
        return seen;
    }

    void Look()
    {
        try
        {
            var observed = probes.Processes.Observe(AgentKind.Codex);
            if (!observed.Listed || observed.Processes.Any(p => !Own(p.Id))) { seen = true; }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { seen = true; }
    }

    // The probe itself and what it started (codex.cmd runs node, which runs codex.exe).
    bool Own(int pid)
    {
        for (var (p, depth) = (pid, 0); depth < 8; depth++)
        {
            if (p == own) { return true; }
            if (probes.ParentOf(p) is not { } parent || parent <= 0 || parent == p) { return false; }
            p = parent;
        }
        return false;
    }
}

// One JSON-RPC message per send and receive: a JSON line on the app-server's stdio, or a text frame
// on the daemon's WebSocket. Receive returns null once the other end has gone.
public interface IRpcLink
{
    Task SendAsync(string message);
    Task<string?> ReceiveAsync();
    Task CloseAsync();
}

// The managed daemon, if it runs: a link to it, or null when nothing listens on its socket.
public interface ICodexDaemon
{
    Task<IRpcLink?> ConnectAsync(string socket, CancellationToken cancellationToken);
}

internal sealed class JsonLinesLink(Stream input, Stream output) : IRpcLink
{
    const int MaxLine = 4 * 1024 * 1024;
    readonly StreamWriter writer = new(input, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
    readonly StreamReader reader = new(output, Encoding.UTF8);

    public async Task SendAsync(string message)
    {
        await writer.WriteAsync(message);
        await writer.WriteAsync('\n');
        await writer.FlushAsync();
    }

    // A line too long to be any answer here ends the conversation rather than filling memory.
    public async Task<string?> ReceiveAsync()
    {
        var line = new StringBuilder();
        var one = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(one.AsMemory()) == 0) { return line.Length > 0 ? line.ToString() : null; }
            if (one[0] == '\n') { return line.ToString().TrimEnd('\r'); }
            if (line.Length >= MaxLine) { throw new InvalidDataException("a line from codex app-server was too long"); }
            line.Append(one[0]);
        }
    }

    // Standard input is the process's to close (IProbeProcess.CloseInput), after the grace period.
    public Task CloseAsync() => Task.CompletedTask;
}

// The daemon's control socket: a Unix-domain socket (Windows has them too) that speaks WebSocket
// after a standard HTTP upgrade, one JSON-RPC message per text frame (E9). The path is "/": the
// socket's /daemon/shutdown path stops the daemon.
public sealed class CodexDaemonSocket : ICodexDaemon
{
    public async Task<IRpcLink?> ConnectAsync(string socket, CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            // The connection is the socket below, never a proxy's.
            UseProxy = false,
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await s.ConnectAsync(new UnixDomainSocketEndPoint(socket), ct);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            },
        };
        var invoker = new HttpMessageInvoker(handler);
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri("ws://localhost/"), invoker, cancellationToken);
            return new WebSocketLink(ws, invoker);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or SocketException or IOException or OperationCanceledException
            or PlatformNotSupportedException or ArgumentException)
        {
            ws.Dispose();
            invoker.Dispose();
            return null;
        }
    }
}

internal sealed class WebSocketLink(WebSocket socket, IDisposable? owner = null) : IRpcLink
{
    const int MaxMessage = 4 * 1024 * 1024;

    public Task SendAsync(string message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

    public async Task<string?> ReceiveAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult got;
            try { got = await socket.ReceiveAsync(buffer, CancellationToken.None); }
            catch (WebSocketException) { return null; }
            if (got.MessageType == WebSocketMessageType.Close) { return null; }
            message.Write(buffer, 0, got.Count);
            if (message.Length > MaxMessage) { throw new InvalidDataException("a message from the Codex daemon was too long"); }
            if (got.EndOfMessage) { return got.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length) : ""; }
        }
    }

    // A close frame, not waited on: the daemon drops the connection instead of answering it (E9).
    public async Task CloseAsync()
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
        finally
        {
            socket.Abort();
            socket.Dispose();
            owner?.Dispose();
        }
    }
}
