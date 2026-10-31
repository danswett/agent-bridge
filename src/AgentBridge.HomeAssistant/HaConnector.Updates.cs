using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

// A release newer than the one installed: its version, notes and page, and whatever the updater
// that found it needs to download and apply exactly that release (opaque to the connector).
public sealed record UpdateRelease(string Version, string? Notes, string? Url)
{
    public object? Handle { get; init; }
}

// The host's own updater, as the connector drives it. The host is updated with the app that
// carries it (Velopack, docs/app-windows.md); a copy its updater did not install (a build folder,
// a platform it does not package) has no Installed, and the machine's update controls are not
// served.
public interface IHostUpdates
{
    // The version installed, or null when this copy is not one its updater can update.
    string? Installed { get; }
    // The releases page, for release_url when no particular release is known.
    string? ReleasesUrl { get; }
    // The newest release when it is newer than the one installed; null when current.
    Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(UpdateRelease release, Action<int> progress, CancellationToken cancellationToken);
    // Hands the downloaded release to the updater, which applies it once this process has exited,
    // and has the host exit. Throws when it cannot be handed over.
    void ApplyAndRestart(UpdateRelease release);
}

// Stage 6: the machine's update entity and its Install and Check buttons (docs/ha-connector.md,
// "Stage 6"), as agent-ha-bridge's Sync-DaemonUpdateStatus serves them for its own install
// (Fixtures/ha-update.json): a check every UpdateCheck and on a press of Check, a press of Install
// taken through checking, downloading and restarting into the new release, and how that ended
// said on the entity and in a notification once the host is back. What is checked and installed is
// this host's own product (design G2): the dashboard's update row for a migrated machine is about
// agent-bridge, never agent-ha-bridge.
public sealed partial class HaConnector
{
    readonly IHostUpdates? updates;
    readonly CancellationTokenSource updateCancel = new();
    readonly SemaphoreSlim updatePublish = new(1, 1);
    // Decided at start: whether this machine's update buttons are this connector's to serve.
    bool updateServing;
    // Presses at or before this are history: a retained value from an earlier run, not an
    // instruction (Test-DaemonUpdateCheckPress compares with the daemon's start the same way, so
    // a press made moments after a restart still counts).
    DateTimeOffset updateStartedAt;
    // Whether that instant has been taken since the connector started or was paused: a start that
    // fails and is tried again keeps it, so a press made on the buttons it had already published,
    // while it waited to try again, still counts.
    bool updateCutoffTaken;
    string? installPressSeen;
    string? checkPressSeen;
    int updateTouched;
    DateTimeOffset nextUpdateSweep;
    DateTimeOffset nextUpdateCheck;
    Task<UpdateRelease?>? updateCheck;
    bool updateCheckPressed;
    // A press of Check that came while a check was already under way: answered by a check of its
    // own once that one is done, not by one begun before it.
    bool updateCheckQueued;
    // What the last check found: latest unknown (null) when it could not be established.
    string? updateLatest;
    string updateNotes = "";
    string? updateUrl;
    // How the last attempt ended, while it is still news (Set-DaemonUpdateVerdict).
    (string Stage, string Detail, DateTimeOffset Until)? updateVerdict;
    // Where an install has got to while it runs.
    (string Stage, double? Proportion)? updateProgress;
    Task? installing;
    // The release this host is restarting into, kept in the ledger so the host that comes back
    // can say how it ended (Invoke-DaemonUpdateOutcome's outcome file).
    UpdateAttempt? updateAttempt;
    bool updateAttemptHere;
    // An attempt an earlier host recorded, whose outcome this one has still to tell. Apart from
    // updateAttempt, so an install pressed meanwhile that fails does not take it away. The ledger
    // holds one: an install handed over records its own, whose outcome the next host tells.
    UpdateAttempt? updateOutcome;
    // A notice Home Assistant did not take, tried again until it does or a newer one replaces it
    // (they share one notification_id, so only the newest means anything).
    (string Title, string Message)? noticePending;
    DateTimeOffset nextNoticeTry;
    string? updateSignature;

    sealed record UpdateAttempt(string Target, string Before, DateTimeOffset At, string? Url);

    string UpdateEntity(string key) => HaContract.MachineEntityId("button", key, Slug);

    // Asked of the updater once per start: it reads the package's files. Known whether or not
    // checks are on, so an update's outcome is told even after they were turned off.
    string? updateInstalled;
    DateTimeOffset nextOutcomeTry;
    string Installed => updateInstalled ?? options.Version;

