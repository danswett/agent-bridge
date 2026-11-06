using AgentBridge.Host.Core.Updates;
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
    // A press within this of the last check is answered from it, not by asking the feed again,
    // so a device pressing Check over and over costs the feed nothing.
    public static readonly TimeSpan CheckAgainAfter = TimeSpan.FromMinutes(1);
    // How long after a failed download it is not downloaded again, whoever asks.
    public static readonly TimeSpan DownloadAgainAfter = ObservedUpdater.DownloadAgainAfter;
    // How many presses are remembered as taken, across restarts.
    public const int TakenKept = 32;
    // How a press the app took and had not carried out when it last stopped (quit, signed out, the
    // machine restarted) ends: the next start says so, rather than leaving it to vanish.
    public const string Restarted = "the app on this machine restarted before carrying it out; press it again";
    // How a press the app came to only after the host had let it go ends (UpdateProtocol.Lapses).
    public const string Lapsed = "this machine's app did not take it within 10 minutes of the press; press it again";
    // The refusals that are the person's choice, which hold an update for as long as they like.
    // Every other refusal is the machine's (the host gone, another profile running, its status
    // unreadable), which must not hold one silently for ever: on 2026-11-04 a VM's app lost its
    // link to a live host and sat on an old release for ~20 h, logging nothing (#800).
    public const string SwitchedOff = "Install updates automatically is off";
    public const string DevelopmentCopy = "this is a development copy";
    public const string HostGone = "the host is not running, so whether its sessions are busy cannot be told";
    public const string SessionsUnread = "the host's sessions are not yet read in full, so whether they are busy cannot be told";
    // After the machine stops refusing an update held past WaitAtMost (the link back, say), it
    // still waits this long for the sessions to go idle before it is forced: devices see "Needs
    // you" for more than a minute, and a turn under way gets the chance to end (#800 review).
    public static readonly TimeSpan ReturnGrace = TimeSpan.FromMinutes(20);
    // A refusal still in force is logged again this often, not only when it changes.
    public static readonly TimeSpan SayAgainAfter = TimeSpan.FromHours(1);
    // An update held past WaitAtMost by the machine is told to the owner, again this often.
    public static readonly TimeSpan NeedsYouAgainAfter = TimeSpan.FromDays(1);
    // Re-attaching to a host the link has lost: at once, then backing off to this at most.
    public static readonly TimeSpan ReattachFirst = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ReattachAtMost = TimeSpan.FromMinutes(30);
    // Release checks the feed refused for its rate limit are held off for what it asks, or
    // otherwise for twice as long each time, up to this.
    public static readonly TimeSpan CheckBackoffAtMost = CheckEvery * 4;
    // A Retry-After or rate-limit reset further off than this is not believed.
    static readonly TimeSpan CheckHintAtMost = TimeSpan.FromDays(1);

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
    // When the install being carried out was pressed: its six hours run from no earlier than that.
    DateTimeOffset installAskedAt;
    // Since when installing has been allowed without a break (no refusal at a tick), or null while
    // it is refused: the six hours run from no earlier than that either.
    DateTimeOffset? allowedSince;
    // The last check's end, and its failure if it failed, which a press soon after is answered with.
    DateTimeOffset? lastCheckAt;
    string? lastCheckFailed;
    string? lastFailure;
    DateTimeOffset? idleSince;
    string? lastWhyNot;
    // When the refusal in force was last logged, and the owner last told of an update it holds.
    DateTimeOffset? saidAt;
    DateTimeOffset? neededYouAt;
    // When the machine last stopped refusing (ReturnGrace runs from it).
    DateTimeOffset? returnedAt;
    // The next re-attach to a lost host allowed, and the wait after it; what re-attaching last
    // logged, and when, so its lines come no more than hourly too.
    DateTimeOffset? reattachAt;
    TimeSpan reattachWait = ReattachFirst;
    readonly Dictionary<string, DateTimeOffset> reattachSaid = new(StringComparer.Ordinal);
    // Release checks held off after the feed's rate limit refused them, and why.
    DateTimeOffset? checksHeldUntil;
    string? checksHeld;
    int rateLimited;
    string? lastProbe;
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
        // A session read past a field breaks the idle run too, even when one read in full follows
        // it before the next tick: the idle time before it is no evidence about the time after.
        host.Sessions.Upserted += s => { if (Busy(s) || host.Sessions.Unsure) { idleSince = null; } };
        host.Sessions.Synced += () => idleSince = null;
        Resume();
    }

    // The presses the app before this start had taken and not finished (UpdateInstallAsked,
    // UpdateCheckAsked). One an update answered was reported by Announce, with its request; the
    // other is ended as Restarted (the install first, there being one last outcome), not carried
    // out now: an install pressed hours ago must not go ahead on sessions never watched for it.
    void Resume()
    {
        var p = prefs();
        if (p.UpdateInstallAsked is null && p.UpdateCheckAsked is null) { return; }
        save(p with { UpdateInstallAsked = null, UpdateCheckAsked = null });
        if (new[] { p.UpdateInstallAsked, p.UpdateCheckAsked }.FirstOrDefault(r => r is not null && r != p.LastUpdate?.Request) is { } left)
        {
            log("update: a press taken before the app restarted was not carried out");
            Remember(UpdateOutcome.Refused, Restarted, null, left);
        }
    }

    // The presses being carried out, kept in the preferences for Resume.
    void Keep()
    {
        string? install, check;
        lock (asks) { (install, check) = (installRequest, checkRequest); }
        var p = prefs();
        if (p.UpdateInstallAsked != install || p.UpdateCheckAsked != check) { save(p with { UpdateInstallAsked = install, UpdateCheckAsked = check }); }
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
                CheckedAt = updater.FoundAsOf,
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
        var now = time.GetUtcNow();
        var at = ForcedAt();
        // Never a time already gone: the wait is over, and the next tick installs it.
        var until = at > now ? $"; it installs anyway at {at.ToLocalTime():g}" : "; its wait is over, so it installs at the next look";
        if (lastWhyNot is { } why) { return Overdue(why, now) is { } since ? NeedsYouText(Pending!, since, why) : why; }
        // Due, but held for a usage probe on the host (#238): without it, devices read an idle
        // timer, or "installs at the next look", while every look waited on the probe.
        if (lastProbe is { } probe) { return $"{probe}; it installs once that is done"; }
        return Idle ? $"no session has been busy for {IdleFor.TotalMinutes:0} minutes yet{until}"
            : $"a session on this machine has a turn running or a question waiting{until}";
    }

    // When an update goes whatever the sessions are doing: six hours from WaitFrom, and no sooner
    // than ReturnGrace after the machine last stopped refusing it.
    DateTimeOffset ForcedAt()
    {
        var at = WaitFrom() + WaitAtMost;
        return returnedAt is { } back && back + ReturnGrace > at ? back + ReturnGrace : at;
    }

    // The person's own choice: anything else that refuses is the machine holding the update.
    static bool Chosen(string why) => why is SwitchedOff or DevelopmentCopy;

    // Since when an update the machine refuses has waited, once that is longer than WaitAtMost.
    DateTimeOffset? Overdue(string? why, DateTimeOffset now) =>
        why is not null && !Chosen(why) && Pending is not null && now - WaitFrom() >= WaitAtMost ? WaitFrom() : null;

    static string NeedsYouText(string release, DateTimeOffset since, string why) =>
        $"Needs you: the update to {release} has waited since {since.ToLocalTime():g} because {why}";

    // A press from another device (UpdateReporter): Check, or Install now. A newer press of the
    // same kind takes over from an older one, whose device then sees another request under way.
    // The tick it starts, which is already done if one was running (that one runs again for it).
    // at: when the device pressed it, as the host says; one older than UpdateProtocol.Lapses has
    // lapsed at the host, and is refused rather than carried out late.
    public Task Ask(UpdateAskKind kind, string request, DateTimeOffset? at = null)
    {
        // Taken before, by this app or the one before a restart: carried out once.
        var taken = prefs().UpdateTaken ?? [];
        if (taken.Contains(request)) { return Task.CompletedTask; }
        save(prefs() with { UpdateTaken = [.. taken.Append(request).TakeLast(TakenKept)] });
        if (at is { } pressed && time.GetUtcNow() - pressed > UpdateProtocol.Lapses)
        {
            log("update: a press that had lapsed was not carried out");
            Remember(UpdateOutcome.Refused, Lapsed, null, request);
            return Task.CompletedTask;
        }
        lock (asks)
        {
            if (kind == UpdateAskKind.Install) { installRequest = request; installVetted = null; installAskedAt = time.GetUtcNow(); }
            else { checkRequest = request; }
        }
        Keep();
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
        Keep();
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
        if (!asked && !prefs().AutoUpdate) { return SwitchedOff; }
        if (updater.InstalledVersion is null) { return DevelopmentCopy; }
        if (host.State != LinkState.Connected || host.Status is null) { return HostGone; }
        return null;
    }

    static bool Busy(Session s) => s.Question is not null || s.Status is SessionStatus.Working or SessionStatus.Waiting;

    // No turn running and no question waiting on this machine's host, as its whole snapshot says:
    // a list still being read may be missing the busy session, and one with a session the app
    // could not fully read may have it wrong (#446). Another machine's sessions are its own host's:
    // an update here restarts only this one.
    public bool Idle => host.Sessions.Complete && !host.Sessions.Unsure && !host.Sessions.All.Any(Busy);

    // Idle now and for IdleFor without a break, or waited six hours (and ReturnGrace since the
    // machine last stopped refusing it). Either way only over a whole snapshot: six hours on, an
    // update still waits out a reconnect's sessions being read again.
    bool Due(DateTimeOffset now)
    {
        if (!Idle) { idleSince = null; }
        else { idleSince ??= now; }
        if (!host.Sessions.Complete) { return false; }
        return (idleSince is { } since && now - since >= IdleFor) || now >= ForcedAt();
    }

    // Where the six hours run from: the release first found, for an automatic install. For one a
    // device pressed (automatic installs off), no earlier than the press: a release found by a
    // check long before, and never installed by itself, must not go at once whatever the sessions
    // are doing.
    // Nor earlier than installing was last allowed by the person: a release a check found with
    // automatic installs off, kept while every tick refused to install it, must not go at once
    // whatever the sessions are doing when they are turned on hours later. The machine's own
    // refusals (the host gone, another profile running) do not restart it: they restarted it at
    // every tick, so the six hours never ran out behind one (#800).
    DateTimeOffset WaitFrom()
    {
        var from = !prefs().AutoUpdate && Asked(UpdateAskKind.Install) is not null && installAskedAt > foundAt ? installAskedAt : foundAt;
        var allowed = allowedSince ?? time.GetUtcNow();
        return allowed > from ? allowed : from;
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
            // The link to the host lost: tried again (its status asked for, or the link made again
            // when the host answers on its pipe) before refusing again, so a link that went stale
            // is not left so for good (#800). Never a host started: that is the person's, and the
            // app's own start's.
            if (why == HostGone)
            {
                await ReattachAsync();
                why = WhyNot(asked);
            }
            // Nothing installs, and a check is made only when a device asked for one, or the machine
            // rather than the person holds the install: a release found is then pending, and one
            // held past WaitAtMost is told to the owner. Said to the devices too: the switch turned
            // off while a release waited leaves it to install when asked, no longer by itself.
            if (why is not null && Chosen(why) && Asked(UpdateAskKind.Check) is null) { Refused(why); idleSince = null; Changed(); return; }
            var now = time.GetUtcNow();
            Due(now);
            // Held off by the feed's rate limit: due at the hold's end, however recently a press was
            // answered with it, and the hold over then (rateLimited still doubles the next one);
            // otherwise every CheckEvery.
            bool checkDue;
            if (checksHeldUntil is { } heldUntil)
            {
                checkDue = now >= heldUntil;
                // Its cached failure goes with it: answered from that, the check at the hold's end
                // (a reset under CheckAgainAfter away) never reached the feed, and the next waited
                // CheckEvery more.
                if (checkDue) { (checksHeldUntil, checksHeld, lastCheckAt, lastCheckFailed) = (null, null, null, null); }
            }
            else { checkDue = checkedAt is null || now - checkedAt >= CheckEvery; }
            if (checkNow || checkDue)
            {
                checkNow = false;
                checkedAt = now;
                await CheckAsync(now);
            }
            // A connected host whose sessions are still not read in full holds the install (Due) as
            // the machine's refusal, told as one, though a press for it stays queued (#800).
            if (why is null && Pending is not null && !host.Sessions.Complete) { why = SessionsUnread; }
            // The machine refused at the last look just before installing (another profile running,
            // the host's own status unreadable): asked again before that is taken as over, rather
            // than cleared unasked, which started ReturnGrace and dropped "Needs you" while the
            // blocker stayed (#923 review). A press is refused with it once its install is due, as
            // it would have been below.
            if (why is null && lastWhyNot is { } before && !Chosen(before))
            {
                var pressedNow = Asked(UpdateAskKind.Install);
                if (await WhyNotNowAsync(pressedNow is not null) is { } still)
                {
                    Refused(still);
                    if (pressedNow is not null && Pending is not null && Due(time.GetUtcNow())) { Finish(ref installRequest, pressedNow, UpdateOutcome.Refused, still); }
                    Changed();
                    return;
                }
            }
            if (Refused(why)) { idleSince = null; Changed(); return; }
            // No longer due (a session busy again): what holds it is that, not a probe from before.
            if (Pending is null || !Due(time.GetUtcNow())) { lastProbe = null; Changed(); return; }
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
            // And not while a usage probe tied to the host is running or in its grace (#238): the
            // host would be stopped under it. The host starts no new one while this is asked
            // every tick; the install goes ahead at the first tick after it is done, still due.
            if (await ProbeBusyAsync() is { } probe)
            {
                if (probe != lastProbe) { log($"auto-update: waiting to install {Pending}: {probe}"); }
                lastProbe = probe;
                Changed();
                return;
            }
            lastProbe = null;
            // Asking took time too (up to its call timeout): the same checks again, as of now.
            asked = Asked(UpdateAskKind.Install) is not null;
            if (Refused(WhyNot(asked)) || !Due(time.GetUtcNow()) || Stale()) { return; }
            await InstallAsync(asked ? "another device asked for it and no session is busy"
                : idleSince is not null ? "no session is busy" : $"it has waited {WaitAtMost.TotalHours:0} hours");
        }
        // A check that cannot reach the network, a download cut off: tried again at the next check.
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // Said once, not at every press answered with the same failure.
            if (e.Message != lastFailure) { log($"auto-update: {e.Message}"); }
            lastFailure = e.Message;
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
            // A check a moment ago answers: its release, or its failure, unless one from Settings
            // has succeeded since.
            if (lastCheckAt is { } at && now - at < CheckAgainAfter)
            {
                if (lastCheckFailed is { } failed && !(updater.CheckedAt >= at)) { throw new InvalidOperationException(failed); }
                found = updater.Found;
            }
            else
            {
                // Held off after the feed's rate limit: asking again only extends it (#800).
                if (checksHeldUntil is { } until && now < until) { throw new InvalidOperationException(checksHeld); }
                try { found = await updater.CheckAsync(); }
                catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
                {
                    if (RateLimited(e, out var retryAt))
                    {
                        var held = HoldChecks(now, retryAt, e.Message);
                        (lastCheckAt, lastCheckFailed) = (now, held);
                        throw new InvalidOperationException(held, e);
                    }
                    (lastCheckAt, lastCheckFailed) = (now, e.Message);
                    throw;
                }
                (rateLimited, checksHeldUntil, checksHeld) = (0, null, null);
                // As of when it was asked: a press after that, a minute on, asks again.
                (lastCheckAt, lastCheckFailed) = (now, null);
            }
            // Not again for a while after it failed, from here or Settings (ObservedUpdater).
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
                log($"auto-update: {found} downloaded; installing when no session is busy, or at {(WaitFrom() + WaitAtMost).ToLocalTime():g}");
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
            Keep();
        }
        Changed();
    }

    // Why the host may not be stopped for the install yet, or null. Only an older host's "unknown
    // op" counts as nothing to wait for: it runs no such probe. A refusal, or a host that does not
    // answer, is waited for, not stopped under it.
    async Task<string?> ProbeBusyAsync()
    {
        try
        {
            var held = await host.CallAsync<ProbeHoldResult>(HostOps.ProbeHold, timeout: TimeSpan.FromSeconds(10));
            return held.Busy ? held.Why ?? "a usage probe on the host is finishing" : null;
        }
        catch (HostErrorException e) when (e.Message.StartsWith($"unknown op {HostOps.ProbeHold}", StringComparison.Ordinal)) { return null; }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            return $"the host could not say whether a usage probe it started is still finishing: {e.Message}";
        }
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
        // A press from another device is not carried out on a machine that has become read-only
        // for other devices since (an automatic update still is: the rule guards other devices).
        if (asked && fresh.ReadOnly is { } readOnly) { return $"this machine is read-only for other devices now: {readOnly}"; }
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
        var now = time.GetUtcNow();
        // Logged when it changes, and again every SayAgainAfter while it lasts: logged only when it
        // changed, a refusal that lasted ~20 h left one line (#800).
        if (why is not null && (why != lastWhyNot || saidAt is not { } said || now - said >= SayAgainAfter))
        {
            var link = why == HostGone ? $" (the app's link to it: {host.State}{(host.StateDetail is { } d ? $", {d}" : "")})" : "";
            log($"auto-update: not installing updates: {why}{link}");
            saidAt = now;
        }
        // The machine no longer refusing: ReturnGrace before it is forced (ForcedAt).
        if (why is null && lastWhyNot is { } before && !Chosen(before)) { returnedAt = now; }
        lastWhyNot = why;
        if (why is null) { saidAt = null; }
        if (why != HostGone) { (reattachAt, reattachWait) = (null, ReattachFirst); }
        if (why is not null && Chosen(why)) { allowedSince = null; }
        else { allowedSince ??= now; }
        // Not forgotten when the refusal lifts for a moment: a link that flaps tells the owner
        // no more than daily either.
        if (Overdue(why, now) is { } since && (neededYouAt is not { } told || now - told >= NeedsYouAgainAfter))
        {
            neededYouAt = now;
            log($"auto-update: {NeedsYouText(Pending!, since, why!)}");
            Changed();
        }
        return why is not null;
    }

    // The app's link to its host is not up (or has no status): a connected link asked for the
    // host's status again; otherwise, when the host answers on its pipe, the link ended and made
    // again (HostConnection.Reconnect). Never a host started: whether one runs is the person's,
    // and the app's own start's (#800 review: a start here raced Stop and updates). At once, then
    // backing off to ReattachAtMost while it stays lost.
    async Task ReattachAsync()
    {
        var now = time.GetUtcNow();
        if (reattachAt is { } at && now < at) { return; }
        reattachAt = now + reattachWait;
        reattachWait = reattachWait * 2 < ReattachAtMost ? reattachWait * 2 : ReattachAtMost;
        try
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (host.State == LinkState.Connected)
            {
                SayReattach("status", "the app's link to the host is connected but has no status from it; asked the host for its status again", now);
                await host.RefreshStatusAsync(bound.Token);
                return;
            }
            var link = $"{host.State}{(host.StateDetail is { } d ? $" ({d})" : "")}";
            if (await manager.IsRunningAsync(bound.Token))
            {
                host.Reconnect();
                SayReattach("reconnect", $"the host answers on its pipe, but the app's link to it is {link}; ended the link's attempt under way and started a new one", now);
                return;
            }
            SayReattach("absent", $"the host does not answer on its pipe (the app's link to it: {link}); not starting it, as only the person or the app's own start does", now);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            SayReattach("failed", $"re-attaching to the host failed: {e.Message}", now);
        }
    }

    // Re-attaching's lines, each kind (what was done) no more than hourly, whatever the link's
    // state in it says.
    void SayReattach(string kind, string line, DateTimeOffset now)
    {
        if (reattachSaid.TryGetValue(kind, out var said) && now - said < SayAgainAfter) { return; }
        reattachSaid[kind] = now;
        log($"auto-update: {line}");
    }

    // A check the feed refused for its rate limit (GitHub's 403 "rate limit exceeded", or a 429):
    // none until what it asked (Retry-After, X-RateLimit-Reset), or else twice the last hold, from
    // CheckEvery*2 up to CheckBackoffAtMost. Said to the devices once each time (State.Last),
    // unless a press is being answered, which ends with it.
    string HoldChecks(DateTimeOffset now, DateTimeOffset? retryAt, string failure)
    {
        rateLimited++;
        var backoff = TimeSpan.FromTicks(CheckEvery.Ticks * (1L << Math.Min(rateLimited, 8)));
        var until = retryAt is { } r && r > now && r - now <= CheckHintAtMost ? r
            : now + (backoff < CheckBackoffAtMost ? backoff : CheckBackoffAtMost);
        checksHeldUntil = until;
        checksHeld = $"the release feed's rate limit refused the check ({failure}); checking again after {until.ToLocalTime():t}";
        // Not over an outcome the devices may not have seen yet: the update just made (its first
        // check, right after the restart, is the likeliest to be refused) or a press answered.
        var last = prefs().LastUpdate;
        var unseen = last is { } l && (l.Outcome == UpdateOutcome.Updated || l.Request is not null) && now - l.At < NeedsYouAgainAfter;
        if (!unseen && Asked(UpdateAskKind.Check) is null && Asked(UpdateAskKind.Install) is null) { Remember(UpdateOutcome.Failed, checksHeld, null, null); }
        return checksHeld;
    }

    // Whether a check failed for the feed's rate limit, and until when it asked to be left alone.
    // GitHub's list comes through the machine's ReleaseDiscovery (#530), which says a limit as
    // Limited with when it will ask again: GitHub's reset when GitHub sent one, else its own
    // backoff (a minute, doubling to an hour), which every app and host on the machine keeps to, so
    // the hold here ends with it rather than telling the devices a second, different time. A 403 or
    // 429 straight from a feed (a release's releases.<channel>.json) counts too, a 403 only when it
    // says so: GitHub refuses other things with 403 too.
    static bool RateLimited(Exception e, out DateTimeOffset? retryAt)
    {
        retryAt = null;
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is ReleaseDiscoveryException { Limited: true } limited) { retryAt = limited.RetryAt; return true; }
            if (x is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }) { return true; }
            if (x is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } && x.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
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
    // announces, and the press from another device it answers, if one asked for it. From Settings,
    // that is the Install a device pressed and the app has not yet carried out: Settings installs
    // the release it would have.
    public static void Record(Func<AppPreferences> prefs, Action<AppPreferences> save, IUpdater updater, string? request = null) =>
        save(prefs() with { UpdatedFrom = updater.InstalledVersion, UpdateNotes = updater.PendingNotes, UpdateRequest = request ?? prefs().UpdateInstallAsked });

    // Settings' Update and restart failed, the old release still running: kept for the devices as
    // an automatic install that fails is. The press a device made, if Settings took it on, is left
    // to the app, which still carries it out. recorded: Settings had recorded the install (Record),
    // whose markers go with it; a download that failed before then leaves them alone, since they
    // may be an automatic install's, under way meanwhile.
    public static void Failed(Func<AppPreferences> prefs, Action<AppPreferences> save, IUpdater updater, string? to, string reason, bool recorded = true,
        TimeProvider? time = null)
    {
        var result = new UpdateResult { Outcome = UpdateOutcome.Failed, From = updater.InstalledVersion, To = to, Reason = reason, At = (time ?? TimeProvider.System).GetUtcNow() };
        save(recorded ? prefs() with { UpdatedFrom = null, UpdateNotes = null, UpdateRequest = null, LastUpdate = result } : prefs() with { LastUpdate = result });
        (updater as ObservedUpdater)?.Noted();
    }

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
