using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Diagnostics;

public enum HostExitReason { Unknown, CleanStop, PlannedRestart, UnhandledException, NoRecord }
public sealed record HostExitRecord(HostExitReason Reason, DateTimeOffset? At, bool Running = false);

public sealed class HostExitMarker
{
    public const string FileName = "host-exit.json";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    internal static readonly AsyncLocal<Action?> BeforeReplace = new();
    readonly object gate = new();
    readonly string path;
    readonly DateTimeOffset startedAt;
    readonly Action<string> log;
    readonly TimeProvider time;
    HostExitReason? ended;

    HostExitMarker(string path, DateTimeOffset startedAt, Action<string>? log, TimeProvider? time)
    {
        this.path = path;
        this.startedAt = startedAt;
        this.log = log ?? Console.Error.WriteLine;
        this.time = time ?? TimeProvider.System;
    }

    public static HostExitMarker Begin(string path, DateTimeOffset startedAt, Action<string>? log = null, TimeProvider? time = null)
    {
        var marker = new HostExitMarker(path, startedAt, log, time);
        marker.Write("running", null, null, null);
        return marker;
    }

    public bool End(HostExitReason reason, object? thrown = null)
    {
        if (!Enum.IsDefined(reason) || reason == HostExitReason.NoRecord) { reason = HostExitReason.Unknown; }
        lock (gate)
        {
            // ProcessExit is only a fallback; a crash must also supersede a requested stop.
            if (ended is { } previous && reason <= previous) { return true; }
            var exceptionType = reason == HostExitReason.UnhandledException
                ? thrown is Exception e ? JournalIdentifiers.TypeName(e.GetType()) ?? JournalContract.Unrecognized : JournalContract.Unrecognized
                : null;
            if (!Write("ended", Name(reason), time.GetUtcNow(), exceptionType)) { return false; }
            ended = reason;
            return true;
        }
    }

    public static string Name(HostExitReason reason) => reason switch
    {
        HostExitReason.CleanStop => "clean-stop",
        HostExitReason.PlannedRestart => "planned-restart",
        HostExitReason.UnhandledException => "unhandled-exception",
        _ => "unknown",
    };

    public static HostExitRecord? ReadPrevious(string path) =>
        Read(path) is { } previous ? previous.Running ? previous with { Reason = HostExitReason.NoRecord } : previous : null;

    public static void ReportPrevious(string path, Action<string> log, IDiagnosticsJournal journal, DateTimeOffset now)
    {
        if (ReadPrevious(path) is not { } previous) { return; }
        log(previous.Reason == HostExitReason.NoRecord
            ? "previous run ended: no exit record (hard kill, crash or power loss)"
            : $"previous run ended: {Name(previous.Reason)}");
        journal.Record(JournalEvents.PreviousHostExit(previous, now));
    }

    bool Write(string state, string? reason, DateTimeOffset? exitedAt, string? exceptionType)
    {
        var temporary = path + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { state, pid = Environment.ProcessId, startedAt, reason, exitedAt, exceptionType }, Json);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            BeforeReplace.Value?.Invoke();
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"exit marker write failed: {e.GetType().Name}");
            return false;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"exit marker cleanup failed: {e.GetType().Name}"); }
        }
    }

    public static HostExitRecord? Read(string path)
    {
        var unknown = new HostExitRecord(HostExitReason.Unknown, null);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 4096) { return unknown; }
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (!root.TryGetProperty("state", out var state)) { return unknown; }
            if (state.GetString() == "running")
            {
                return root.TryGetProperty("startedAt", out var start) && start.TryGetDateTimeOffset(out var at)
                    ? new(HostExitReason.Unknown, at, Running: true) : unknown;
            }
            if (state.GetString() != "ended" || !root.TryGetProperty("reason", out var reason)
                || !root.TryGetProperty("exitedAt", out var end) || !end.TryGetDateTimeOffset(out var exitedAt)) { return unknown; }
            return new(reason.GetString() switch
            {
                "clean-stop" => HostExitReason.CleanStop,
                "planned-restart" => HostExitReason.PlannedRestart,
                "unhandled-exception" => HostExitReason.UnhandledException,
                _ => HostExitReason.Unknown,
            }, exitedAt);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { return unknown; }
    }
}
