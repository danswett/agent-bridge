using System.Diagnostics;
using AgentBridge.Host.Core.Updates;

namespace AgentBridge.Host;

// H2b (#2869): the host's small timer that feeds HeadlessAppUpdate. A machine whose app is closed never updates, because the app
// installs updates and the host alone cannot; this is the host noticing a release and starting the app headless for it
// (docs/protocol/v1-update.md, "The update-only run"; docs/design/updates-fast-fleet-update.md).
//
// Every Every (30 minutes, the app's own interval), after FirstAfter, it asks the host's EXISTING update check, the one Home Assistant's
// Check uses (IHostUpdates.CheckAsync), in its background mode, never "pressed": ReleaseDiscovery's shared budget, backoff and cached
// list apply, so it makes no GitHub call of its own inside their interval. It adds no op, endpoint or remote way in: only this timer
// calls it. A tick that is still running is not overlapped, and nothing a tick does can throw out of it.
//
// The bad-release policy is read through the host's own shared reader before a release is acted on: a halted release, or a policy that is
// unknown or stale, holds it (the app's gate says the same, but this keeps the host from starting the app every interval for nothing).
public sealed class HeadlessUpdateTimer : IDisposable
{
    public static readonly TimeSpan FirstAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    // The first app release that knows --update-only (#3810). NULL until a release carries it, so the shipped timer runs but never starts the
    // app: an app that predates the flag ignores it and would open as a full, visible app. Set this to that release's version once it is
    // published (a one-line change, in its own PR).
    public static readonly string? MinimumApp = null;

    readonly IHostUpdates updates;
    readonly HeadlessAppUpdate headless;
    readonly Func<UpdateRelease, CancellationToken, Task<bool>> held;
    readonly TimeProvider time;
    readonly Action<string>? log;
    readonly CancellationTokenSource stopped = new();
    int running;
    ITimer? timer;

    internal HeadlessUpdateTimer(IHostUpdates updates, HeadlessAppUpdate headless, Func<UpdateRelease, CancellationToken, Task<bool>> held, TimeProvider time, Action<string>? log)
    {
        this.updates = updates;
        this.headless = headless;
        this.held = held;
        this.time = time;
        this.log = log;
    }

    // Null (and nothing runs) when the setting is off, there is no updater (a read-only host, a build folder, not Windows), or this is not
    // Windows. options and starter are for tests; the real ones are the app's own lock and a real process start.
    public static HeadlessUpdateTimer? Start(bool enabled, IHostUpdates? updates, string profile, Func<CancellationToken, Task<HaltPolicySnapshot>> readPolicy,
        Action<string>? log = null, TimeProvider? time = null, HeadlessAppUpdateOptions? options = null, IAppStarter? starter = null)
    {
        options ??= new HeadlessAppUpdateOptions
        {
            Profile = profile,
            Enabled = true,
            MinimumApp = MinimumApp,
            InstalledApp = () => updates?.Installed,
            AppExe = AppExe,
            AppLocked = () => AppSingleInstance.Held(profile),
            AppProcessRunning = () => Process.GetProcessesByName("AgentBridge").Length > 0,
        };
        if (!enabled || updates is null || !options.Windows || !options.Enabled) { return null; }
        time ??= TimeProvider.System;
        var timer = new HeadlessUpdateTimer(updates, new HeadlessAppUpdate(options, starter ?? new ProcessAppStarter(), time, log),
            async (release, ct) => Held(await readPolicy(ct), release.Version), time, log);
        timer.Begin();
        return timer;
    }

    // The app beside this host in the same package (docs/app-windows.md): the host is published inside the app's Velopack package.
    static string? AppExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "AgentBridge.exe");
        return File.Exists(exe) ? exe : null;
    }

    internal void Begin() => timer = time.CreateTimer(_ => _ = TickAsync(stopped.Token), null, FirstAfter, Every);

    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) { return; }
        try
        {
            var offered = await updates.CheckAsync(cancellationToken, pressed: false);
            var isHeld = offered is not null && await held(offered, cancellationToken);
            var result = headless.TryStart(offered, isHeld);
            if (result.State is not (HeadlessStartState.NothingOffered or HeadlessStartState.Backoff)) { log?.Invoke($"headless update: {result.State}{(result.Detail is null ? "" : $" ({result.Detail})")}"); }
        }
        catch (Exception e)
        {
            try { log?.Invoke($"headless update: the check could not run ({e.GetType().Name}); nothing started"); } catch (Exception) { }
        }
        finally { Volatile.Write(ref running, 0); }
    }

    // Held unless the policy is fresh and does not name the release: an unknown or stale policy holds, as the app's own gate does.
    public static bool Held(HaltPolicySnapshot policy, string version) =>
        !policy.Fresh || policy.Policy is null || policy.Policy.Halted.Contains(version, StringComparer.Ordinal);

    public void Dispose()
    {
        stopped.Cancel();
        timer?.Dispose();
    }
}
