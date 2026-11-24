using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentBridge.Hooks;

// #1003 PR C: the hook's end of approval.wait (docs/design/claude-approval-channel.md), over the host's local pipe (docs/protocol/local-pipe.md): hello, agent.hello with
// the session token, approval.wait with the event. Nothing is sent until the server is verified as the host's own program; anything but allow or deny is none. AOT-safe.
public static class ApprovalClient
{
    public const int MaxEventBytes = 16 * 1024;
    const int MaxFrame = 65519;

    public static bool IsHostImage(string? serverImage, string? expectedHostExe) =>
        !string.IsNullOrEmpty(serverImage) && string.Equals(serverImage, expectedHostExe, StringComparison.OrdinalIgnoreCase);

    // Windows only; elsewhere nothing is asked. Untested: it needs a pipe server in another process.
    public static Func<NamedPipeClientStream, bool> HostVerifier(string? expectedHostExe) => pipe => OperatingSystem.IsWindows() && IsHostImage(ServerImage(pipe), expectedHostExe);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static unsafe string? ServerImage(NamedPipeClientStream pipe)
    {
        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid) == 0) { return null; }
        var process = OpenProcess(0x1000, 0, pid);
        if (process == IntPtr.Zero) { return null; }
        try
        {
            var buffer = stackalloc char[1024];
            uint size = 1024;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) != 0 ? new string(buffer, 0, (int)size) : null;
        }
        finally { CloseHandle(process); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern int GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, int inherit, uint pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern unsafe int QueryFullProcessImageNameW(IntPtr process, uint flags, char* buffer, ref uint size);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern int CloseHandle(IntPtr handle);

    public static async Task<string?> AskHostAsync(string pipeName, Func<NamedPipeClientStream, bool> verifyServer, string eventText, string? token, TimeSpan wait, TimeSpan connectWait)
    {
        if (string.IsNullOrEmpty(token) || Encoding.UTF8.GetByteCount(eventText) > MaxEventBytes) { return null; }
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using (var connect = new CancellationTokenSource(connectWait)) { await pipe.ConnectAsync(connect.Token); }
            return verifyServer(pipe) ? await AskAsync(pipe, eventText, token, wait) : null;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException) { return null; }
    }

    public static async Task<string?> AskAsync(Stream pipe, string eventText, string? token, TimeSpan wait)
    {
        if (string.IsNullOrEmpty(token) || Encoding.UTF8.GetByteCount(eventText) > MaxEventBytes) { return null; }
        using var limit = new CancellationTokenSource(wait);
        try
        {
            await WriteAsync(pipe, "{\"id\":1,\"op\":\"hello\",\"data\":{}}", limit.Token);
            using var hello = await ReplyAsync(pipe, 1, limit.Token);
            if (hello is null || !Succeeded(hello, out _)) { return null; }
            await WriteAsync(pipe, $"{{\"id\":2,\"op\":\"agent.hello\",\"data\":{{\"token\":\"{JavaScriptEncoder.Default.Encode(token)}\"}}}}", limit.Token);
            using var agent = await ReplyAsync(pipe, 2, limit.Token);
            if (agent is null || !Succeeded(agent, out _)) { return null; }
            await WriteAsync(pipe, $"{{\"id\":3,\"op\":\"approval.wait\",\"data\":{{\"event\":\"{JavaScriptEncoder.Default.Encode(eventText)}\"}}}}", limit.Token);
            using var answer = await ReplyAsync(pipe, 3, limit.Token);
            if (answer is null || !Succeeded(answer, out var result)) { return null; }
            var verdict = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return verdict is "allow" or "deny" ? verdict : null;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException) { return null; }
    }

    static bool Succeeded(JsonDocument reply, out JsonElement result)
    {
        result = default;
        var root = reply.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) { return false; }
        if (root.TryGetProperty("result", out var r)) { result = r; }
        return true;
    }

    // The frame answering request `id`; events, progress and other frames are skipped.
    static async Task<JsonDocument?> ReplyAsync(Stream pipe, int id, CancellationToken ct)
    {
        while (true)
        {
            var head = new byte[4];
            if (!await ReadAsync(pipe, head, ct)) { return null; }
            var length = BinaryPrimitives.ReadInt32LittleEndian(head);
            if (length <= 0 || length > MaxFrame) { return null; }
            var body = new byte[length];
            if (!await ReadAsync(pipe, body, ct)) { return null; }
            var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("re", out var re) && re.ValueKind == JsonValueKind.Number
                && re.TryGetInt32(out var n) && n == id && !root.TryGetProperty("progress", out _)) { return doc; }
            doc.Dispose();
        }
    }

    static async Task<bool> ReadAsync(Stream pipe, byte[] buffer, CancellationToken ct)
    {
        for (var read = 0; read < buffer.Length;)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) { return false; }
            read += n;
        }
        return true;
    }

    static async Task WriteAsync(Stream pipe, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var head = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, body.Length);
        await pipe.WriteAsync(head, ct);
        await pipe.WriteAsync(body, ct);
        await pipe.FlushAsync(ct);
    }
}
