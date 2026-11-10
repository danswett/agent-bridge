using AgentBridge.Host.Core.Updates;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// Downloads updates in the background, and installs them as Settings' "When an update is ready"
// says, per machine and profile (owner decision on #689, 6674007815; Product, 6674016765):
// * Restart to update (the default, for a machine a person uses; #516): only when a person asks.
//   The release waits, ready (Ready), for as long as it takes: the app's banner and Settings offer
//   Restart to update. Before #516 the app installed by itself once the host had been idle for two
//   minutes, or after six hours whatever it was doing, and so exited under the person without a
//   word.
// * Install between turns (for a machine agents use): by itself, at the first moment no session
//   here has a turn running or a question waiting, over a snapshot settled for SettleFor (not
//   IdleFor's two minutes, which a machine of busy agents never reaches). A machine that never
//   goes quiet installs anyway after WaitAtMost (and ReturnGrace after the machine last stopped
//   refusing), over a turn running, but never while a question waits or whether one does cannot
//   be told: the update stops the host, and the question must not be stranded (Product's rule).
//   It says "Needs you" instead.
// "Download updates automatically", on by default, per profile beside "Include pre-releases",
// checks on start and every half hour; a release it finds is downloaded at once. A download that fails is tried again in the background, more rarely each time
// (ObservedUpdater.HoldAfter), and never ends the app. A release that has waited longer than
// WaitAtMost, kept from the person by the machine (the host gone, another profile running from
// here, its status unreadable) or downloaded and simply not restarted into, is told to the owner
// as "Needs you" (#800), at most daily: a reminder, never an install. Set to Install between
// turns, only what keeps it from installing is told so: the machine refusing, a download failing,
// or a question waiting at the backstop.
//
// Installing takes a person's press, unless When an update is ready is Install between turns
// (above), by the same path every way: the host stopped over the pipe,
// then Update.exe handed the checked copy of the package (PackageHandoff, #984), whose new version
// starts the host again; its first start
// shows a toast saying what changed (Announce).
// * Restart to update, at this machine (RestartToUpdateAsync): at once, whatever the sessions are
//   doing, since the app has warned the person first when a session here has a turn running or a
//   question waiting (WaitingOnYou): a question the host was carrying may then have to be answered
//   at the machine.
// * Install, pressed on another of the person's devices (Ask, below): once this machine's host has
//   been idle for IdleFor, however long that takes. Nobody at the machine was warned, so a busy
//   session always holds it.
//
// A read-only host (delivery.readOnly) and one beside a running agent-ha-bridge (G5) update too:
// those rules guard what other devices may do to this machine (the Home Assistant connector's
// Install button among them), and replacing this app and its host touches neither the sessions'
// consoles nor agent-ha-bridge. Never while another profile's app or host runs from this
// installation (Velopack stops every process in the package, busy or not, and restarts only this
// profile). A device's press also waits while the host cannot say what its sessions are doing (not
// running, or its sessions not yet read in full). Only this app's timer checks and downloads, and
// only a person's press installs, unless When an update is ready is Install between turns:
// nothing an agent can reach does.
//
// Asked (#51, docs/protocol/v1-update.md): another of the person's devices pressed Check or
// Install for this machine, and the host carried it here (UpdateReporter). A check is made at
// once. An install is checked first against the same refusals (another profile running from this
// installation, the host gone), then the latest release is found, downloaded and installed once
// the host has been idle for IdleFor. Being asked stands in for "Download updates automatically"
// being on, for that release. How it ended (State.Last) is kept in the preferences, so an update
// that restarts the app is still reported, by the new version, with the press it answers.
//
// Update now (#1159, docs/protocol/v1-update.md, Going): an Install whose press the host verified
// as signed by the person, carrying what they confirmed it may go over. It is refused at once as an
// Install is, then found, downloaded and checked; it waits for a Codex usage check to end
// (usage-check, at most UsageCheckAtMost), but never for IdleFor or the backstop; then the host is
// asked to leave (host.update.depart), which takes #805's quiet lease and compares what is busy with
// the confirmation. Sent back by it, the download is kept and the press waits for the next quiet
// moment (SettleFor). It lives 15 minutes from its signature, then is dropped expired, the release
// still offered. How it ended carries a code (UpdateResult.Code), and why it waits one too
// (UpdateState.WaitingCode). UpdateReporter tells the host this app carries it.
public sealed class AutoUpdater
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);
    // While the host the last update installed has failed (HostWatch), a fix is looked for this
    // often instead: roll forward only, nothing is downgraded (#568).
    public static readonly TimeSpan CheckAfterFailedHost = TimeSpan.FromMinutes(5);

    // Set after an update: watched when WatchHostAsync is called, by the app that update started.
    public HostWatch? Watch { get; init; }
    volatile bool hostFailed;

    // Watches the host for HostWatch.Window; if it failed, records the update as Failed with the
    // reason and moves to the faster checks. A host that connected, or one with no relay to reach, says nothing.
    public async Task WatchHostAsync(CancellationToken ct = default)
    {
        if (Watch is null) { return; }
        var result = await Watch.RunAsync(ct);
        if (result.End != HostWatchEnd.Failed) { return; }
        hostFailed = true;
        log($"update: the host failed after the update: {result.Reason}");
        Remember(UpdateOutcome.Failed, result.Reason, null, null);
    }
    // How long the host must have been idle, seen at every tick and every session change, before
    // an Install pressed on another device goes ahead: long past the moment a snapshot is replaced
    // (sessions.subscribe again after a gap), whose upserts arrive within seconds even if an
    // earlier sessions.synced closed it early.
    public static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(2);
    // Set to Install between turns: how long no session here may have a turn running or a
    // question waiting, over a whole snapshot, before the update installs. Only for the snapshot
    // to settle (a reconnect's upserts arrive within seconds of its sessions.synced).
    public static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(5);
    // After the machine stops refusing an update held past WaitAtMost (the link back, say), Install
    // between turns still waits this long for a gap before the backstop installs: devices see
    // "Needs you" for more than a minute, and a turn under way gets the chance to end (#800 review).
    public static readonly TimeSpan ReturnGrace = TimeSpan.FromMinutes(20);
    // Set to Install between turns, an install that failed is not tried by itself again for a
    // while that doubles with each failure up to this (UpdateBackoff, kept in the preferences, as a
    // failed update restarts the app): every gap would otherwise stop the host for it again (the
    // host back from the failure is a gap). A person's Restart to update, or a device's Install,
    // still may at once.
    public static readonly TimeSpan InstallAgainAfter = TimeSpan.FromHours(1);
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
    // The refusals that are the person's choice, which stop checks and downloads for as long as
    // they like. Every other refusal is the machine's (the host gone, another profile running, its
    // status unreadable), which must not stop them silently: on 2026-11-04 a VM's app lost its
    // link to a live host and sat on an old release for ~20 h, logging nothing (#800).
    public const string SwitchedOff = "Download updates automatically is off";
    public const string DevelopmentCopy = "this is a development copy";
    public const string HostGone = "the host is not running, so whether its sessions are busy cannot be told";
    public const string SessionsUnread = "the host's sessions are not yet read in full, so whether they are busy cannot be told";
    // A refusal still in force is logged again this often, not only when it changes.
    public static readonly TimeSpan SayAgainAfter = TimeSpan.FromHours(1);
    // A release found or downloaded that has waited this long, kept from the person by the machine
    // or not restarted into, is told to the owner as "Needs you", in the state the devices see and
    // in the log (#800). Set to Restart to update nothing is forced then; set to Install between
    // turns it is the backstop, which installs over a turn running, never a question waiting.
    public static readonly TimeSpan WaitAtMost = TimeSpan.FromHours(6);
    // The owner is told again this often while it lasts; and an update made, or a press answered,
    // this recently is left for the devices to see, never replaced by a rate-limit hold's failure.
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
    // The app's UI thread, where the sessions this reads change: the settle timer's tick is posted
    // there, never run on the timer's own thread (Copilot, r4488279939).
    readonly IUiDispatcher ui;
    // Names what holds files in the installation, or null: asked before the host is stopped
    // (HostManager.StopAndApplyAsync), so an update that cannot replace them stops nothing.
    readonly Func<string?>? preflight;
    DateTimeOffset? checkedAt;
    // The last check's end, and its failure if it failed, which a press soon after is answered with.
    DateTimeOffset? lastCheckAt;
    string? lastCheckFailed;
    // The source (ObservedUpdater.Source) the last check asked, and the one this last looked at:
    // everything above that a check or a download set belongs to that source (SourceMoved).
    string? lastCheckSource;
    string seenSource;
    string? lastFailure;
    DateTimeOffset? idleSince;
    string? lastWhyNot;
    // When the refusal in force was last logged, and the owner last told of an update it holds.
    DateTimeOffset? saidAt;
    DateTimeOffset? neededYouAt;
    // When the machine last stopped refusing (ReturnGrace runs from it).
    DateTimeOffset? returnedAt;
    // Set to Install between turns: the tick due once a gap has settled (SettleFor), rather than
    // at the next minute's, which a short gap between turns would not last to.
    readonly object settleGate = new();
    ITimer? settle;
    // When the release found or downloaded first was (a newer one taking its place keeps it, so
    // releases every half hour cannot put "Needs you" off for good), and since when the person has
    // allowed updates without a break: the machine's own refusals never restart that (#800).
    DateTimeOffset? foundAt;
    DateTimeOffset? allowedSince;
    // When the machine was last asked whether anything else holds an update waited past
    // WaitAtMost (another profile running, the host's status unreadable).
    DateTimeOffset? probedAt;
    // The next re-attach to a lost host allowed, and the wait after it; what re-attaching last
    // logged, and when, so its lines come no more than hourly too.
    DateTimeOffset? reattachAt;
    TimeSpan reattachWait = ReattachFirst;
    readonly Dictionary<string, DateTimeOffset> reattachSaid = new(StringComparer.Ordinal);
    // Release checks held off after the feed's rate limit refused them, and why.
    DateTimeOffset? checksHeldUntil;
    string? checksHeld;
    // When the refused check came back: only a check that succeeded after that ends the hold.
    DateTimeOffset? heldAt;
    int rateLimited;
    string? lastProbe;
    // Held by a tick, and by Restart to update from before it reads the release downloaded until
    // it has installed it or given up: neither changes what the other is working with.
    readonly SemaphoreSlim work = new(1, 1);
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
    // Update now (#1159, docs/protocol/v1-update.md): the install press is one, with its terms (the
    // release shown, what was confirmed, the end of its life) and who pressed it; null for a plain
    // Install. Why it waits (UpdateWaitingCode); since when its usage-check hold has run, kept when a
    // new press replaces it there so the 11 minutes don't start again; and whether the host sent it
    // back to wait for the next quiet moment.
    UpdateNowAsk? installNow;
    string? installBy;
    string? nowWaiting;
    DateTimeOffset? usageHoldSince;
    bool nowWaitsForQuiet;
    // The host's waiting code while an Update now waits for a quiet moment, which a usage-check
    // hold under it gives way to and returns to.
    string? quietCode;
    ITimer? expiry;
    // The Update now the host is being asked to leave for, or has left for: it ends by the host's
    // answer and the install, never dropped expired meanwhile (the host reads its life itself, and
    // one it committed within it is carried out).
    string? departing;

    // How long an Update now waits for a Codex usage check on the host to end (#238, settled Q5):
    // HostManager.ProbeWait, the wait every install gives one.
    public TimeSpan UsageCheckAtMost { get; init; } = TimeSpan.FromMinutes(11);
    // How long the host may take to answer host.update.depart: its lease is tried for about a
    // minute (6 x 10 s), then it announces and commits.
    public static readonly TimeSpan DepartWait = TimeSpan.FromMinutes(3);

    bool NowPress => installNow is not null && installRequest is not null;

    // othersInPackage(this host's pid): another profile's app or host running from this
    // installation, which an update would stop; null when there is none. updater: the app's
    // ObservedUpdater, which Settings uses too, so what Settings does is reported as well.
    // A release downloaded is said by the app's banner and Settings, never a toast: toasts are
    // for what waits on the person, and an update can wait (Creative, #231 for a found agent).
    public AutoUpdater(IUpdater updater, HostConnection host, HostManager manager, Func<AppPreferences> prefs, Action<AppPreferences> save, Action<string> log,
        Func<int?, string?> othersInPackage,         TimeProvider? time = null, IUiDispatcher? ui = null,
                Func<string?>? preflight = null)
    {
                this.preflight = preflight;
        this.updater = updater as ObservedUpdater ?? new ObservedUpdater(updater, time);
        this.updater.Changed += Changed;
        seenSource = this.updater.Source;
        this.host = host;
        this.manager = manager;
        this.prefs = prefs;
        this.save = save;
        this.log = log;
        this.othersInPackage = othersInPackage;
        this.time = time ?? TimeProvider.System;
        this.ui = ui ?? InlineDispatcher.Instance;
        // A turn starting or a question arriving between ticks starts the idle wait again; so does a
        // snapshot read in full again (a reconnect, a gap), so idle seen before it does not count.
        // Nothing installs while one is being read (Due), and every reconnect ends in one.
        // A session read past a field breaks the idle run too, even when one read in full follows
        // it before the next tick: the idle time before it is no evidence about the time after.
        host.Sessions.Upserted += s => { if (Busy(s) || host.Sessions.Unsure) { idleSince = null; } else { Gap(); } };
        host.Sessions.Synced += () => { idleSince = null; Gap(); };
        Resume();
        // A release an earlier run downloaded and the person has not restarted into yet: ready now,
        // before any check (offline, or the feed refusing for a while, #530), as it was when that
        // run ended. Velopack no longer applies it by itself on this start (Program).
        if (this.updater.InstalledVersion is not null && this.updater.Ready is { } downloaded) { Downloaded(downloaded); }
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

    // The release found and downloaded, waiting for a person to install it.
    public string? Pending { get; private set; }

    // Whether the app's window is open now (#1167): an install begun while it is shows the Updating
    // screen there, and the next start opens it again. Never one that is closed: an update a device
    // started on an unattended machine pops no window (#1159). Read off the UI thread.
    public Func<bool> WindowOpen { get; init; } = () => false;

    // The release being installed, while the window that was open when the install began shows the
    // Updating screen (#1167); null otherwise. Nothing waits for it: the screen stays as long as
    // the host's stop and the hand-over to Update.exe take, and goes if the install fails.
    public string? Updating => installing ? updatingIn : null;
    string? updatingIn;

    // The release downloaded and waiting for the person (the banner's and Settings'
    // Restart to update), or null while there is none, it is being installed, or the updater no
    // longer has it ready (a later check found a newer one, still downloading or held after a
    // failure): never offered when pressing it could not install it.
    public string? Ready => installing || updater.InstalledVersion is null || seenSource != updater.Source || updater.Ready != Pending ? null : Pending;

    // What a restart now would interrupt on this machine, for the warning Restart to update gives
    // first; null when nothing here is waiting on the person.
    public string? WaitingOnYou => RestartWarning.For(host);

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
            bool requestNow;
            string? nowCode;
            lock (asks)
            {
                request = installRequest ?? checkRequest;
                requestNow = installRequest is not null && installNow is not null;
                nowCode = requestNow ? nowWaiting : null;
            }
            var now = installing ? UpdatePhase.Installing : updater.Phase;
            // A release downloaded that will install because a device asked, once the host is idle,
            // or because this machine installs between turns. One downloaded and waiting for
            // Restart to update is plain Idle with it Available: a device's Install is then what
            // installs it.
            var waiting = now == UpdatePhase.Idle && Pending is not null && installed is not null && (installRequest is not null || (BetweenTurns && prefs().AutoUpdate));
            // What the last check found, from here or from Settings: still there when its download
            // failed, and gone when a later one found nothing newer.
            // A release an earlier run downloaded is available too, before any check, and while
            // the feed refuses (#530): the banner here offers it, so devices may press Install.
            var available = updater.Found ?? updater.Ready;
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
                // "Needs you" once the update has waited past WaitAtMost (#800), here too while the
                // phase stays Idle: Waiting would hide the devices' Install, and say it installs.
                Waiting = waiting ? (nowCode is not null ? NowWaits(nowCode) : WaitingFor()) : NeedsYou(time.GetUtcNow()),
                Request = request,
                // An Update now (#1159): the press in request is one, and, while it waits, why, as
                // a code the devices word themselves (Waiting stays for the log and older clients).
                RequestNow = requestNow,
                WaitingCode = waiting ? nowCode : null,
                // Installs without being asked only when set to Install between turns (#689): the
                // devices see which this machine does without a field of its own. The switch only
                // downloads (#516).
                Automatic = BetweenTurns && prefs().AutoUpdate,
                Last = prefs().LastUpdate,
            });
        }
    }

    // An Update now waiting, for the log and older clients (newer ones word WaitingCode themselves).
    string NowWaits(string code) => code switch
    {
        UpdateWaitingCode.UsageCheck => $"{lastProbe ?? "a Codex usage check is running"}; Update now goes once that is done",
        UpdateWaitingCode.TurnRunning => "Update now waits for the current turn to finish",
        UpdateWaitingCode.QuestionWaiting => "Update now waits for a question to be answered",
        _ => "Update now waits until the host can tell the sessions are quiet",
    };

    string WaitingFor()
    {
        var now = time.GetUtcNow();
        // Due, but held for a usage probe on the host (#238): without it, devices read the gap or
        // the backstop's time while every look waited on the probe.
        return Skipping(now) ?? NeedsYou(now) ?? lastWhyNot ?? (lastProbe is { } probe ? $"{probe}; it installs once that is done" : null) ?? Busyness();
    }

    // The update line while the backstop skips for a question waiting: Creative's words on #719
    // (6674096426), which the devices show as they are. The log says "Needs you" (Refused).
    public const string QuestionLine = "Update ready \u00b7 waiting for a question to be answered";

    string? Skipping(DateTimeOffset now) =>
        BetweenTurns && Asked(UpdateAskKind.Install) is null && Ready is not null && lastWhyNot is null && foundAt is not null
            && now >= ForcedAt(now) && BackstopHeld() == QuestionWaits ? QuestionLine : null;

    string Busyness()
    {
        if (installRequest is not null || !BetweenTurns)
        {
            return Idle ? $"no session has been busy for {IdleFor.TotalMinutes:0} minutes yet"
                : "a session on this machine has a turn running or a question waiting";
        }
        // Install between turns: the gap settling, or the backstop's time.
        var now = time.GetUtcNow();
        if (FailedRetryAt(now) is { } retryAt) { return $"update failed, retrying at {retryAt.ToLocalTime():t}, or at Restart to update"; }
        var at = ForcedAt(now);
        var until = at > now ? $"; it installs anyway at {at.ToLocalTime():g} unless a question is waiting then" : "";
        return Idle ? "it installs between turns, as soon as this machine's sessions are read and still idle"
            : $"it installs between turns, and a session on this machine has a turn running or a question waiting{until}";
    }

    // Set to Install between turns (Settings' "When an update is ready").
    bool BetweenTurns => prefs().WhenReady == UpdateWhenReady.InstallBetweenTurns;

    // When the backstop may install over a turn running: WaitAtMost from WaitFrom, and no sooner
    // than ReturnGrace after the machine last stopped refusing it.
    DateTimeOffset ForcedAt(DateTimeOffset now)
    {
        var at = WaitFrom(now) + WaitAtMost;
        return returnedAt is { } back && back + ReturnGrace > at ? back + ReturnGrace : at;
    }

    // Why the backstop may not install now, or null when it may: a question waiting, or not being
    // able to tell whether one is (the snapshot not read in full, or a session in it not read).
    // Installing stops the host (HostManager.StopAndApplyAsync), and the app cannot establish
    // that a question waiting stays answerable from the person's other devices through that: its
    // own warning says it may then have to be answered at the machine (RestartWarning). Product's
    // rule on #719 (6674016765, 6674052423): an update never strands a waiting question, and
    // can't-tell skips.
    string? BackstopHeld()
    {
        if (host.State != LinkState.Connected || !host.Sessions.Complete || host.Sessions.Unsure) { return CannotTellQuestions; }
        return host.Sessions.All.Any(s => s.Question is not null || s.Status == SessionStatus.Waiting) ? QuestionWaits : null;
    }

    public const string QuestionWaits = "a question is waiting on this machine, and installing stops its host, which could leave the question answerable only at the machine";
    public const string CannotTellQuestions = "whether a question is waiting on this machine cannot be told, and installing stops its host";

    // The person's own choice: anything else that refuses is the machine holding the update.
    static bool Chosen(string why) => why is SwitchedOff or DevelopmentCopy;

    // The release waiting: the one offered for Restart to update, or else the newest found, not
    // yet downloaded (its download failing), or else the one this last had.
    string? Held => Ready ?? updater.Found ?? Pending;

    // Where WaitAtMost runs from: the release first found, and no earlier than the person last
    // allowed updates (the switch turned on, or a device's press with it off).
    DateTimeOffset WaitFrom(DateTimeOffset now)
    {
        var from = foundAt ?? now;
        var allowed = allowedSince ?? now;
        return allowed > from ? allowed : from;
    }

    // Why an update has waited longer than WaitAtMost, or null while it has not, or the person
    // chose that (the switch off; a development copy never updates). The machine holding it (the
    // host gone, another profile running, its status unreadable, its sessions unread), or, with
    // nothing holding it, a release downloaded and waiting for the person: a person who never
    // restarts goes as stale as a machine that is stuck, so both are told (#800, SRE on #719).
    string? NeedsYouWhy(DateTimeOffset now)
    {
        if (updater.InstalledVersion is null || Held is null || foundAt is null || now - WaitFrom(now) < WaitAtMost) { return null; }
        if (lastWhyNot is { } why) { return Chosen(why) ? null : why; }
        if (Ready is not null)
        {
            // A device's Install held past it by a usage probe on the host (#238) says so, not the
            // sessions it no longer waits for.
            if (Asked(UpdateAskKind.Install) is not null) { return lastProbe ?? Busyness(); }
            // Install between turns: only the backstop held by a question (or not knowing) keeps
            // it; until then it installs at the first gap, or at ForcedAt.
            if (BetweenTurns) { return now >= ForcedAt(now) ? BackstopHeld() : null; }
            return WaitsForRestart;
        }
        // Not offered: a newer release found since, whose download keeps failing, is the reason,
        // never "waits for Restart" for one the banner no longer offers.
        return updater.Found is { } newest && updater.Ready != newest && updater.RetryAt is not null ? DownloadFailing : null;
    }

    public const string WaitsForRestart = "it is downloaded and waits for Restart to update on this machine";
    public const string DownloadFailing = "its download keeps failing, and is tried again in the background";

    // "Needs you: the update to X has waited since T because Y", or null (NeedsYouWhy). A reminder
    // only: nothing installs because of it.
    string? NeedsYou(DateTimeOffset now) =>
        NeedsYouWhy(now) is { } why ? $"Needs you: the update to {Held} has waited since {WaitFrom(now).ToLocalTime():g} because {why}" : null;

    // A press from another device (UpdateReporter): Check, or Install now. A newer press of the
    // same kind takes over from an older one, whose device then sees another request under way.
    // The tick it starts, which is already done if one was running (that one runs again for it).
    // at: when the device pressed it, as the host says; one older than UpdateProtocol.Lapses has
    // lapsed at the host, and is refused rather than carried out late.
    // now: an Update now (#1159), with its terms; by: the device that pressed it, for the log.
    public Task Ask(UpdateAskKind kind, string request, DateTimeOffset? at = null, UpdateNowAsk? now = null, string? by = null)
    {
        // Only an Install is ever an Update now.
        if (kind != UpdateAskKind.Install) { now = null; }
        // Taken before, by this app or the one before a restart: carried out once.
        var taken = prefs().UpdateTaken ?? [];
        if (taken.Contains(request)) { return Task.CompletedTask; }
        save(prefs() with { UpdateTaken = [.. taken.Append(request).TakeLast(TakenKept)] });
        if (at is { } pressed && time.GetUtcNow() - pressed > UpdateProtocol.Lapses)
        {
            log("update: a press that had lapsed was not carried out");
            Remember(UpdateOutcome.Refused, Lapsed, null, request, now is null ? null : ManageReason.Expired);
            return Task.CompletedTask;
        }
        if (now is { } terms && time.GetUtcNow() >= terms.Until)
        {
            log("update: an Update now past its 15 minutes was not carried out");
            Remember(UpdateOutcome.Refused, Expired, null, request, ManageReason.Expired);
            return Task.CompletedTask;
        }
        lock (asks)
        {
            if (kind == UpdateAskKind.Install)
            {
                // An Update now replacing one held for a usage check keeps the hold's start (set only
                // while a hold runs), so its 11 minutes don't start again (Going, step 2), however
                // many presses replace it; anything else starts afresh, and a press replacing one
                // that waits for a quiet moment is tried at once (its own confirmation is fresh).
                var hold = now is not null && installNow is not null ? usageHoldSince : null;
                installRequest = request;
                installVetted = null;
                (installNow, installBy, usageHoldSince, nowWaitsForQuiet, quietCode) = (now, now is null ? null : by, hold, false, null);
                nowWaiting = hold is not null ? UpdateWaitingCode.UsageCheck : null;
            }
            else { checkRequest = request; }
        }
        // Dropped expired at the end of its life wherever it is, a download under way included.
        if (now is not null) { Expire(now.Until); }
        Keep();
        log($"update: {(now is not null ? "update now" : kind == UpdateAskKind.Install ? "install" : "check")} asked for");
        checkNow = true;
        Changed();
        return TickAsync();
    }

    // How an Update now ends that passed its life (15 minutes from its signature), its download
    // kept and offered as before; and one a Codex usage check held past UsageCheckAtMost.
    public const string Expired = "this Update now was not carried out within 15 minutes of the press; press it again";
    public const string UsageCheckHeld = "a Codex usage check on this machine was still running after 11 minutes, and installing would cut it off; press it again";

    void Changed() => StateChanged?.Invoke();

    // Ends a press, the one that was being carried out (a newer one that came meanwhile is left
    // for the next tick): how it went, kept for the devices, and across the restart an update makes.
    // code: for an Update now, why, as a code the devices word themselves (UpdateResult.Code); a
    // plain Install or Check carries none.
    void Finish(ref string? field, string? request, UpdateOutcome outcome, string? reason, string? to = null, string? code = null)
    {
        if (request is null) { return; }
        var now = false;
        lock (asks)
        {
            if (field != request) { return; }
            if (request == installRequest)
            {
                now = installNow is not null;
                (installNow, installBy, nowWaiting, usageHoldSince, nowWaitsForQuiet, quietCode) = (null, null, null, null, false, null);
            }
            field = null;
        }
        Keep();
        log($"update: asked {outcome.ToString().ToLowerInvariant()}{(reason is null ? "" : $": {reason}")}{(now && code is not null ? $" ({code})" : "")}");
        Remember(outcome, reason, to, request, now ? code : null);
    }

    void Remember(UpdateOutcome outcome, string? reason, string? to, string? request, string? code = null)
    {
        save(prefs() with
        {
            LastUpdate = new UpdateResult { Outcome = outcome, From = updater.InstalledVersion, To = to, Reason = reason, Request = request, Code = code, At = time.GetUtcNow() },
        });
        Changed();
    }

    // The Update now being carried out, past its 15 minutes: dropped expired, wherever it is
    // (downloading, held for a usage check, waiting for a quiet moment), its download kept.
    bool DropExpired()
    {
        UpdateNowAsk? terms;
        string? request;
        bool leaving;
        lock (asks) { (terms, request, leaving) = (installNow, installRequest, installRequest is not null && installRequest == departing); }
        if (terms is null || request is null || leaving || time.GetUtcNow() < terms.Until) { return false; }
        Finish(ref installRequest, request, UpdateOutcome.Refused, Expired, code: ManageReason.Expired);
        return true;
    }

    // At an Update now's end of life: dropped then, on the UI thread, even while a tick is held by a
    // download (the download goes on, and is kept); the tick drops it too, whichever comes first.
    void Expire(DateTimeOffset until)
    {
        var left = until - time.GetUtcNow();
        lock (settleGate)
        {
            expiry?.Dispose();
            expiry = time.CreateTimer(_ => ui.Post(() => DropExpired()), null,
                left > TimeSpan.Zero ? left : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    // A waiting Update now no longer held by a usage check (the check over, or the machine busy
    // again before it was asked): why it waits is the host's again, and the next hold starts afresh.
    void HoldOver()
    {
        lock (asks)
        {
            if (installNow is null) { return; }
            usageHoldSince = null;
            if (nowWaiting == UpdateWaitingCode.UsageCheck) { nowWaiting = quietCode; }
        }
    }

    // Why nothing may be checked for or installed here, as the host last said, or null when it may.
    // Asked: a person pressed Install on another device, which stands in for the switch.
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

    // Idle now and for IdleFor without a break, over a whole snapshot. However long a session stays
    // busy, a device's Install waits for it: before #516 it went ahead after six hours anyway.
    bool Due(DateTimeOffset now) => IdleSince(now, IdleFor);

    bool IdleSince(DateTimeOffset now, TimeSpan span)
    {
        if (!Idle) { idleSince = null; }
        else { idleSince ??= now; }
        return host.Sessions.Complete && idleSince is { } since && now - since >= span;
    }

    // Why the update goes ahead now, or null while it waits: an Update now at once, or at the next
    // quiet moment once the host sent it back; an Install a device pressed, once the
    // host has been idle for IdleFor; or, unasked and set to Install between turns, a gap settled
    // for SettleFor, else the backstop at ForcedAt with no question waiting (BackstopHeld).
    string? GoAhead(bool asked, DateTimeOffset now)
    {
        if (Pending is null) { return null; }
        // An Update now goes at once, skipping IdleFor and the backstop: the person confirmed what
        // it goes over, and the host compares that with what is busy when it acts. Sent back to
        // wait by the host, it goes at the next quiet moment: no turn running and no question
        // waiting over a whole snapshot, settled for SettleFor, never IdleFor or a backstop.
        if (NowPress)
        {
            if (!nowWaitsForQuiet) { return "Update now was pressed on another device"; }
            if (IdleSince(now, SettleFor)) { return "Update now was pressed on another device, and no session here has a turn running or a question waiting"; }
            if (idleSince is not null) { Arm(now); }
            return null;
        }
        // A device's Install keeps its own rule whatever the mode: nobody at the machine was warned,
        // so the host idle for IdleFor, never a settled gap or the backstop (Copilot, r4488279902).
        // Install between turns governs only what installs unasked.
        if (asked) { return Due(now) ? $"another device asked for it and no session has been busy for {IdleFor.TotalMinutes:0} minutes" : null; }
        if (!BetweenTurns || InstallFailedLately(now)) { return null; }
        if (IdleSince(now, SettleFor)) { return "it installs between turns and no session here has a turn running or a question waiting"; }
        if (now >= ForcedAt(now) && BackstopHeld() is null) { return $"it installs between turns and has waited {WaitAtMost.TotalHours:0} hours with a turn always running"; }
        if (idleSince is not null) { Arm(now); }
        return null;
    }

    bool InstallFailedLately(DateTimeOffset now) => FailedRetryAt(now) is not null;

    // A kept retry time beyond Cap (the clock moved back) is written back as Cap from now the first
    // time it is seen, so the deadline stays fixed and elapses instead of moving on every check.
    DateTimeOffset? FailedRetryAt(DateTimeOffset now)
    {
        var p = prefs();
        var capped = UpdateBackoff.Capped(p, now);
        if (!ReferenceEquals(capped, p)) { save(capped); }
        return UpdateBackoff.RetryAt(capped, now, Pending);
    }

    // A session went idle, or the snapshot was read again: set to Install between turns with a
    // release ready, or with an Update now waiting for a quiet moment, a gap starts, and the tick
    // is due once it has settled.
    void Gap()
    {
        if (Pending is null || !(BetweenTurns || (NowPress && nowWaitsForQuiet)) || !Idle) { return; }
        var now = time.GetUtcNow();
        idleSince ??= now;
        Arm(now);
    }

    void Arm(DateTimeOffset now)
    {
        if (idleSince is not { } since) { return; }
        // Already settled and still not installed (refused, say): the next minute's tick looks
        // again, rather than a tick that arms another at once.
        var left = since + SettleFor - now;
        if (left <= TimeSpan.Zero) { return; }
        lock (settleGate)
        {
            settle?.Dispose();
            settle = time.CreateTimer(_ => ui.Post(() => _ = TickAsync()), null, left, Timeout.InfiniteTimeSpan);
        }
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
        if (!work.Wait(0)) { Volatile.Write(ref again, 1); return; }
        try
        {
            do
            {
                Volatile.Write(ref again, 0);
                await TickOnceAsync();
            }
            while (Volatile.Read(ref again) == 1);
        }
        finally { work.Release(); }
        // One that arrived between the last look and letting go.
        if (Volatile.Read(ref again) == 1) { await TickAsync(); }
    }

    async Task TickOnceAsync()
    {
        // Restart to update (or Settings' path) is installing outside the tick: nothing here may
        // check, download or install meanwhile, nor take that install's flag or its press.
        if (installing || manager.UpdateUnderWay || Volatile.Read(ref claimed) == 1) { return; }
        try
        {
            SourceMoved();
            HoldOverIfCheckedSince();
            if (updater.InstalledVersion is null)
            {
                Finish(ref installRequest, Asked(UpdateAskKind.Install), UpdateOutcome.Refused, NotInstalled, code: ManageReason.Unavailable);
                Finish(ref checkRequest, Asked(UpdateAskKind.Check), UpdateOutcome.Refused, NotInstalled);
            }
            DropExpired();
            // An install asked for is refused now, rather than after the download and the wait,
            // if it could never go ahead: the host gone, or another profile running from here.
            if (Asked(UpdateAskKind.Install) is { } install && installVetted != install)
            {
                installVetted = install;
                if (await VetAsync(asked: true) is ({ } refused, var code)) { Finish(ref installRequest, install, UpdateOutcome.Refused, refused, code: code); }
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
            // Nothing is checked for when the person turned downloads off (or this is a development
            // copy), unless a device asked for a check. The machine's own refusals (the host gone,
            // another profile running) never stop checks and downloads: a release found behind one
            // is downloaded and offered, rather than held silently (#800).
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
            else { checkDue = checkedAt is null || now - checkedAt >= (hostFailed ? CheckAfterFailedHost : CheckEvery); }
            if (checkNow || checkDue)
            {
                checkNow = false;
                checkedAt = now;
                await CheckAsync(now);
            }
            // A download that failed is tried again as soon as its hold ends (ObservedUpdater),
            // rather than at the next check, without asking the feed again (#530).
            else if (updater.Found is { } found && updater.Ready != found && updater.RetryAt is { } retry && now >= retry)
            {
                await updater.DownloadAsync(_ => { });
                if (updater.Ready == found) { Downloaded(found); }
                Changed();
            }
            // A connected host whose sessions are still not read in full holds a device's Install
            // (Due) as the machine's refusal, told as one, though the press stays queued (#800).
            // Not an Update now: the host compares what is busy with the confirmation when it acts,
            // and a machine it can't read sends the press back to wait (unknown).
            if (why is null && asked && !NowPress && Pending is not null && !host.Sessions.Complete) { why = SessionsUnread; }
            // A download can take minutes: an Update now past its life by then goes no further.
            if (DropExpired()) { asked = Asked(UpdateAskKind.Install) is not null; }
            now = time.GetUtcNow();
            if (Held is null) { foundAt = null; }
            else { foundAt ??= now; }
            // Waited past WaitAtMost with nothing refusing so far: the machine is asked whether
            // anything else keeps it from the person (another profile running from here, which
            // Restart to update would refuse, or the host's status unreadable), as it is before any
            // install. Asked every minute while that refusal lasts, so its end is seen, and
            // otherwise every CheckEvery: a release simply waiting for Restart is no refusal.
            if (why is null && !asked && Held is not null && foundAt is not null && now - WaitFrom(now) >= WaitAtMost
                && (lastWhyNot is not null || probedAt is not { } probed || now - probed >= CheckEvery))
            {
                probedAt = now;
                why = await WhyNotNowAsync();
            }
            if (Refused(why)) { idleSince = null; Changed(); return; }
            // Set to Restart to update nothing installs by itself (#516): only an Install a device
            // pressed goes ahead here, and Restart to update at this machine (RestartToUpdateAsync).
            // Set to Install between turns, a gap does, or the backstop (GoAhead).
            // No longer going ahead (a session busy again): what holds it is that, not a probe from
            // before (#238).
            if (GoAhead(asked, time.GetUtcNow()) is null) { lastProbe = null; HoldOver(); Changed(); return; }
            // Settings shares the updater: a check there, or a change of feed, may have replaced
            // the release downloaded here. Then it is found and downloaded again first.
            if (Stale()) { return; }
            // Asked again now: the host still there (its own status, not the last report) and no
            // other profile's app or host running from the package.
            var pressed = Asked(UpdateAskKind.Install);
            var (whyNow, codeNow) = await VetAsync(pressed is not null);
            if (Refused(whyNow))
            {
                Finish(ref installRequest, pressed, UpdateOutcome.Refused, lastWhyNot, code: codeNow);
                return;
            }
            // Those took time: the press replaced or ended, or the host gone, meanwhile, counts too.
            asked = Asked(UpdateAskKind.Install) is not null;
            if (Refused(WhyNot(asked))) { return; }
            if (GoAhead(asked, time.GetUtcNow()) is not { } because) { HoldOver(); Changed(); return; }
            // And Settings may have checked, or changed the feed, meanwhile (WhyNot itself reads the
            // feed): still the release downloaded, or nothing is stopped. Should it change even
            // while the host stops, ApplyAndRestart refuses it and the host is started again.
            if (Stale()) { return; }
            // And not while a usage probe tied to the host is running or in its grace (#238): the
            // host would be stopped under it. The host starts no new one while this is asked
            // every tick; the install goes ahead at the first tick after it is done, if still due.
            if (await ProbeBusyAsync() is { } probe)
            {
                if (probe != lastProbe) { log($"auto-update: waiting to install {Pending}: {probe}"); }
                lastProbe = probe;
                // An Update now waits in usage-check, a hold of its own that spends no restart slot
                // and touches no lease, for at most UsageCheckAtMost from when it began (a new press
                // replacing it there keeps that start), then ends refused usage-check; or expired,
                // should its own life end first.
                if (NowPress && !DropExpired())
                {
                    var at = time.GetUtcNow();
                    string? request;
                    DateTimeOffset since;
                    lock (asks)
                    {
                        request = installRequest;
                        since = usageHoldSince ??= at;
                        nowWaiting = UpdateWaitingCode.UsageCheck;
                    }
                    if (at - since >= UsageCheckAtMost) { Finish(ref installRequest, request, UpdateOutcome.Refused, UsageCheckHeld, code: UpdateCode.UsageCheck); }
                }
                Changed();
                return;
            }
            lastProbe = null;
            // The hold over: what is busy is compared with the confirmation again, by the host when
            // it is asked to leave (below), before the lease.
            HoldOver();
            // Asking took time too (up to its call timeout): the same checks again, as of now. The
            // press they are made for is the one carried out: should another replace it while they
            // are made, the tick queued for it looks again, so an Install never goes at once as an
            // Update now does, nor an Update now without its departure.
            string? go;
            UpdateNowAsk? goNow;
            lock (asks) { (go, goNow) = (installRequest, installNow); }
            asked = go is not null;
            if (Refused(WhyNot(asked)) || GoAhead(asked, time.GetUtcNow()) is not { } still || Stale()) { return; }
            lock (asks) { if (installRequest != go || installNow != goNow) { return; } }
            because = still;
            // The person's Restart to update may have begun while those were asked: one install at
            // a time, claimed as Restart to update claims it.
            if (installing || manager.UpdateUnderWay || Interlocked.Exchange(ref claimed, 1) == 1) { return; }
            try
            {
                if (goNow is not null) { await DepartAsync(because); }
                else { await InstallAsync(because, (go, false)); }
            }
            finally { Volatile.Write(ref claimed, 0); }
        }
        // A check that cannot reach the network, a download cut off: tried again at the next check.
        // InstallAsync handles its own failures, so the installing flag is not this catch's.
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // Said once, not at every press answered with the same failure.
            if (e.Message != lastFailure) { log($"auto-update: {e.Message}"); }
            lastFailure = e.Message;
            checkedAt = time.GetUtcNow();
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
            if (lastCheckAt is { } at && lastCheckSource == updater.Source && now - at < CheckAgainAfter)
            {
                if (lastCheckFailed is { } failed && !(updater.CheckedAt >= at)) { throw new InvalidOperationException(failed); }
                found = updater.Found;
            }
            else
            {
                // Held off after the feed's rate limit: asking again only extends it (#800).
                if (checksHeldUntil is { } until && now < until) { throw new InvalidOperationException(checksHeld); }
                var from = updater.Source;
                try { found = await updater.CheckAsync(); }
                catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
                {
                    if (RateLimited(e, out var retryAt))
                    {
                        var held = HoldChecks(now, retryAt, e.Message);
                        (lastCheckAt, lastCheckFailed, lastCheckSource) = (now, held, from);
                        throw new InvalidOperationException(held, e);
                    }
                    (lastCheckAt, lastCheckFailed, lastCheckSource) = (now, e.Message, from);
                    throw;
                }
                (rateLimited, checksHeldUntil, checksHeld) = (0, null, null);
                // As of when it was asked: a press after that, a minute on, asks again.
                (lastCheckAt, lastCheckFailed, lastCheckSource) = (now, null, from);
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
        if (found is not null) { Downloaded(found); }
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

    async Task<string?> WhyNotNowAsync(bool asked = false) => (await VetAsync(asked)).Why;

    // Why not now, with it as an Update now's code (UpdateResult.Code): another profile running from
    // this installation other-profile, a machine read-only for other devices now read-only, and
    // anything else (a development copy, the host gone or its status unreadable) unavailable.
    async Task<(string? Why, string? Code)> VetAsync(bool asked = false)
    {
        if (WhyNot(asked) is { } why) { return (why, ManageReason.Unavailable); }
        HostStatus fresh;
        try { fresh = await host.CallAsync<HostStatus>(HostOps.Status, timeout: TimeSpan.FromSeconds(10)); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            return ($"the host's status could not be read: {e.Message}", ManageReason.Unavailable);
        }
        // A press from another device is not carried out on a machine that has become read-only
        // for other devices since (an automatic update still is: the rule guards other devices).
        if (asked && fresh.ReadOnly is { } readOnly) { return ($"this machine is read-only for other devices now: {readOnly}", ManageReason.ReadOnly); }
        if (await Task.Run(() => othersInPackage(fresh.ProcessId)) is { } others) { return (others, UpdateCode.OtherProfile); }
        return (null, null);
    }

    // The source changed since this last looked (Include pre-releases, or feed.txt): what was
    // checked, failed, found or downloaded on the old one answers nothing for the new, so it goes, all together. Before
    // this, a device's Check within a minute of a check on the old source was answered from it
    // (nothing found, the old source's Found having been cleared) and its Install dropped.
    void SourceMoved()
    {
        var now = updater.Source;
        if (seenSource == now) { return; }
        seenSource = now;
        (lastCheckAt, lastCheckFailed, checkedAt, Pending) = (null, null, null, null);
        // A rate-limit hold is the old source's answer too (a feed.txt elsewhere has no such
        // limit); rateLimited stays, so a GitHub source that refuses again is held off longer.
        (checksHeldUntil, checksHeld) = (null, null);
        // The new source's release waits from when it is found.
        foundAt = null;
        log("auto-update: the update source changed; it is checked afresh");
    }

    bool Stale()
    {
        if (updater.Ready == Pending) { return false; }
        log($"auto-update: {Pending} is no longer the release downloaded; checking again");
        Pending = null;
        checkedAt = null;
        return true;
    }

    // A release downloaded: ready for the person, on the banner and in Settings. A newer one
    // downloaded while an older one waited takes its place.
    void Downloaded(string found)
    {
        // Only a package the updater has ready: a download Settings' newer find overtook can end
        // without it, and offering it would invite a restart that installs nothing.
        if (updater.Ready != found || found == Pending) { return; }
        Pending = found;
        hostFailed = false;
        log($"auto-update: {found} downloaded; it waits for Restart to update");
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
        // The machine no longer refusing: ReturnGrace before the backstop (ForcedAt).
        if (why is null && lastWhyNot is { } before && !Chosen(before)) { returnedAt = now; }
        lastWhyNot = why;
        if (why is null) { saidAt = null; }
        if (why != HostGone) { (reattachAt, reattachWait) = (null, ReattachFirst); }
        if (why is not null && Chosen(why)) { allowedSince = null; }
        else { allowedSince ??= now; }
        // Not forgotten when the refusal lifts for a moment: a link that flaps tells the owner no
        // more than daily either.
        if (NeedsYou(now) is { } line && (neededYouAt is not { } told || now - told >= NeedsYouAgainAfter))
        {
            neededYouAt = now;
            log($"auto-update: {line}");
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
        // Its end, not its start: only a check that succeeded after the refusal ends its hold
        // (Copilot, 31b83040). The shared updater now runs checks one at a time (ObservedUpdater),
        // so Settings' can no longer succeed while this one is under way.
        heldAt = time.GetUtcNow();
        checksHeld = $"the release feed's rate limit refused the check ({failure}); checking again after {until.ToLocalTime():t}";
        // Not over an outcome the devices may not have seen yet: the update just made (its first
        // check, right after the restart, is the likeliest to be refused) or a press answered.
        var last = prefs().LastUpdate;
        var unseen = last is { } l && (l.Outcome == UpdateOutcome.Updated || l.Request is not null) && now - l.At < NeedsYouAgainAfter;
        if (!unseen && Asked(UpdateAskKind.Check) is null && Asked(UpdateAskKind.Install) is null) { Remember(UpdateOutcome.Failed, checksHeld, null, null); }
        return checksHeld;
    }

    // A check that succeeded since the hold began (Settings', through the shared updater): the feed
    // answers again, so the hold, its cached failure and its backoff end, and the schedule runs on
    // from that check, rather than a press or the next check waiting out the old deadline (#934).
    // Only an answer as fresh as that: a copy older than the refusal (the machine's shared one, or
    // the CDN's, #927) says nothing about whether the feed answers now (Platform, 6711518771).
    void HoldOverIfCheckedSince()
    {
        if (checksHeldUntil is null || heldAt is not { } failed || updater.CheckedAt is not { } ok || ok <= failed
            || updater.FoundAsOf is not { } fresh || fresh <= failed) { return; }
        (checksHeldUntil, checksHeld, heldAt, rateLimited, lastCheckFailed) = (null, null, null, 0, null);
        (lastCheckAt, checkedAt) = (ok, ok);
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

    // The person at this machine pressed Restart to update (the banner, or Settings): the release
    // downloaded is installed now. Never one it did not download, nor while another profile's app
    // or host runs from this installation (Update.exe would stop that too). confirm(warning) is
    // asked last, after every probe and just before the host stops, whenever a session here is
    // waiting on the person or has a turn running (WaitingOnYou): what was idle when they pressed
    // may not be by then. An Install a device pressed and not yet carried out is answered by it.
    // Null once the release has been handed to Velopack (which ends this process); NotNow if the
    // person chose not to; otherwise why it did not install, still on this version, host back.
    public async Task<string?> RestartToUpdateAsync(Func<string, Task<bool>>? confirm = null)
    {
        if (updater.InstalledVersion is null) { return NotInstalled; }
        // One install at a time, this one or a device's from the tick: the loser must neither
        // clear the winner's flag nor answer its press (HostManager refuses it only once stopping).
        if (Interlocked.Exchange(ref claimed, 1) == 1) { return "an update is already being installed"; }
        var recheck = false;
        var held = false;
        try
        {
            if (installing || manager.UpdateUnderWay) { return "an update is already being installed"; }
            // A tick already under way (a check, a download) finishes first, since it may replace or
            // drop the release downloaded; none starts after it until this is done (claimed).
            await work.WaitAsync();
            held = true;
            if (installing || manager.UpdateUnderWay) { return "an update is already being installed"; }
            SourceMoved();
            // Downloaded by Settings since the last tick: that is the one to install.
            if (updater.Ready is { } downloaded && downloaded != Pending)
            {
                Pending = downloaded;
                // The banner shows it now, whether or not this goes on to install it.
                Changed();
            }
            if (Pending is not { } to) { return "no update has been downloaded yet"; }
            if (Stale()) { return Again(to); }
            if (await OthersAsync() is { } others) { return others; }
            if (WaitingOnYou is { } warning && confirm is not null)
            {
                if (!await confirm(warning)) { return NotNow; }
                // The dialog may have stayed open a long while: another profile may have started
                // from this installation since (Velopack would stop it too), or Settings installed.
                if (installing || manager.UpdateUnderWay) { return "an update is already being installed"; }
                if (await OthersAsync() is { } since) { return since; }
            }
            // Settings may have checked, or changed the feed, meanwhile: still the very release
            // read above, or nothing is stopped.
            if (Stale() || Pending != to) { return Again(to); }
            return await InstallAsync("the person chose Restart to update");
        }
        finally
        {
            if (held) { work.Release(); }
            Volatile.Write(ref claimed, 0);
            // Checked for and downloaded again at once, or a press that came meanwhile, now the
            // tick may run.
            if (recheck || Volatile.Read(ref again) == 1) { _ = TickAsync(); }
        }

        // The newest is downloaded at once only while downloads are automatic: otherwise nothing
        // fetches it until the person checks in Settings, so that is what they are told.
        string Again(string to)
        {
            recheck = prefs().AutoUpdate;
            return $"the update downloaded has changed since ({to} is no longer the newest for this machine): "
                + (recheck ? "the newest is being downloaded now, so try again in a moment" : "check for updates in Settings to download the newest");
        }

        // The host's own word on its process, so its pid is not counted as another profile's; a
        // host not running holds nothing to lose, and is started by the new version.
        async Task<string?> OthersAsync()
        {
            int? pid = null;
            if (host.State == LinkState.Connected)
            {
                try { pid = (await host.CallAsync<HostStatus>(HostOps.Status, timeout: TimeSpan.FromSeconds(10))).ProcessId; }
                catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { pid = host.Status?.ProcessId; }
            }
            if (await Task.Run(() => othersInPackage(pid)) is not { } others) { return null; }
            log($"auto-update: not restarting to update: {others}");
            return others;
        }
    }

    sealed class PackageReplacedException() : Exception("the package ready changed while the host stopped");

    // An Update now, its download checked and its probe hold over (Going, step 3): the release it
    // goes to must be the one the person was shown (else release-changed), and nothing may keep it
    // from replacing the installation (asked before the host is asked to leave, so a departure is
    // never spent on an update that can't finish); then the host is asked to leave
    // (host.update.depart), which takes #805's lease, compares what is busy with the confirmation,
    // announces and commits. Committed, the host is stopped and the release installed, as every
    // install is. Waiting, the download is kept and the press waits for the next quiet moment.
    // Refused, it ends refused with the host's code (or expired, its life having ended meanwhile),
    // the release still offered. A host that can't be asked, or doesn't answer, ends it refused
    // unavailable and nothing is installed: one that committed unseen is restarted by its own
    // watchdog (departure-stuck).
    async Task DepartAsync(string because)
    {
        UpdateNowAsk? terms;
        string? request, by;
        lock (asks) { (terms, request, by) = (installNow, installRequest, installBy); }
        if (terms is null || request is null || DropExpired()) { return; }
        if (Pending != terms.Release)
        {
            Finish(ref installRequest, request, UpdateOutcome.Refused, $"the release ready here ({Pending}) is not the one the person was shown ({terms.Release})",
                code: UpdateCode.ReleaseChanged);
            return;
        }
        string? blockers;
        try { blockers = preflight is null ? null : await Task.Run(preflight); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { blockers = e.Message; }
        if (blockers is not null)
        {
            Finish(ref installRequest, request, UpdateOutcome.Refused, $"{UpdateBlockedException.Prefix}{blockers}", code: ManageReason.Unavailable);
            return;
        }
        // Replaced, or dropped expired, while that was asked: the press the person made last is
        // the one carried out, by the tick queued for it. Otherwise it is departing from here on,
        // and only the host's answer, then the install, ends it.
        lock (asks)
        {
            if (installRequest != request || installNow != terms) { return; }
            departing = request;
        }
        UpdateDepartResult left;
        try { left = await host.CallAsync<UpdateDepartResult>(HostOps.UpdateDepart, new UpdateDepartRequest(request), timeout: DepartWait); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            Undepart();
            if (DropExpired()) { return; }
            Finish(ref installRequest, request, UpdateOutcome.Refused, $"the host could not be asked to leave for the update: {e.Message}", code: ManageReason.Unavailable);
            return;
        }
        if (left.Outcome != UpdateDepartOutcome.Committed) { Undepart(); }
        switch (left.Outcome)
        {
            case UpdateDepartOutcome.Committed:
                var accept = terms.Accept;
                log($"update now: pressed on {by ?? "another device"}, going over {(accept is null ? "nothing busy" : $"{accept.Working} with a turn running and {accept.Waiting} with a question or approval waiting{(accept.Unknown == true ? ", with sessions not yet reported" : "")}")}");
                // The host left for this press: it is the one installed and answered, even should
                // another have replaced it while the host was asked.
                await InstallAsync(because, (request, true));
                return;
            case UpdateDepartOutcome.Waiting:
                var code = UpdateProtocol.IsCode(left.Code) ? left.Code : UpdateWaitingCode.Unknown;
                lock (asks)
                {
                    if (installRequest != request) { return; }
                    (nowWaiting, quietCode, nowWaitsForQuiet) = (code, code, true);
                }
                // What the app saw idle before the host found it busy is no evidence about the
                // moment after: a quiet moment is one seen from now, and one that has already
                // begun is settled from now rather than missed until the next session change.
                idleSince = null;
                Gap();
                log($"update now: the host found the machine busier than confirmed ({code}); it installs at the next quiet moment");
                Changed();
                // Its life may have ended while the host was asked.
                DropExpired();
                return;
            default:
                // Its life ended while the host was asked: that is how it ended, wherever it was.
                if (DropExpired()) { return; }
                var refused = UpdateProtocol.IsCode(left.Code) ? left.Code! : ManageReason.Unavailable;
                Finish(ref installRequest, request, UpdateOutcome.Refused, refused == ManageReason.Expired ? Expired : $"the host did not leave for the update: {refused}", code: refused);
                return;
        }

        void Undepart() { lock (asks) { if (departing == request) { departing = null; } } }
    }

    // The person, warned what a restart would interrupt, chose not to: nothing to report.
    public const string NotNow = "not now";
    int claimed;

    // Null once the release has been applied (which, for real, ends this process); otherwise why
    // not, still on this version with the host back. answers: the press this install answers, as
    // the tick decided it, and whether the host departed for it (an Update now), which it then
    // answers whatever press has come since; else the one being carried out now.
    async Task<string?> InstallAsync(string because, (string? Request, bool Departed)? answers = null)
    {
        var to = Pending!;
        var source = updater.Source;
        var request = answers is { } pinned ? pinned.Request : Asked(UpdateAskKind.Install);
        var departed = answers is { Departed: true } ? request : null;
        log($"auto-update: installing {to}, as {because}");
        bool open;
        try { open = WindowOpen(); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { open = false; }
        updatingIn = open ? to : null;
        installing = true;
        Changed();
        // The host comes back from a failed install, or from the app quitting mid-way (HostManager).
        try
        {
            await manager.StopAndApplyAsync(() => Record(prefs, save, updater, request, open), () =>
            {
                // Settings' checks and downloads do not wait for this one: one may have replaced the
                // package while the host stopped, and Velopack applies whatever is ready. Only the
                // release chosen, from the source it was chosen on, is applied (Copilot, r4487830884).
                if (updater.Ready != to || updater.Source != source) { throw new PackageReplacedException(); }
                updater.ApplyAndRestart(to, HandedOver(prefs, save));
            }, preflight);
            return null;
        }
        // Replaced while the host stopped: nothing failed, and the host is back (HostManager). What
        // is ready now is offered instead, with its own notes when it is installed.
        catch (PackageReplacedException)
        {
            save(prefs() with { UpdatedFrom = null, UpdateNotes = null, UpdateRequest = null, UpdateWindowOpen = false, UpdateHandedOver = false });
            installing = false;
            Pending = null;
            checkedAt = null;
            if (seenSource == updater.Source && updater.Ready is { } now) { Downloaded(now); }
            log($"auto-update: not installing {to}: the package ready changed while the host stopped; {(Pending is { } next ? $"{next} is offered instead" : "checking again")}");
            // The host left for an Update now's press, which can't leave again: it ends here.
            if (departed is not null) { End(UpdateOutcome.Refused, "the release ready changed while the host stopped", UpdateCode.ReleaseChanged); }
            Changed();
            return $"the update downloaded changed while the host stopped ({to} is no longer the one ready): "
                + (Pending is { } offered ? $"{offered} is ready now, so choose Restart to update again" : "it is being checked for again");
        }
        // Another install got there first (Settings' Restart to update, or this one's other path):
        // that one is installing, and recorded it.
        catch (UpdateUnderWayException e)
        {
            const string already = "an update is already being installed on this machine";
            log($"auto-update: not installing {to}: {e.Message}");
            installing = false;
            End(UpdateOutcome.Refused, already, ManageReason.Busy);
            Changed();
            return already;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // Still this version: nothing to announce. Still offered while the updater has it
            // downloaded, for the person to try again when they choose; never tried again by itself.
            save(prefs() with { UpdatedFrom = null, UpdateNotes = null, UpdateRequest = null, UpdateWindowOpen = false, UpdateHandedOver = false });
            if (updater.Ready != to) { Pending = null; }
            checkedAt = time.GetUtcNow();
            save(UpdateBackoff.Failed(prefs(), checkedAt.Value, to, out var retryAt));
            log($"auto-update: installing {to} failed: {e.Message}; update failed, retrying at {retryAt.ToLocalTime():t}");
            installing = false;
            Changed();
            // Kept for the devices whether a device asked for it or the person here did: the old
            // release still runs, and no later start would say so.
            if (request is null) { Remember(UpdateOutcome.Failed, e.Message, to, null); }
            else { End(UpdateOutcome.Failed, e.Message); }
            return $"installing {to} failed: {e.Message}";
        }

        // How the press this answered ended: the one being carried out, or the Update now the host
        // left for, kept for its device even should a newer press have replaced it since.
        void End(UpdateOutcome outcome, string reason, string? code = null)
        {
            if (departed is not null && Asked(UpdateAskKind.Install) != departed)
            {
                log($"update: asked {outcome.ToString().ToLowerInvariant()}: {reason} ({code ?? "no code"})");
                Remember(outcome, reason, to, departed, code);
                return;
            }
            Finish(ref installRequest, request, outcome, reason, to, code);
        }
    }

    // Before an update is applied, by the app or from Settings: what the new version's first start
    // announces, and the press from another device it answers, if one asked for it. From Settings,
    // that is the Install a device pressed and the app has not yet carried out: Settings installs
    // the release it would have.
    public static void Record(Func<AppPreferences> prefs, Action<AppPreferences> save, IUpdater updater, string? request = null, bool windowOpen = false) =>
        save(UpdateBackoff.Attempting(prefs(), updater.Ready) with
        {
            UpdatedFrom = updater.InstalledVersion,
            UpdateNotes = updater.PendingNotes,
            UpdateRequest = request ?? prefs().UpdateInstallAsked,
            UpdateWindowOpen = windowOpen,
            UpdateHandedOver = false,
        });

    // Once Update.exe has started (IUpdater.ApplyAndRestart's handedOver): an install was attempted,
    // so a next start still on this version says it couldn't install (#1167).
    public static Action HandedOver(Func<AppPreferences> prefs, Action<AppPreferences> save) =>
        () => save(prefs() with { UpdateHandedOver = true });

    // Settings' Update and restart failed, the old release still running: kept for the devices as
    // an automatic install that fails is. The press a device made, if Settings took it on, is left
    // to the app, which still carries it out. recorded: Settings had recorded the install (Record),
    // whose markers go with it; a download that failed before then leaves them alone, since they
    // may be an automatic install's, under way meanwhile.
    public static void Failed(Func<AppPreferences> prefs, Action<AppPreferences> save, IUpdater updater, string? to, string reason, bool recorded = true,
        TimeProvider? time = null)
    {
        var result = new UpdateResult { Outcome = UpdateOutcome.Failed, From = updater.InstalledVersion, To = to, Reason = reason, At = (time ?? TimeProvider.System).GetUtcNow() };
        save(recorded ? prefs() with { UpdatedFrom = null, UpdateNotes = null, UpdateRequest = null, UpdateWindowOpen = false, UpdateHandedOver = false, LastUpdate = result } : prefs() with { LastUpdate = result });
        (updater as ObservedUpdater)?.Noted();
    }

    // The first start after an update: what it came to, once, and how it went kept for the
    // account's other devices (State.Last), the press it answers with it. Where the window was open
    // when the update was handed over, the app opens it again and says it there (the Updated bar,
    // or why it couldn't install, with Try again); otherwise a toast does (#1167). A failed install
    // is never silent: Update.exe, silent, says nothing itself, and restarts this old version.
    public static UpdateAnnouncement? Announce(Func<AppPreferences> prefs, Action<AppPreferences> save, IUpdater updater, IToastSink sink, Action<string> log,
        TimeProvider? time = null, Func<string?>? blockers = null)
    {
        if (prefs().UpdatedFrom is not { } from) { return null; }
        var notes = prefs().UpdateNotes;
        var open = prefs().UpdateWindowOpen;
        var attempted = prefs().UpdateFailedRelease;
        var handedOver = prefs().UpdateHandedOver;
        var now = updater.InstalledVersion;
        var took = now is not null && now != from;
        var at = (time ?? TimeProvider.System).GetUtcNow();
        // Still this version, and Update.exe never started (the app quit or ended between recording
        // the install and handing it over): no install was attempted, so there is nothing to tell
        // the person (Creative on #1181), only the log and the devices.
        var aborted = !took && !handedOver;
        save(prefs() with
        {
            UpdatedFrom = null,
            UpdateNotes = null,
            UpdateRequest = null,
            UpdateWindowOpen = false,
            UpdateHandedOver = false,
            LastUpdate = new UpdateResult
            {
                Outcome = took ? UpdateOutcome.Updated : UpdateOutcome.Failed,
                From = from,
                To = now,
                Reason = took ? null : aborted ? "the update was not handed to the installer: the app ended first" : $"the update did not take: still {now ?? "not installed"}",
                Request = prefs().UpdateRequest,
                At = at,
            },
        });
        if (aborted)
        {
            log($"the update from {from}{(attempted is null ? "" : $" to {attempted}")} was not handed to the installer: the app ended first; nothing was installed");
            return null;
        }
        if (!took)
        {
            // Update.exe failed after this app's earlier run ended (error 5 moving a held file, say)
            // and started this one: the failure and its next try are kept (UpdateBackoff), and what
            // holds the files is named, best effort.
            save(UpdateBackoff.Failed(prefs(), at, prefs().UpdateFailedRelease, out var retryAt));
            string? held = null;
            try { held = blockers?.Invoke(); }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { }
            log($"the update from {from} did not take: still {now ?? "not installed"}{(held is null ? "" : $"; {UpdateBlockedException.Prefix}{held}")}; update failed, retrying at {retryAt.ToLocalTime():t}");
            var failed = new UpdateAnnouncement(false, from, now ?? from, attempted, open);
            if (!open && prefs().Toasts) { sink.Show(new ToastNotice("agent-bridge-updated", ToastXml.CouldNotInstall(failed.Message), Quiet: true)); }
            return failed;
        }
        save(UpdateBackoff.Cleared(prefs()));
        log($"updated from {from} to {now}");
        if (!open && prefs().Toasts) { sink.Show(new ToastNotice("agent-bridge-updated", ToastXml.Updated(now!, from, notes), Quiet: true)); }
        return new UpdateAnnouncement(true, from, now!, attempted, open);
    }
}

// What the first start after an update came to (AutoUpdater.Announce). Took: the version changed.
// Now: the version running; Attempted: the one being installed, when known. WindowOpen: the
// window was open when the update was handed over, so the app opens it again and says it there.
public sealed record UpdateAnnouncement(bool Took, string From, string Now, string? Attempted, bool WindowOpen)
{
    // The failure in the person's words (#1167), from versions alone: never an error's own text.
    public string Message => Took
        ? $"Updated to version {Now}."
        : Attempted is { } v && v != Now
            ? $"Couldn't install version {v}. {Product.Name} is still on {Now}."
            : $"Couldn't install the update. {Product.Name} is still on {Now}.";
}