    // A change the watch reported on the Install or Check button.
    bool UpdateHit(HaTrigger hit)
    {
        if (!updateServing) { return false; }
        if (hit.EntityId != UpdateEntity("install_update") && hit.EntityId != UpdateEntity("check_updates")) { return false; }
        Interlocked.Exchange(ref updateTouched, 1);
        return true;
    }

    IEnumerable<string> UpdateWatched() =>
        updateServing ? [UpdateEntity("check_updates"), UpdateEntity("install_update")] : [];

    // StartupAsync's part: the entity, and the buttons when they are served. The state is the
    // version running, as current, until the first check says otherwise: an entity without a
    // latest version reads unavailable, and the machine row with it.
    async Task UpdateStartupAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!updateCutoffTaken)
        {
            updateStartedAt = now;
            updateCutoffTaken = true;
        }
        updateSignature = null;
        nextUpdateSweep = now;
        lock (gate)
        {
            updateLatest ??= Installed;
            updateUrl ??= updates?.ReleasesUrl;
        }
        var (updateTopic, updateConfig) = HaContract.UpdateConfig(Slug, options.MachineName);
        await PublishAsync(updateTopic, updateConfig, true, ct);
        if (updateServing)
        {
            foreach (var (topic, config) in HaContract.UpdateButtonConfigs(Slug, options.MachineName)) { await PublishAsync(topic, config, true, ct); }
            AddPin($"agent_bridge_{Slug}_install_update", UpdateEntity("install_update"), now);
            AddPin($"agent_bridge_{Slug}_check_updates", UpdateEntity("check_updates"), now);
        }
        await PublishUpdateAsync(now, ct);
        AddPin($"agent_bridge_{Slug}_update", HaContract.MachineEntityId("update", "update", Slug), now);
    }

    async Task UpdatesAsync(DateTimeOffset now, CancellationToken ct)
    {
        // The outcome answers an earlier press, not a check: told whatever the buttons are now, and
        // on its own retry, so a notification Home Assistant keeps refusing holds up no new press.
        if (updateInstalled is not null && now >= nextOutcomeTry)
        {
            try
            {
                await UpdateOutcomeAsync(now, ct);
                Resolved("update-outcome");
            }
            // A request that timed out among them (HttpClient's timeout is a cancellation).
            catch (Exception e) when (e is not AgentHaBridgeRunning && !ct.IsCancellationRequested)
            {
                nextOutcomeTry = now + options.ButtonsRetry;
                Problem("update-outcome", $"Home Assistant connector could not tell how the update ended: {Describe(e)}; trying again");
            }
        }
        (string Title, string Message)? retry;
        lock (gate) { retry = now >= nextNoticeTry ? noticePending : null; }
        if (retry is { } n) { await TellAsync(n.Title, n.Message, ct); }
        if (!updateServing)
        {
            await PublishUpdateAsync(now, ct);
            return;
        }
        if (updateCheck is { IsCompleted: true } done)
        {
            updateCheck = null;
            CheckDone(done, now);
        }
        if (updateCheckQueued && updateCheck is null)
        {
            updateCheckQueued = false;
            if (!UpdateBusy) { StartCheck(now, pressed: true); }
        }
        var touched = Interlocked.Exchange(ref updateTouched, 0) == 1;
        if (options.ConsumeAnswers && (touched || now >= nextUpdateSweep))
        {
            nextUpdateSweep = now + options.AnswerSweep;
            try { await UpdatePressesAsync(now, ct); }
            catch { if (touched) { Interlocked.Exchange(ref updateTouched, 1); } throw; }
        }
        if (updateCheck is null && !UpdateBusy && now >= nextUpdateCheck) { StartCheck(now, pressed: false); }
        await PublishUpdateAsync(now, ct);
    }

    async Task UpdatePressesAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (await PressedAsync("check_updates", checkPressSeen, ct) is { } check)
        {
            checkPressSeen = check.Value;
            // Not while an update runs: it has looked for itself, and says what it found.
            if (check.Fresh && !UpdateBusy)
            {
                log("Home Assistant connector: update check requested from Home Assistant");
                StartCheck(now, pressed: true);
            }
        }
        if (await PressedAsync("install_update", installPressSeen, ct) is { } install)
        {
            installPressSeen = install.Value;
            if (install.Fresh) { await InstallPressedAsync(now, ct); }
        }
    }

    // A press not seen before, and whether it was made since this connector started.
    async Task<(string Value, bool Fresh)?> PressedAsync(string key, string? seen, CancellationToken ct)
    {
        var press = await ObserveAsync(UpdateEntity(key), ct);
        if (!press.Present || press.Value == seen) { return null; }
        var fresh = DateTimeOffset.TryParse(press.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at > updateStartedAt;
        return (press.Value, fresh);
    }

    // Get-BridgeUpdateStatus, forced or on the interval. Run beside the passes: a release feed
    // that is slow to answer must hold up no session's card.
    void StartCheck(DateTimeOffset now, bool pressed)
    {
        nextUpdateCheck = now + options.UpdateCheck;
        if (updateCheck is not null)
        {
            if (pressed) { updateCheckQueued = true; }
            return;
        }
        updateCheckPressed = pressed;
        updateCheck = Begin(() => updates!.CheckAsync(updateCancel.Token).WaitAsync(options.UpdateCheckTimeout, time));
    }

    static Task<T> Begin<T>(Func<Task<T>> start)
    {
        try { return start(); }
        catch (Exception e) { return Task.FromException<T>(e); }
    }

    void CheckDone(Task<UpdateRelease?> done, DateTimeOffset now)
    {
        var pressed = updateCheckPressed;
        updateCheckPressed = false;
        // An install looks for itself, and what it found is what the entity shows until it ends.
        // Cancelled only matters when the connector is stopping: otherwise it is a request that
        // timed out (HttpClient's timeout is a cancellation), and as much a failure as any other.
        if ((done.IsCanceled && updateCancel.IsCancellationRequested) || UpdateBusy) { return; }
        if (done.IsCanceled || done.Exception is not null)
        {
            var why = done.Exception is { } failed ? failed.InnerException?.Message ?? failed.Message : "the request timed out";
            Problem("update-check", $"Home Assistant connector update check failed: {why}");
            nextUpdateCheck = now + options.UpdateRetry;
            // Not "failed": a check that could not be made is not a failed update (#92). The
            // explanation goes in the notes, and the entity, with no latest version known, reads
            // as not established.
            lock (gate)
            {
                // "Already current" is no longer known.
                if (updateVerdict is { Stage: "current" }) { updateVerdict = null; }
                updateLatest = null;
                updateNotes = $"The latest release could not be established: {why}";
                updateUrl = updates!.ReleasesUrl;
            }
            return;
        }
        Resolved("update-check");
        var release = done.Result;
        var announce = false;
        lock (gate)
        {
            if (release is null)
            {
                updateLatest = Installed;
                updateNotes = "";
                updateUrl = updates!.ReleasesUrl;
                // A press has to answer for itself: "already current" changes nothing else on the
                // entity, and an identical publish is not a change Home Assistant shows.
                if (pressed && !UpdateBusy)
                {
                    updateVerdict = ("current", $"Checked just now; {Installed} is the latest release.", now + options.UpdateVerdict);
                }
            }
            else
            {
                // "Already current" is no longer true.
                if (updateVerdict is { Stage: "current" }) { updateVerdict = null; }
                announce = updateLatest != release.Version;
                updateLatest = release.Version;
                updateNotes = release.Notes ?? "";
                updateUrl = release.Url ?? updates!.ReleasesUrl;
            }
        }
        if (announce) { log($"Home Assistant connector: update available: {Installed} -> {release!.Version}"); }
    }

    // A press of Install: refused while an update runs (#196), otherwise the spinner at once and
    // the update beside the passes.
    async Task InstallPressedAsync(DateTimeOffset now, CancellationToken ct)
    {
        log("Home Assistant connector: install update requested from Home Assistant");
        if (UpdateBusy)
        {
            string detail;
            lock (gate)
            {
                detail = updateProgress is { } p
                    ? $"an update to {updateLatest ?? "the latest release"} is already {Doing(p.Stage)} here"
                    : "an update is already under way here";
            }
            log($"Home Assistant connector: install press not acted on: {detail}");
            await TellAsync($"Bridge update already running on {options.MachineName}",
                $"This press did not start an update: {detail}. That update reports here when it finishes.", ct);
            return;
        }
        lock (gate)
        {
            updateVerdict = null;
            updateProgress = ("checking", null);
            installing = Task.Run(() => InstallAsync(updateCancel.Token), CancellationToken.None);
        }
        await PublishUpdateAsync(now, ct);
    }

    // An install running, or one handed to the updater whose host has not exited yet.
    bool UpdateBusy
    {
        get { lock (gate) { return installing is { IsCompleted: false } || updateAttemptHere; } }
    }

    static string Doing(string stage) => stage switch
    {
        "checking" => "being checked",
        "downloading" => "downloading",
        "restarting" => "restarting into it",
        _ => stage,
    };

    // Invoke-BridgeSelfUpdate, in the host: the release looked for again (Install acts on what is
    // newest now, not on what the last check found), downloaded, and handed to the updater, which
    // replaces the host once it has exited and starts it again.
    async Task InstallAsync(CancellationToken ct)
    {
        string? target = null;
        try
        {
            var release = await updates!.CheckAsync(ct).WaitAsync(options.UpdateCheckTimeout, time, ct);
            if (release is null)
            {
                var detail = $"{Installed} is already installed, and it is the latest release";
                lock (gate)
                {
                    updateLatest = Installed;
                    updateNotes = "";
                    updateProgress = null;
                    updateVerdict = ("current", detail, time.GetUtcNow() + options.UpdateVerdict);
                }
                log($"Home Assistant connector: self-update: {detail}");
                await SayAsync(() => PublishUpdateAsync(time.GetUtcNow(), ct), "say there is no newer release", ct);
                await TellAsync($"No newer bridge release on {options.MachineName}", $"The bridge update did not start: {detail}", ct);
                return;
            }
            target = release.Version;
            lock (gate)
            {
                updateLatest = release.Version;
                updateNotes = release.Notes ?? "";
                updateUrl = release.Url ?? updates.ReleasesUrl;
                updateProgress = ("downloading", 0);
            }
            log($"Home Assistant connector: self-update: downloading {release.Version}");
            await updates.DownloadAsync(release, percent =>
            {
                lock (gate) { if (updateProgress is { Stage: "downloading" }) { updateProgress = ("downloading", Math.Clamp(percent, 0, 100) / 100.0); } }
            }, ct);
            ct.ThrowIfCancellationRequested();
            lock (gate)
            {
                updateProgress = ("restarting", null);
                updateAttempt = new UpdateAttempt(release.Version, Installed, time.GetUtcNow(), updateUrl);
                updateAttemptHere = true;
            }
            // Recorded, or not handed over: the host that comes back has nothing else to go on.
            if (!SaveLedger())
            {
                throw new IOException($"the update could not be recorded in {options.StateFile}, so how it ended could not be told after the restart");
            }
            await SayAsync(() => PublishUpdateAsync(time.GetUtcNow(), ct), "show the restart", ct);
            log($"Home Assistant connector: self-update: restarting into {release.Version}");
            updates.ApplyAndRestart(release);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            var why = e.Message;
            lock (gate)
            {
                updateProgress = null;
                updateAttempt = null;
                updateAttemptHere = false;
                updateVerdict = ("failed", why, time.GetUtcNow() + options.UpdateVerdict);
            }
            SaveLedger();
            log($"Home Assistant connector: self-update{(target is null ? "" : $" to {target}")} did not start: {why}");
            await SayAsync(() => PublishUpdateAsync(time.GetUtcNow(), ct), "say the update did not start", ct);
            await TellAsync($"Bridge update failed on {options.MachineName}", $"The bridge update did not start: {why}", ct);
        }
    }

    // What the update beside the passes says about itself: best effort, as the update must not
    // depend on Home Assistant (or on agent-ha-bridge not having started meanwhile, which refuses
    // the write).
    // A request that timed out is one of those failures (HttpClient's timeout is a cancellation);
    // only the update itself stopping is not.
    async Task SayAsync(Func<Task> say, string what, CancellationToken ct)
    {
        try { await say(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { log($"Home Assistant connector could not {what}: {Describe(e)}"); }
    }

    // Invoke-DaemonUpdateOutcome: the host that comes back after an update says how it ended,
    // once, on the entity and in a notification. Never the host that recorded the attempt: that one
    // is still on the old version for the moment before it exits.
    async Task UpdateOutcomeAsync(DateTimeOffset now, CancellationToken ct)
    {
        UpdateAttempt attempt;
        lock (gate)
        {
            if (updateOutcome is not { } a) { return; }
            attempt = a;
        }
        if (now - attempt.At > options.UpdateOutcomeAge)
        {
            log($"Home Assistant connector: the update to {attempt.Target} recorded {attempt.At:o} is too old to announce");
        }
        else
        {
            var installed = updateInstalled!;
            var success = string.Equals(installed, attempt.Target, StringComparison.OrdinalIgnoreCase);
            var url = attempt.Url;
            string title, message, stage, detail;
            if (success)
            {
                stage = "completed";
                detail = $"Updated to {attempt.Target}";
                title = $"Bridge installer completed on {options.MachineName}";
                message = $"The bridge updated to **{attempt.Target}**; currently recorded version: **{installed}**. The host restarted on it.";
                if (!string.IsNullOrEmpty(url)) { message += $" [Release notes]({url})"; }
            }
            else
            {
                stage = "failed";
                detail = $"The host restarted on {installed}, not {attempt.Target}";
                title = $"Bridge update failed on {options.MachineName}";
                message = $"The bridge update to {attempt.Target} did not complete: the host restarted on {installed}. Currently recorded version: {installed}. The installation may be partially changed; no rollback is claimed.";
            }
            // The verdict alone: what the checks found since stays as they found it.
            lock (gate) { updateVerdict = (stage, detail, now + options.UpdateOutcomeShown); }
            await PublishUpdateAsync(now, ct);
            await NoticeAsync(title, message, ct);
            log($"Home Assistant connector: self-update {(success ? $"completed for {attempt.Target}" : $"to {attempt.Target} FAILED: the host restarted on {installed}")}");
        }
        lock (gate) { if (ReferenceEquals(updateOutcome, attempt)) { updateOutcome = null; } }
        SaveLedger();
    }

    // A notice answering a press: sent now, or kept and tried again with the passes until Home
    // Assistant takes it, so a failure it reports is not lost to a moment's refusal. Never throws but
    // for the connector stopping.
    async Task TellAsync(string title, string message, CancellationToken ct)
    {
        lock (gate) { noticePending = (title, message); }
        try
        {
            await NoticeAsync(title, message, ct);
            lock (gate) { if (noticePending == (title, message)) { noticePending = null; } }
            Resolved("update-notice");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            lock (gate) { nextNoticeTry = time.GetUtcNow() + options.ButtonsRetry; }
            Problem("update-notice", $"Home Assistant connector could not send \"{title}\": {Describe(e)}; trying again");
        }
    }

    // The update notices all replace one another (notification_id agent_bridge_update_<slug>).
    Task NoticeAsync(string title, string message, CancellationToken ct) =>
        ha.CallServiceAsync("persistent_notification", "create", new JsonObject
        {
            ["title"] = title,
            ["message"] = message,
            ["notification_id"] = $"agent_bridge_update_{Slug}",
        }, ct);

    // The entity as things stand, published when that differs from what was published last. Shared
    // by the passes and the update beside them, so neither publishes over the other with older news.
    async Task PublishUpdateAsync(DateTimeOffset now, CancellationToken ct)
    {
        await updatePublish.WaitAsync(ct);
        try
        {
            JsonObject state, attributes;
            lock (gate)
            {
                if (updateVerdict is { } v && now >= v.Until) { updateVerdict = null; }
                var running = updateServing && updateProgress is not null && (installing is { IsCompleted: false } || updateAttemptHere);
                // Without the buttons the entity still says how an update ended (its outcome).
                var (stage, proportion, detail) = running ? (updateProgress!.Value.Stage, updateProgress.Value.Proportion, "")
                    : updateVerdict is { } verdict ? (verdict.Stage, (double?)null, verdict.Detail) : ("", (double?)null, "");
                state = updateServing
                    ? HaPayloads.UpdateState(Installed, updateLatest, updateUrl, updateNotes, running, proportion)
                    : HaPayloads.UpdateState(options.Version);
                attributes = HaPayloads.UpdateAttributes(stage, detail);
            }
            var signature = HaContract.Serialize(state) + HaContract.Serialize(attributes);
            if (signature == updateSignature) { return; }
            await PublishAsync(HaContract.UpdateStateTopic(Slug), state, true, ct);
            await PublishAsync(HaContract.UpdateAttributesTopic(Slug), attributes, true, ct);
            updateSignature = signature;
        }
        finally { updatePublish.Release(); }
    }

    void LoadUpdateAttempt(JsonElement root)
    {
        if (updateAttemptHere) { return; }
        updateOutcome = null;
        if (!root.TryGetProperty("update", out var u) || u.ValueKind != JsonValueKind.Object) { return; }
        string? Text(string name) => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Text("target") is { Length: > 0 } target && Text("before") is { } before
            && DateTimeOffset.TryParse(Text("at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            updateOutcome = new UpdateAttempt(target, before, at, Text("url"));
        }
    }

    JsonNode? UpdateAttemptLedger() => (updateAttempt ?? updateOutcome) is { } a
        ? new JsonObject
        {
            ["target"] = a.Target,
            ["before"] = a.Before,
            ["at"] = a.At.ToString("o", CultureInfo.InvariantCulture),
            ["url"] = a.Url,
        }
        : null;

    async Task StopUpdatesAsync()
    {
        await updateCancel.CancelAsync();
        try { if (installing is { } i) { await i; } } catch (Exception e) when (e is OperationCanceledException or AgentHaBridgeRunning) { }
        try { if (updateCheck is { } c) { await c; } } catch (Exception) { }
    }
}
