using System.Diagnostics;
using AgentBridge.Host.Core.Updates;

namespace AgentBridge.Host;

public enum HeadlessStartState { Started, NotWindows, Disabled, NothingOffered, NoMinimumApp, AppTooOld, Held, AppRunning, NoApp, Backoff, Failed }

public sealed record HeadlessStart(HeadlessStartState State, string? Detail = null)
{
    public bool Started => State == HeadlessStartState.Started;
}

// Starts the app's process; throws when it cannot.
public interface IAppStarter
{
    void Start(string exe, IReadOnlyList<string> arguments);
}

public sealed record HeadlessAppUpdateOptions
{
    public required string Profile { get; init; }
    // update.startAppToInstall (the setting itself is the next slice).
    public bool Enabled { get; init; }
    // The first app release that knows --update-only (#3810). Null: not known yet, so it never starts: an app that predates the
    // flag ignores it and would open as a full, visible app.
    public string? MinimumApp { get; init; }
    public Func<string?> InstalledApp { get; init; } = () => null;
    public Func<string?> AppExe { get; init; } = () => null;
    // Whether the app's single-instance lock is held (the app or an update-only run); null when it cannot be read.
    public Func<bool?> AppLocked { get; init; } = () => false;
    // Only when the lock cannot be read: whether the app's process is running.
    public Func<bool>? AppProcessRunning { get; init; }
    // True for a release the bad-release policy holds, or whose policy is unknown.
    public Func<string, bool>? Held { get; init; }
    public bool Windows { get; init; } = OperatingSystem.IsWindows();
    public TimeSpan StartEvery { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan FirstBackoff { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan LongestBackoff { get; init; } = TimeSpan.FromHours(6);
}

// The host's half of the headless fleet update (docs/protocol/v1-update.md, "The update-only run"; #3810 is the app's half, #2869).
// A machine whose app is closed never updates, because the app installs updates and the host alone cannot. When a release is offered
// and the app is closed, this starts `AgentBridge.exe --update-only --profile <p>`, as this host's own user, once. It never starts:
//   * an app that predates the flag (that would open a full visible app): MinimumApp must be known and the installed app at or above it;
//   * while the app or an earlier update-only run holds the app's single-instance lock (Local\AgentBridgeApp.<profile>, src/AgentBridge.App/
//     Program.cs, which the update-only run claims too): one check covers both, and a process scan is only the fallback when the lock
//     cannot be read;
//   * for a release the bad-release policy holds;
//   * faster than StartEvery, and after a failed start only after a backoff that doubles up to LongestBackoff.
// It adds no way in: there is no op or endpoint, the caller is the host's own timer (the next slice), and it never throws.
public sealed class HeadlessAppUpdate(HeadlessAppUpdateOptions options, IAppStarter starter, TimeProvider? time = null, Action<string>? log = null)
{
    readonly HeadlessAppUpdateOptions options = options;
    readonly IAppStarter starter = starter;
    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly Action<string>? log = log;
    readonly object gate = new();
    DateTimeOffset nextAllowed = DateTimeOffset.MinValue;
    int failures;

    public HeadlessStart TryStart(UpdateRelease? offered) => TryStart(offered, false);

    // held: the caller's own answer for the bad-release policy (it is read asynchronously, so it is decided before this is called): true for a
    // release the policy halts or whose policy is unknown or stale.
    public HeadlessStart TryStart(UpdateRelease? offered, bool held)
    {
        if (!options.Windows) { return new(HeadlessStartState.NotWindows); }
        if (!options.Enabled) { return new(HeadlessStartState.Disabled); }
        if (offered is null) { return new(HeadlessStartState.NothingOffered); }
        if (!TryVersion(options.MinimumApp, out var minimum)) { return new(HeadlessStartState.NoMinimumApp); }
        if (!TryVersion(SafeInstalled(), out var installed) || installed < minimum) { return new(HeadlessStartState.AppTooOld, "the installed app may not know --update-only"); }
        if (held || options.Held?.Invoke(offered.Version) == true) { return new(HeadlessStartState.Held, offered.Version); }
        if (Running()) { return new(HeadlessStartState.AppRunning); }
        if (SafeExe() is not { Length: > 0 } exe) { return new(HeadlessStartState.NoApp); }
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (now < nextAllowed) { return new(HeadlessStartState.Backoff, $"until {nextAllowed:O}"); }
            try
            {
                starter.Start(exe, ["--update-only", "--profile", options.Profile]);
                failures = 0;
                nextAllowed = now + options.StartEvery;
                log?.Invoke($"headless update: started the app to install {offered.Version}");
                return new(HeadlessStartState.Started);
            }
            catch (Exception e)
            {
                failures++;
                var backoff = TimeSpan.FromTicks(Math.Min(options.LongestBackoff.Ticks, options.FirstBackoff.Ticks << Math.Min(failures - 1, 16)));
                nextAllowed = now + backoff;
                Say($"headless update: could not start the app ({e.GetType().Name}); next try after {backoff}");
                return new(HeadlessStartState.Failed, e.GetType().Name);
            }
        }
    }

    // The app's single-instance lock is the check; the scan runs only when the lock cannot be read, and without one it cannot tell, so
    // it does not start.
    bool Running()
    {
        bool? locked;
        try { locked = options.AppLocked(); } catch (Exception) { locked = null; }
        if (locked is { } held) { return held; }
        try { return options.AppProcessRunning?.Invoke() ?? true; } catch (Exception) { return true; }
    }

    string? SafeInstalled() { try { return options.InstalledApp(); } catch (Exception) { return null; } }

    string? SafeExe() { try { return options.AppExe(); } catch (Exception) { return null; } }

    void Say(string line) { try { log?.Invoke(line); } catch (Exception) { } }

    // 0.1.N, with a leading v or a pre-release/build suffix taken off; anything else is unknown, which never starts.
    static bool TryVersion(string? text, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(text)) { return false; }
        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var parsed) && (version = parsed) is not null;
    }
}

// The app's single-instance lock, by its name (src/AgentBridge.App/Program.cs SingleInstance: Local\AgentBridgeApp.<profile>). Held by
// the app and by an update-only run alike. Windows only; null when it cannot be read (another account's, or not Windows).
public static class AppSingleInstance
{
    public static bool? Held(string profile)
    {
        if (!OperatingSystem.IsWindows() || profile.Length == 0 || profile.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))) { return null; }
        try
        {
            if (!Mutex.TryOpenExisting($@"Local\AgentBridgeApp.{profile}", out var existing)) { return false; }
            existing.Dispose();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException) { return null; }
    }
}

public sealed class ProcessAppStarter : IAppStarter
{
    public void Start(string exe, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
        foreach (var argument in arguments) { info.ArgumentList.Add(argument); }
        using var process = Process.Start(info) ?? throw new IOException("the app did not start");
    }
}
