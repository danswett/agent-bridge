using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// Installs updates without being asked: Settings, "Install updates automatically", on by default,
// per profile beside "Include pre-releases". Checks on start and every half hour; a release it
// finds is downloaded at once and installed when this machine's host is idle (no session with a
// turn running or a question waiting), or after six hours whatever it is doing, by the same path
// as Settings' "Update and restart": the host stopped over the pipe, then Velopack's
// ApplyUpdatesAndRestart, whose new version starts the host again. The new version's first start
// shows a toast saying what changed (Announce).
//
// Never for a read-only host (delivery.readOnly, which may be driven from no other device) or one
// whose machine agent-ha-bridge owns (G5): those are the person's to update from Settings. And
// never when the host cannot say which it is (not running). Only this app's timer starts it:
// nothing an agent can reach does.
public sealed class AutoUpdater
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan WaitAtMost = TimeSpan.FromHours(6);

    readonly IUpdater updater;
    readonly HostConnection host;
    readonly HostManager manager;
    readonly Func<AppPreferences> prefs;
    readonly Action<AppPreferences> save;
    readonly TimeProvider time;
    readonly Action<string> log;
    DateTimeOffset? checkedAt;
    DateTimeOffset foundAt;
    string? lastWhyNot;
    int busy;

    public AutoUpdater(IUpdater updater, HostConnection host, HostManager manager, Func<AppPreferences> prefs, Action<AppPreferences> save, Action<string> log, TimeProvider? time = null)
    {
        this.updater = updater;
        this.host = host;
        this.manager = manager;
        this.prefs = prefs;
        this.save = save;
        this.log = log;
        this.time = time ?? TimeProvider.System;
    }

    // The release downloaded and waiting to be installed.
    public string? Pending { get; private set; }

    // Why nothing may be installed here now, or null when it may.
    public string? WhyNot()
    {
        if (!prefs().AutoUpdate) { return "Install updates automatically is off"; }
        if (updater.InstalledVersion is null) { return "this is a development copy"; }
        if (host.State != LinkState.Connected || host.Status is not { } status)
        {
            return "the host is not running, so whether it is read-only or agent-ha-bridge's cannot be told";
        }
        if (status.Machine.ReadOnly is { } readOnly) { return $"the host is read-only ({readOnly})"; }
        if (status.AgentHaBridge is { } running) { return $"agent-ha-bridge runs for this user (G5): {running}"; }
        return null;
    }

    // No turn running and no question waiting on this machine's host. Another machine's sessions
    // are their own host's: an update here restarts only this one.
    public bool Idle => host.Sessions.All.All(s => s.Question is null && s.Status is not (SessionStatus.Working or SessionStatus.Waiting));

    // The app calls this on start and every minute. Checks when one is due, and installs when it may.
    public async Task TickAsync()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) { return; }
        try
        {
            if (Refused()) { return; }
            var now = time.GetUtcNow();
            if (checkedAt is null || now - checkedAt >= CheckEvery)
            {
                checkedAt = now;
                if (await updater.CheckAsync() is { } found && found != Pending)
                {
                    await updater.DownloadAsync(_ => { });
                    // The wait runs from the first release found, so newer ones arriving every
                    // half hour cannot put an update off for good.
                    if (Pending is null) { foundAt = now; }
                    Pending = found;
                    log($"auto-update: {found} downloaded; installing when no session is busy, or at {(foundAt + WaitAtMost).ToLocalTime():g}");
                }
            }
            if (Pending is null) { return; }
            if (!Idle && now - foundAt < WaitAtMost) { return; }
            // The download and the check took time, during which the host may have changed.
            if (Refused()) { return; }
            await InstallAsync(Idle ? "no session is busy" : $"it has waited {WaitAtMost.TotalHours:0} hours");
        }
        // A check that cannot reach the network, a download cut off: tried again at the next check.
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            log($"auto-update: {e.Message}");
            checkedAt = time.GetUtcNow();
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    bool Refused()
    {
        var why = WhyNot();
        if (why != lastWhyNot && why is not null) { log($"auto-update: not installing updates: {why}"); }
        lastWhyNot = why;
        return why is not null;
    }

    async Task InstallAsync(string because)
    {
        var to = Pending!;
        log($"auto-update: installing {to}, as {because}");
        Record(prefs, save, updater);
        try
        {
            await manager.StopAsync();
            updater.ApplyAndRestart();
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // Still this version: nothing to announce, and the host it stopped comes back.
            save(prefs() with { UpdatedFrom = null, UpdateNotes = null });
            log($"auto-update: installing {to} failed: {e.Message}");
            try { await manager.StartAsync(); }
            catch (Exception again) when (again is not (OutOfMemoryException or StackOverflowException)) { log($"auto-update: the host did not start again: {again.Message}"); }
        }
    }

    // Before an update is applied, by the app or from Settings: what the new version's first start
    // announces.
    public static void Record(Func<AppPreferences> prefs, Action<AppPreferences> save, IUpdater updater) =>
        save(prefs() with { UpdatedFrom = updater.InstalledVersion, UpdateNotes = updater.PendingNotes });

    // The first start after an update: a toast saying what changed, once.
    public static void Announce(Func<AppPreferences> prefs, Action<AppPreferences> save, IUpdater updater, IToastSink sink, Action<string> log)
    {
        if (prefs().UpdatedFrom is not { } from) { return; }
        var notes = prefs().UpdateNotes;
        save(prefs() with { UpdatedFrom = null, UpdateNotes = null });
        var now = updater.InstalledVersion;
        if (now is null || now == from)
        {
            log($"the update from {from} did not take: still {now ?? "not installed"}");
            return;
        }
        log($"updated from {from} to {now}");
        if (prefs().Toasts) { sink.Show(new ToastNotice("agent-bridge-updated", ToastXml.Updated(now, from, notes), Quiet: true)); }
    }
}
