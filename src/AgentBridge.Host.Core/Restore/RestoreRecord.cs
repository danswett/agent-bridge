using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Restore;

// What a session was doing when the host last wrote it down. ProtocolJson's enum converter needs
// [UnknownAs]: a value a later host writes reads as idle, which promises the least.
[UnknownAs(Idle)]
public enum RestoreTurn { Idle, Working, Waiting }

// What a launch through this host started a session with (LaunchedWith): the launcher and the
// settings launch.options offers, never a prompt. A session started by hand has none, and its
// permission mode is unknown: it is never taken to be Allow all.
public sealed record RestoreLaunch(string Launcher, string? Workspace, bool? Worktree, string? Profile, string? Model, string? Effort, string? Context, string Permissions);

// One session the host saw live (#780). Nothing beyond what launch.options already says of a
// session: no prompt, response, question text or attachment. Gone: the host saw it go, and when.
public sealed record RestoreEntry
{
    public required string Id { get; init; }
    public required AgentKind Agent { get; init; }
    public string? Title { get; init; }
    public string? Cwd { get; init; }
    public string? Model { get; init; }
    public RestoreTurn Turn { get; init; }
    public bool QuestionWaiting { get; init; }
    public RestoreLaunch? Launch { get; init; }
    public required DateTimeOffset Seen { get; init; }
    public DateTimeOffset? Gone { get; init; }
}

// A session that went with an earlier run of the host, with why, about when (Since: when that run
// last wrote its record) and when this was found (At). Decided: judged to have died, and offered to
// restore. Not yet: its agent's discovery was held, so whether it still runs could not be told, and
// it waits to be judged rather than be read as absent (§6.3, #43).
// EndedSeen: its agent was once seen to record a normal ending while it was still being judged,
// kept because the record of it may be gone by the time it can be judged (the hook intake prunes
// an ended Codex registration once its process has gone).
// Uncertain: a Claude or Codex session judged on the same boot with its registration gone, so whether
// it ended is unknown (RestoreVerdict.Uncertain): never offered or pushed, its folder kept as an
// offer's is, for as long.
public sealed record RestoreOwed(RestoreEntry Entry, RestoreCause Cause, DateTimeOffset? Since, DateTimeOffset At, bool Decided, bool? EndedSeen = null, bool? Uncertain = null);

// restore.json, in the profile's folder beside config.json. Boot: when the machine started, as this
// run of the host read it; a later run on another boot knows the machine restarted. Stopped: the host
// stopped cleanly (an update, a stop or a restart). Sessions: the live ones, and for a short while
// those it saw go. Owed: sessions of earlier runs that died, or are still being judged.
public sealed record RestoreFile
{
    public const int CurrentVersion = 1;
    public int V { get; init; } = CurrentVersion;
    public required DateTimeOffset Boot { get; init; }
    public required string Instance { get; init; }
    public required DateTimeOffset Written { get; init; }
    public DateTimeOffset? Stopped { get; init; }
    public IReadOnlyList<RestoreEntry> Sessions { get; init; } = [];
    public IReadOnlyList<RestoreOwed> Owed { get; init; } = [];
}

public static class RestoreFiles
{
    public const string FileName = "restore.json";

    // Never more than a start would read. A record of a few hundred sessions is tens of KiB.
    public const long MaxBytes = 4 * 1024 * 1024;

