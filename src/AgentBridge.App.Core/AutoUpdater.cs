using AgentBridge.Local;
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
// A read-only host (delivery.readOnly) and one beside a running agent-ha-bridge (G5) update too:
// those rules guard what other devices may do to this machine (the Home Assistant connector's
// Install button among them), and replacing this app and its host touches neither the sessions'
// consoles nor agent-ha-bridge. Never while another profile's app or host runs from this
// installation (Velopack stops every process in the package, busy or not, and restarts only this
// profile), or while the host cannot say what its sessions are doing (not running, or its
// sessions not yet read in full). Only this app's timer starts it: nothing an agent can reach
// does.
public sealed class AutoUpdater
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan WaitAtMost = TimeSpan.FromHours(6);
    // How long the host must have been idle, seen at every tick and every session change, before
    // it counts: long past the moment a snapshot is replaced (sessions.subscribe again after a gap),
    // whose upserts arrive within seconds even if an earlier sessions.synced closed it early.
    public static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(2);

    readonly IUpdater updater;
    readonly HostConnection host;
    readonly HostManager manager;
    readonly Func<AppPreferences> prefs;
    readonly Action<AppPreferences> save;
    readonly Action<string> log;
    readonly Func<int?, string?> othersInPackage;
    readonly TimeProvider time;
    DateTimeOffset? checkedAt;
    DateTimeOffset foundAt;
    DateTimeOffset? idleSince;
    string? lastWhyNot;
    int busy;

    // othersInPackage(this host's pid): another profile's app or host running from this
    // installation, which an update would stop; null when there is none.
    public AutoUpdater(IUpdater updater, HostConnection host, HostManager manager, Func<AppPreferences> prefs, Action<AppPreferences> save, Action<string> log,
        Func<int?, string?> othersInPackage, TimeProvider? time = null)
    {
        this.updater = updater;
        this.host = host;
        this.manager = manager;
        this.prefs = prefs;
        this.save = save;
        this.log = log;
        this.othersInPackage = othersInPackage;
        this.time = time ?? TimeProvider.System;
        // A turn starting or a question arriving between ticks starts the idle wait again.
        host.Sessions.Upserted += s => { if (Busy(s)) { idleSince = null; } };
    }

    // The release found and downloaded, waiting to be installed.
    public string? Pending { get; private set; }

    // Why nothing may be installed here, as the host last said, or null when it may.
    public string? WhyNot()
    {
        if (!prefs().AutoUpdate) { return "Install updates automatically is off"; }
        if (updater.InstalledVersion is null) { return "this is a development copy"; }
        if (host.State != LinkState.Connected || host.Status is null)
        {
            return "the host is not running, so whether its sessions are busy cannot be told";
        }
        return null;
    }

    static bool Busy(Session s) => s.Question is not null || s.Status is SessionStatus.Working or SessionStatus.Waiting;

    // No turn running and no question waiting on this machine's host, as its whole snapshot says:
    // a list still being read may be missing the busy session. Another machine's sessions are its
    // own host's: an update here restarts only this one.
    public bool Idle => host.Sessions.Complete && !host.Sessions.All.Any(Busy);

    // Idle now and for IdleFor without a break, or waited six hours.
    bool Due(DateTimeOffset now)
    {
        if (!Idle) { idleSince = null; }
        else { idleSince ??= now; }
        return (idleSince is { } since && now - since >= IdleFor) || now - foundAt >= WaitAtMost;
    }

    // The app calls this on start and every minute. Checks when one is due, and installs when it may.
    public async Task TickAsync()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) { return; }
        try
        {
            if (Refused(WhyNot())) { idleSince = null; return; }
            var now = time.GetUtcNow();
            Due(now);
            if (checkedAt is null || now - checkedAt >= CheckEvery)
            {
                checkedAt = now;
                if (await updater.CheckAsync() is { } found)
                {
                    if (updater.Ready != found) { await updater.DownloadAsync(_ => { }); }
                    if (found != Pending)
                    {
                        // The wait runs from the first release found, so newer ones arriving every
                        // half hour cannot put an update off for good.
                        if (Pending is null) { foundAt = now; }
                        Pending = found;
                        log($"auto-update: {found} downloaded; installing when no session is busy, or at {(foundAt + WaitAtMost).ToLocalTime():g}");
                    }
                }
            }
            if (Pending is null || !Due(time.GetUtcNow())) { return; }
            // Settings shares the updater: a check there, or a change of feed, may have replaced
            // the release downloaded here. Then it is found and downloaded again first.
            if (Stale()) { return; }
            // Asked again now: the host still there (its own status, not the last report) and no
            // other profile's app or host running from the package.
            if (Refused(await WhyNotNowAsync())) { return; }
            // Those took time: the switch turned off, or the host gone, meanwhile, counts too.
            if (Refused(WhyNot()) || !Due(time.GetUtcNow())) { return; }
            // And Settings may have checked, or changed the feed, meanwhile (WhyNot itself reads the
            // feed): still the release downloaded, or nothing is stopped. Should it change even
            // while the host stops, ApplyAndRestart refuses it and the host is started again.
            if (Stale()) { return; }
            await InstallAsync(idleSince is not null ? "no session is busy" : $"it has waited {WaitAtMost.TotalHours:0} hours");
        }
        // A check that cannot reach the network, a download cut off: tried again at the next check.
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            log($"auto-update: {e.Message}");
            checkedAt = time.GetUtcNow();
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    async Task<string?> WhyNotNowAsync()
    {
        if (WhyNot() is { } why) { return why; }
        HostStatus fresh;
        try { fresh = await host.CallAsync<HostStatus>(HostOps.Status, timeout: TimeSpan.FromSeconds(10)); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            return $"the host's status could not be read: {e.Message}";
        }
        if (await Task.Run(() => othersInPackage(fresh.ProcessId)) is { } others) { return others; }
        return null;
    }

    bool Stale()
    {
        if (updater.Ready == Pending) { return false; }
        log($"auto-update: {Pending} is no longer the release downloaded; checking again");
        Pending = null;
        checkedAt = null;
        return true;
    }

    bool Refused(string? why)
    {
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
            // Marked first: should the app quit before the update is applied, it starts the host
            // again on the way out (and its next start would anyway).
            manager.StoppedForUpdate = true;
            await manager.StopAsync();
            updater.ApplyAndRestart();
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // Still this version: nothing to announce, and the host it stopped comes back. Tried
            // again only after the next check finds and downloads a release, not every minute.
            save(prefs() with { UpdatedFrom = null, UpdateNotes = null });
            Pending = null;
            checkedAt = time.GetUtcNow();
            log($"auto-update: installing {to} failed: {e.Message}");
            try { await manager.StartAsync(); }
            catch (Exception again) when (again is not (OutOfMemoryException or StackOverflowException)) { log($"auto-update: the host did not start again: {again.Message}"); }
            manager.StoppedForUpdate = false;
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
