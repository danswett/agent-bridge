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
// sessions not yet read in full). Only this app's timer starts it, or a person's press on another
// of their devices (Ask, below): nothing an agent can reach does.
//
// Asked (#51, docs/protocol/v1-update.md): another of the person's devices pressed Check or
// Install for this machine, and the host carried it here (UpdateReporter). A check is made at
// once. An install is checked first against the same refusals (another profile running from this
// installation, the host gone), then the latest release is found, downloaded and installed by the
// same rules as an automatic one: once the host has been idle for IdleFor, or after WaitAtMost.
// Being asked is what stands in for "Install updates automatically" being on, for that release.
// How it ended (State.Last) is kept in the preferences, so an update that restarts the app is
// still reported, by the new version, with the press it answers.
public sealed class AutoUpdater
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan WaitAtMost = TimeSpan.FromHours(6);
    // How long the host must have been idle, seen at every tick and every session change, before
    // it counts: long past the moment a snapshot is replaced (sessions.subscribe again after a gap),
    // whose upserts arrive within seconds even if an earlier sessions.synced closed it early.
    public static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(2);

    // Shared with Settings, whose checks and downloads it reports too.
    readonly ObservedUpdater updater;
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
    int again;
    // Presses from other devices being carried out, and whether the install one has been checked
    // against the refusals yet.
    readonly object asks = new();
    string? checkRequest;
    string? installRequest;
    string? installVetted;
    volatile bool checkNow;
    // From stopping the host for an install until it fails (success ends the app).
    volatile bool installing;

    // othersInPackage(this host's pid): another profile's app or host running from this
    // installation, which an update would stop; null when there is none. updater: the app's
    // ObservedUpdater, which Settings uses too, so what Settings does is reported as well.
    public AutoUpdater(IUpdater updater, HostConnection host, HostManager manager, Func<AppPreferences> prefs, Action<AppPreferences> save, Action<string> log,
        Func<int?, string?> othersInPackage, TimeProvider? time = null)
    {
        this.updater = updater as ObservedUpdater ?? new ObservedUpdater(updater, time);
        this.updater.Changed += Changed;
        this.host = host;
        this.manager = manager;
        this.prefs = prefs;
        this.save = save;
        this.log = log;
        this.othersInPackage = othersInPackage;
        this.time = time ?? TimeProvider.System;
        // A turn starting or a question arriving between ticks starts the idle wait again; so does a
        // snapshot read in full again (a reconnect, a gap), so idle seen before it does not count.
        // Nothing installs while one is being read (Due), and every reconnect ends in one.
        host.Sessions.Upserted += s => { if (Busy(s)) { idleSince = null; } };
        host.Sessions.Synced += () => idleSince = null;
    }

    // The release found and downloaded, waiting to be installed.
    public string? Pending { get; private set; }

    // State changed: what UpdateReporter sends the host for the account's other devices.
    public event Action? StateChanged;

    public const string NotInstalled = "this copy of the app was not installed by its updater, so it cannot update";

    // What this machine's updater is doing, for the account's other devices.
    public UpdateState State
    {
        get
        {
            var installed = updater.InstalledVersion;
            string? request;
            lock (asks) { request = installRequest ?? checkRequest; }
            var now = installing ? UpdatePhase.Installing : updater.Phase;
            // A release downloaded that will install by itself, or because a device asked.
            var waiting = now == UpdatePhase.Idle && Pending is not null && installed is not null && (prefs().AutoUpdate || installRequest is not null);
            // What the last check found, from here or from Settings: still there when its download
            // failed, and gone when a later one found nothing newer.
            var available = updater.Found;
            return UpdateProtocol.Fit(new UpdateState
            {
                Current = installed,
                Installer = installed is not null,
                Unavailable = installed is null ? NotInstalled : null,
                Available = available,
                Notes = available is not null ? updater.PendingNotes : null,
                CheckedAt = updater.CheckedAt,
                Phase = waiting ? UpdatePhase.Waiting : now,
                Progress = now == UpdatePhase.Downloading ? updater.Progress : null,
                Waiting = waiting ? WaitingFor() : null,
                Request = request,
                Automatic = prefs().AutoUpdate,
                Last = prefs().LastUpdate,
            });
        }
    }

    string WaitingFor()
    {
        var until = $"; it installs anyway at {(foundAt + WaitAtMost).ToLocalTime():g}";
        if (lastWhyNot is { } why) { return why; }
        return Idle ? $"no session has been busy for {IdleFor.TotalMinutes:0} minutes yet{until}"
            : $"a session on this machine has a turn running or a question waiting{until}";
    }

    // A press from another device (UpdateReporter): Check, or Install now. A newer press of the
    // same kind takes over from an older one, whose device then sees another request under way.
    // The tick it starts, which is already done if one was running (that one runs again for it).
    public Task Ask(UpdateAskKind kind, string request)
    {
        lock (asks)
        {
            if (kind == UpdateAskKind.Install) { installRequest = request; installVetted = null; }
            else { checkRequest = request; }
        }
        log($"update: {(kind == UpdateAskKind.Install ? "install" : "check")} asked for");
        checkNow = true;
        Changed();
        return TickAsync();
    }

    void Changed() => StateChanged?.Invoke();

    // Ends a press, the one that was being carried out (a newer one that came meanwhile is left
    // for the next tick): how it went, kept for the devices, and across the restart an update makes.
    void Finish(ref string? field, string? request, UpdateOutcome outcome, string? reason, string? to = null)
    {
        if (request is null) { return; }
        lock (asks)
        {
            if (field != request) { return; }
            field = null;
        }
        log($"update: asked {outcome.ToString().ToLowerInvariant()}{(reason is null ? "" : $": {reason}")}");
        Remember(outcome, reason, to, request);
    }

    void Remember(UpdateOutcome outcome, string? reason, string? to, string? request)
    {
        save(prefs() with
        {
            LastUpdate = new UpdateResult { Outcome = outcome, From = updater.InstalledVersion, To = to, Reason = reason, Request = request, At = time.GetUtcNow() },
        });
        Changed();
    }

    // Why nothing may be installed here, as the host last said, or null when it may. Asked: a
    // person pressed Install on another device, which stands in for the switch.
    public string? WhyNot(bool asked = false)
    {
        if (!asked && !prefs().AutoUpdate) { return "Install updates automatically is off"; }
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

    // Idle now and for IdleFor without a break, or waited six hours. Either way only over a whole
    // snapshot: six hours on, an update still waits out a reconnect's sessions being read again.
    bool Due(DateTimeOffset now)
    {
        if (!Idle) { idleSince = null; }
        else { idleSince ??= now; }
        if (!host.Sessions.Complete) { return false; }
        return (idleSince is { } since && now - since >= IdleFor) || now - foundAt >= WaitAtMost;
    }

    string? Asked(UpdateAskKind kind)
    {
        lock (asks) { return kind == UpdateAskKind.Install ? installRequest : checkRequest; }
    }

    // The app calls this on start and every minute, and a press from another device at once.
    // Checks when one is due, and installs when it may. A press arriving while a tick runs gets
    // another straight after it.
    public async Task TickAsync()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) { Volatile.Write(ref again, 1); return; }
        try
        {
            do
            {
                Volatile.Write(ref again, 0);
                await TickOnceAsync();
            }
            while (Volatile.Read(ref again) == 1);
        }
        finally { Volatile.Write(ref busy, 0); }
        // One that arrived between the last look and letting go.
        if (Volatile.Read(ref again) == 1) { await TickAsync(); }
    }

    async Task TickOnceAsync()
    {
        try
        {
            if (updater.InstalledVersion is null)
            {
                Finish(ref installRequest, Asked(UpdateAskKind.Install), UpdateOutcome.Refused, NotInstalled);
                Finish(ref checkRequest, Asked(UpdateAskKind.Check), UpdateOutcome.Refused, NotInstalled);
            }
            // An install asked for is refused now, rather than after the download and the wait,
            // if it could never go ahead: the host gone, or another profile running from here.
            if (Asked(UpdateAskKind.Install) is { } install && installVetted != install)
            {
                installVetted = install;
                if (await WhyNotNowAsync(asked: true) is { } refused) { Finish(ref installRequest, install, UpdateOutcome.Refused, refused); }
                // Another press came while this one was looked at: it is looked at first, by the
                // tick queued for it, before anything is downloaded for it.
                if (Asked(UpdateAskKind.Install) is { } replaced && replaced != install) { return; }
            }
            var asked = Asked(UpdateAskKind.Install) is not null;
            var why = WhyNot(asked);
            // Nothing installs, and a check is made only when a device asked for one. Said to the
            // devices too: the switch turned off while a release waited leaves it to install when
            // asked, no longer waiting by itself.
            if (why is not null && Asked(UpdateAskKind.Check) is null) { Refused(why); idleSince = null; Changed(); return; }
            var now = time.GetUtcNow();
            Due(now);
            if (checkNow || checkedAt is null || now - checkedAt >= CheckEvery)
            {
                checkNow = false;
                checkedAt = now;
                await CheckAsync(now);
            }
            if (Refused(why)) { idleSince = null; Changed(); return; }
            if (Pending is null || !Due(time.GetUtcNow())) { Changed(); return; }
            // Settings shares the updater: a check there, or a change of feed, may have replaced
            // the release downloaded here. Then it is found and downloaded again first.
            if (Stale()) { return; }
            // Asked again now: the host still there (its own status, not the last report) and no
            // other profile's app or host running from the package.
            var pressed = Asked(UpdateAskKind.Install);
            if (Refused(await WhyNotNowAsync(pressed is not null)))
            {
                Finish(ref installRequest, pressed, UpdateOutcome.Refused, lastWhyNot);
                return;
            }
            // Those took time: the switch turned off, or the host gone, meanwhile, counts too.
            asked = Asked(UpdateAskKind.Install) is not null;
            if (Refused(WhyNot(asked)) || !Due(time.GetUtcNow())) { return; }
            // And Settings may have checked, or changed the feed, meanwhile (WhyNot itself reads the
            // feed): still the release downloaded, or nothing is stopped. Should it change even
            // while the host stops, ApplyAndRestart refuses it and the host is started again.
            if (Stale()) { return; }
            await InstallAsync(asked ? "another device asked for it and no session is busy"
                : idleSince is not null ? "no session is busy" : $"it has waited {WaitAtMost.TotalHours:0} hours");
        }
        // A check that cannot reach the network, a download cut off: tried again at the next check.
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            log($"auto-update: {e.Message}");
            checkedAt = time.GetUtcNow();
            installing = false;
            Changed();
        }
    }

    async Task CheckAsync(DateTimeOffset now)
    {
        // The presses this check answers; any that come while it runs get a check of their own.
        var check = Asked(UpdateAskKind.Check);
        var install = Asked(UpdateAskKind.Install);
        string? found;
        try
        {
            found = await updater.CheckAsync();
            if (found is not null && updater.Ready != found) { await updater.DownloadAsync(_ => { }); }
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            Finish(ref checkRequest, check, UpdateOutcome.Failed, e.Message);
            Finish(ref installRequest, install, UpdateOutcome.Failed, e.Message);
            throw;
        }
        if (found is not null)
        {
            if (found != Pending)
            {
                // The wait runs from the first release found, so newer ones arriving every
                // half hour cannot put an update off for good.
                if (Pending is null) { foundAt = now; }
                Pending = found;
                log($"auto-update: {found} downloaded; installing when no session is busy, or at {(foundAt + WaitAtMost).ToLocalTime():g}");
            }
        }
        // A check asked for ends here: what it found is in the state (Available). Nothing newer
        // ends an install asked for too, and a release found before (on a feed since changed, or
        // a pre-release since turned off) is no longer one to offer.
        if (found is null)
        {
            Pending = null;
            Finish(ref checkRequest, check, UpdateOutcome.Current, null);
            Finish(ref installRequest, install, UpdateOutcome.Current, null);
        }
        else
        {
            lock (asks) { if (checkRequest == check) { checkRequest = null; } }
        }
        Changed();
    }

    async Task<string?> WhyNotNowAsync(bool asked = false)
    {
        if (WhyNot(asked) is { } why) { return why; }
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
        var request = Asked(UpdateAskKind.Install);
        log($"auto-update: installing {to}, as {because}");
        installing = true;
        Changed();
        // The host comes back from a failed install, or from the app quitting mid-way (HostManager).
        try { await manager.StopAndApplyAsync(() => Record(prefs, save, updater, request), updater.ApplyAndRestart); }
        // Settings' Update and restart got there first: that one is installing, and recorded it.
        catch (UpdateUnderWayException e)
        {
            log($"auto-update: not installing {to}: {e.Message}");
            installing = false;
            Finish(ref installRequest, request, UpdateOutcome.Refused, "an update is already being installed from this machine's Settings", to);
            Changed();
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // Still this version: nothing to announce. Tried again only after the next check finds
            // and downloads a release, not every minute.
            save(prefs() with { UpdatedFrom = null, UpdateNotes = null, UpdateRequest = null });
            Pending = null;
            checkedAt = time.GetUtcNow();
            log($"auto-update: installing {to} failed: {e.Message}");
            installing = false;
            // Kept for the devices whether a device asked for it or the app installed it unasked:
            // the old release still runs, and no later start would say so.
            if (request is null) { Remember(UpdateOutcome.Failed, e.Message, to, null); }
            else { Finish(ref installRequest, request, UpdateOutcome.Failed, e.Message, to); }
        }
    }

    // Before an update is applied, by the app or from Settings: what the new version's first start
    // announces, and the press from another device it answers, if one asked for it.
    public static void Record(Func<AppPreferences> prefs, Action<AppPreferences> save, IUpdater updater, string? request = null) =>
        save(prefs() with { UpdatedFrom = updater.InstalledVersion, UpdateNotes = updater.PendingNotes, UpdateRequest = request });

    // The first start after an update: a toast saying what changed, once, and how it went kept for
    // the account's other devices (State.Last), the press it answers with it.
    public static void Announce(Func<AppPreferences> prefs, Action<AppPreferences> save, IUpdater updater, IToastSink sink, Action<string> log,
        TimeProvider? time = null)
    {
        if (prefs().UpdatedFrom is not { } from) { return; }
        var notes = prefs().UpdateNotes;
        var now = updater.InstalledVersion;
        var took = now is not null && now != from;
        save(prefs() with
        {
            UpdatedFrom = null,
            UpdateNotes = null,
            UpdateRequest = null,
            LastUpdate = new UpdateResult
            {
                Outcome = took ? UpdateOutcome.Updated : UpdateOutcome.Failed,
                From = from,
                To = now,
                Reason = took ? null : $"the update did not take: still {now ?? "not installed"}",
                Request = prefs().UpdateRequest,
                At = (time ?? TimeProvider.System).GetUtcNow(),
            },
        });
        if (!took)
        {
            log($"the update from {from} did not take: still {now ?? "not installed"}");
            return;
        }
        log($"updated from {from} to {now}");
        if (prefs().Toasts) { sink.Show(new ToastNotice("agent-bridge-updated", ToastXml.Updated(now!, from, notes), Quiet: true)); }
    }
}