    // What the record was, or null with why not (null and null: there is none). One that cannot be
    // read whole is not guessed at.
    public static (RestoreFile? File, string? Problem) Read(string path)
    {
        try
        {
            // Absent only when the OS says so: FileInfo.Exists is false for a file whose details
            // cannot be read too, which would read a record nobody can see as no record at all.
            byte[] bytes;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxBytes) { return (null, $"it is larger than {MaxBytes / 1024 / 1024} MiB"); }
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return (null, null); }
            var file = JsonSerializer.Deserialize<RestoreFile>(bytes, ProtocolJson.Options);
            if (file is null) { return (null, "it is empty"); }
            if (file.V != RestoreFile.CurrentVersion) { return (null, $"it is version {file.V}, not {RestoreFile.CurrentVersion}"); }
            // JSON null reads into anything, whatever the record says it holds.
            if (file.Sessions is null || file.Owed is null || file.Sessions.Any(e => e?.Id is not { Length: > 0 }) || file.Owed.Any(w => w?.Entry?.Id is not { Length: > 0 })
                || !OwedWhole(bytes))
            {
                return (null, "it is malformed");
            }
            return (file, null);
        }
        // A time the converter cannot read throws as it is, not as a JsonException.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or FormatException or ArgumentException or InvalidOperationException)
        {
            return (null, e.Message);
        }
    }

    // Each owed entry's judgement as written, never a default: one with no time read as MinValue, was
    // let go at once and its worktree with it, rather than the record set aside (Copilot on #813).
    // Since may be absent: a null one is not written.
    static readonly string[] OwedRequired = ["entry", "cause", "at", "decided"];

    static bool OwedWhole(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        if (Find(doc.RootElement, "owed") is not { ValueKind: JsonValueKind.Array } owed) { return true; }
        foreach (var w in owed.EnumerateArray())
        {
            if (w.ValueKind != JsonValueKind.Object || OwedRequired.Any(name => Find(w, name) is not { ValueKind: not JsonValueKind.Null })) { return false; }
        }
        return true;
    }

    // As the record is read: names without regard to case.
    static JsonElement? Find(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object) { return null; }
        foreach (var p in o.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { return p.Value; }
        }
        return null;
    }

    // Written whole and flushed to the disk before it is renamed into place, so a crash or a power cut
    // at any moment leaves either the record before or the record after, never part of one.
    public static void Write(string path, RestoreFile file)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, ProtocolJson.Options);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var partial = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(partial); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // Whether a record set aside is still there. Only "not found" is no: one that cannot be looked
    // at may be there.
    public static bool SetAsideExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path + ".unreadable");
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return true; }
    }

    // Moved aside, not deleted, so a person can see what could not be read; one is kept. A move that
    // fails throws: swallowed, the run went on to write over the record it could not move, and the
    // next start found a readable record and no .unreadable, its worktree protection gone.
    public static void SetAside(string path) => File.Move(path, path + ".unreadable", overwrite: true);
}

// When the machine started. Windows counts its tick from boot, sleep included; Linux says btime in
// /proc/stat. Read against the wall clock, so it moves with a clock change: RestoreRecorder allows
// BootTolerance either way.
public interface IBootClock
{
    DateTimeOffset Boot();
}

public sealed class SystemBootClock(TimeProvider? time = null) : IBootClock
{
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public DateTimeOffset Boot()
    {
        if (OperatingSystem.IsLinux() && LinuxBoot() is { } linux) { return linux; }
        return clock.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    static DateTimeOffset? LinuxBoot()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/stat"))
            {
                if (line.StartsWith("btime ", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out var seconds))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }
}

// Whether the person's logon session began since this host last ran in it, asked once at start. A
// sign-out ends every agent, and so does a Windows shutdown with Fast Startup, which hibernates the
// machine rather than rebooting it, so the boot time stays as it was. Either reads as a reboot.
public interface ILogonMarker
{
    // Whether the marker is missing: only read, never made here.
    bool Fresh();

    // Made once the start that read it fresh has written its record: made before, a crash in between
    // left the old record with the sign-out's evidence gone, so the next start read sessions the
    // shutdown ended as ending normally, and let their worktrees go (Copilot on #813).
    void Commit() { }
}

// A volatile registry key of this profile's, HKCU\Software\AgentBridge\logon-<hash of the profile
// folder>: Windows keeps it in memory only, so it is gone once the user's hive is unloaded at
// sign-out or shutdown. It is only a marker, empty: no value, nothing of the person's. There when
// asked: the same logon. Missing: a fresh one, and it is made for the next start once this one has
// written its record (Commit). Never a guess that it is fresh when it cannot be read: that would
// offer every session that ended normally.
public sealed class VolatileLogonMarker(string profileFolder) : ILogonMarker
{
    const string Parent = @"Software\AgentBridge";
    const string Prefix = "logon-";

