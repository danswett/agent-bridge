using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Hooks;

// agent-bridge-host-hook: this product's hook, registered for each agent on a machine that
// has migrated from agent-ha-bridge (docs/host-hooks.md):
//
//   agent-bridge-host-hook <agent> <hook>               spool the event while the host is
//                                                       taking them, else the fixed reply
//   agent-bridge-host-hook --fallback <agent> <hook>    the fixed reply alone
//   agent-bridge-host-hook --version
//
// It reads the event from stdin, records its process ancestry, writes both to the spool the
// host reads and prints the reply the agent expects, in tens of milliseconds. When no host is
// taking events (no fresh heartbeat), it prints the same fixed reply and nothing else: unlike
// agent-ha-bridge's hook, it has no PowerShell to fall back to, and never reaches Home
// Assistant, so an agent waits no longer with the host down than with it up.
//
// It never fails the agent (invariant 14): anything unexpected prints the fixed reply and
// exits 0. And the reply to Copilot's ask_user always lets the tool run, at once, so the
// question is shown at the terminal and stays answerable there (invariant 3).
public static class HookRunner
{
    // Beside the program: where the host's runtime is, written when a machine migrates.
    public const string PointerFile = "hook.json";

    public static int Run(IReadOnlyList<string> args, Stream stdin, Stream stdout, HookEnvironment env)
    {
        string? reply = null;
        var replied = false;
        void Reply()
        {
            if (replied || reply is null) { return; }
            replied = true;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(reply);
                stdout.Write(bytes);
                stdout.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { }
        }

        try
        {
            if (args.Count >= 1 && args[0] == "--version")
            {
                reply = env.Version + "\n";
                Reply();
                return 0;
            }
            var rest = args.ToList();
            var forced = rest.Remove("--fallback");
            if (rest.Count < 2) { return 0; }
            var (agent, hook) = (rest[0], rest[1]);
            reply = SpoolContract.Reply(agent, hook);
            if (reply is null) { return 0; }

            if (forced)
            {
                // The reply needs nothing from the event, so it goes before stdin is touched: an
                // inherited console, or a pipe left open, must not hold it. What was sent is then
                // drained for a moment, so a writer still writing is not cut off mid-event.
                Reply();
                var drain = Task.Run(() => ReadAll(stdin, SpoolContract.MaxEventBytes));
                drain.Wait(env.FallbackDrain);
                return 0;
            }
            // Bounded in time as well as size: an agent sends its event and closes the pipe, but one
            // that left it open would otherwise hold the agent (ask_user above all, invariant 3) on
            // a hook waiting for an end that never comes. Stopped at the deadline, the read is left
            // behind; the process ends without it.
            var read = Task.Run(() => ReadAll(stdin, SpoolContract.MaxEventBytes));
            var finished = read.Wait(env.InputWait);
            var (utf8, whole) = finished ? read.Result : ([], false);

            var (runtime, rootProblem) = RuntimeRoot(env.ExecutableDirectory);
            if (runtime is null)
            {
                // Nowhere to record why, and nothing to hand the event to.
                Reply();
                return 0;
            }

            var path = "reply";
            string? reason = null;
            try
            {
                var (alive, why) = SpoolContract.ConsumerState(runtime, env.Now());
                var trimmed = Trim(utf8);
                if (!alive) { reason = why; }
                else if (!finished) { reason = $"event not finished within {env.InputWait.TotalSeconds:0.###}s"; }
                else if (!whole) { reason = "event too large"; }
                else if (!SpoolContract.IsJson(trimmed)) { reason = "unusable event"; }
                else
                {
                    try
                    {
                        SpoolContract.Write(runtime, agent, hook, trimmed, env.Ancestors(), env.Now(), env.ProcessId);
                        path = "spool";
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        reason = "spool failed: " + e.Message;
                    }
                }
            }
            finally
            {
                Reply();
                SpoolContract.Record(runtime, agent, hook, path, reason ?? rootProblem, env.Now(), env.ElapsedMs());
            }
            return 0;
        }
        catch
        {
            Reply();
            return 0;
        }
    }

    // The runtime root the pointer beside the program names, or null with why.
    public static (string? Root, string? Problem) RuntimeRoot(string? executableDirectory)
    {
        if (string.IsNullOrEmpty(executableDirectory)) { return (null, "the hook's own folder is unknown"); }
        byte[] bytes;
        try { bytes = File.ReadAllBytes(Path.Combine(executableDirectory, PointerFile)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, $"no {PointerFile} beside the hook"); }
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var version) && version == 1
                && root.TryGetProperty("runtimeRoot", out var r) && r.ValueKind == JsonValueKind.String
                && r.GetString() is { Length: > 0 } runtime && Path.IsPathFullyQualified(runtime))
            {
                return (runtime, null);
            }
        }
        catch (JsonException) { }
        return (null, $"{PointerFile} is not valid");
    }

    // The pointer's content, for the host to write when a machine migrates.
    public static byte[] Pointer(string runtimeRoot)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("runtimeRoot", runtimeRoot);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    // Up to max bytes of stdin, and whether that was all of it. The budget is checked before
    // each read and bounds it, so a writer that keeps the pipe open once max bytes have come
    // cannot hold the hook waiting for one more; an event that fills it is not taken.
    static (byte[] Bytes, bool Whole) ReadAll(Stream stdin, int max)
    {
        try
        {
            using var copy = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var room = max - (int)copy.Length;
                if (room <= 0) { return (copy.ToArray(), false); }
                var read = stdin.Read(buffer, 0, Math.Min(buffer.Length, room));
                if (read <= 0) { return (copy.ToArray(), true); }
                copy.Write(buffer, 0, read);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { return ([], true); }
    }

    static ReadOnlySpan<byte> Trim(byte[] bytes)
    {
        var span = bytes.AsSpan();
        // A byte order mark is not JSON, but an agent on Windows may send one.
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) { span = span[3..]; }
        return span.Trim((ReadOnlySpan<byte>)" \t\r\n"u8);
    }
}

// What the hook needs from its process, injectable so tests never depend on the real one.
public sealed class HookEnvironment
{
    public required string? ExecutableDirectory { get; init; }
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
    public int ProcessId { get; init; } = Environment.ProcessId;
    public Func<IReadOnlyList<int>> Ancestors { get; init; } = () => Ancestry.OfParent();
    public Func<long> ElapsedMs { get; init; } = SinceStart;
    public string Version { get; init; } = "dev";
    // How long --fallback waits, after replying, for an event still being written to it.
    public TimeSpan FallbackDrain { get; init; } = TimeSpan.FromSeconds(2);
    // How long the hook waits for the whole event, so an agent that never closes stdin is not
    // held: an event comes in milliseconds.
    public TimeSpan InputWait { get; init; } = TimeSpan.FromSeconds(5);

    // How long the agent has waited on this process, from when the OS started it.
    static long SinceStart()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return (long)(DateTime.Now - self.StartTime).TotalMilliseconds;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { return 0; }
    }
}