    string Name => Prefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profileFolder).ToUpperInvariant())))[..16];

    // At uninstall (the app's BeforeUninstall): every profile's marker, in case one was ever left
    // behind, and the app's key once nothing else is in it.
    public static void RemoveAll()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using (var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Parent, writable: true))
        {
            if (parent is null) { return; }
            foreach (var name in parent.GetSubKeyNames())
            {
                if (name.StartsWith(Prefix, StringComparison.Ordinal)) { parent.DeleteSubKey(name, throwOnMissingSubKey: false); }
            }
            if (parent.SubKeyCount > 0 || parent.ValueCount > 0) { return; }
        }
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
    }

    public bool Fresh()
    {
        if (!OperatingSystem.IsWindows()) { return false; }
        try
        {
            using var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Parent);
            // No key of the app's at all: never made in this logon either.
            if (parent is null) { return true; }
            using var existing = parent.OpenSubKey(Name);
            return existing is null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    public void Commit()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        try
        {
            // The parent made as an ordinary key first: one made volatile could take no ordinary
            // key of the app's beneath it.
            using var parent = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Parent, writable: true);
            using var made = parent.CreateSubKey(Name, writable: true, Microsoft.Win32.RegistryOptions.Volatile);
        }
        // Not made: the next start reads a fresh logon again, and offers what it would after a
        // reboot. Never the other way.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}
// Whether a session that is not running ended normally, as its agent records it: true, false, or
// null when the agent records nothing that could say (a Claude session without a hook
// registration). Never a guess from its absence.
public interface ISessionEnds
{
    bool? Ended(AgentKind agent, string id);
}

// Copilot: its events.jsonl has the main agent's session.shutdown after the last thing the person
// typed, and after the last time it was started or resumed. Claude and Codex: the hook registration (agent-ha-bridge's shape, or this host's intake)
// says Ended, which their SessionEnd hook sets.
public sealed class SessionEnds(string copilotHome, IReadOnlyList<string> claudeRoots, IReadOnlyList<string> codexRoots) : ISessionEnds
{
    // The end of a transcript is where an ending is written; this much of it is read.
    const int Tail = 256 * 1024;

    public static SessionEnds For(SessionSourceOptions options) => new(options.CopilotHome, options.ClaudeRegistrationRoots, options.CodexRegistrationRoots);

    public bool? Ended(AgentKind agent, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || RegistrationDiscovery.SafeKey(id) != id) { return null; }
        return agent switch
        {
            AgentKind.Copilot => CopilotEnded(Path.Combine(copilotHome, "session-state", id, "events.jsonl")),
            AgentKind.Claude => RegistrationEnded(agent, claudeRoots, id),
            AgentKind.Codex => RegistrationEnded(agent, codexRoots, id),
            _ => null,
        };
    }

    internal static bool? CopilotEnded(string path)
    {
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - Tail);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            text = Encoding.UTF8.GetString(buffer);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var ended = false;
        // The first line may be cut by the tail, and fails to parse; it is older than what decides.
        foreach (var line in text.Split('\n'))
        {
            if (!line.Contains("\"user.message\"", StringComparison.Ordinal) && !line.Contains("\"session.shutdown\"", StringComparison.Ordinal)
                && !line.Contains("\"session.resume\"", StringComparison.Ordinal) && !line.Contains("\"session.start\"", StringComparison.Ordinal)) { continue; }
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) { continue; }
                // A sub-agent's prompt or shutdown is not the session's.
                if (e.TryGetProperty("agentId", out var agent) && agent.ValueKind == JsonValueKind.String && agent.GetString() is { Length: > 0 }) { continue; }
                // Resumed after it shut down: a run of its own, which an earlier ending says nothing of.
                if (type.ValueEquals("user.message") || type.ValueEquals("session.resume") || type.ValueEquals("session.start")) { ended = false; }
                else if (type.ValueEquals("session.shutdown")) { ended = true; }
            }
            catch (JsonException) { }
        }
        return ended;
    }

    // As discovery reads them: each root's registration checked as a registration of its own
    // session, and of those in several roots the one written last. One that cannot be read says
    // nothing, rather than an older root's copy saying it ended.
    static bool? RegistrationEnded(AgentKind agent, IReadOnlyList<string> roots, string id)
    {
        Registration? newest = null;
        foreach (var root in roots)
        {
            var registration = RegistrationDiscovery.Parse(agent, Path.Combine(root, RegistrationDiscovery.SafeKey(id) + ".json"), out var why);
            if (registration is null)
            {
                if (why is not null) { return null; }
                continue;
            }
            if (newest is null || (registration.Updated ?? DateTimeOffset.MinValue) > (newest.Updated ?? DateTimeOffset.MinValue)) { newest = registration; }
        }
        return newest?.Ended;
    }
}
