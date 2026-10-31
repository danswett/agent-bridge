using System.Text.RegularExpressions;

namespace AgentBridge.HomeAssistant;

// Stage 7, the daemon's upkeep (docs/ha-connector.md): the machine's Detailed activity switch
// created when Home Assistant has none, the liveness sensor rebuilt when Home Assistant has
// stopped taking its beats, and a live session's entities put back when Home Assistant has lost
// them or forgotten their values.
//
// None of it removes anything but what this connector just made, or published itself, for this
// machine: forgetting a machine is the dashboard's X, pressed by a person in a browser, never
// something this connector does (the forget section of the doc).
public sealed partial class HaConnector
{
    HaLiveness? liveness;
    // The next start publishes the machine again for a host that never stopped (its online sensor
    // went, most likely to the dashboard's X): its sessions keep what they know.
    bool republishing;
    // Restore-DaemonSessionEntities' attempts, by session: said the first time and every tenth.
    readonly Dictionary<string, int> restores = new(StringComparer.Ordinal);
    // When each session's entities were first read as gone, this time.
    readonly Dictionary<string, DateTimeOffset> absentSince = new(StringComparer.Ordinal);

    void ResetUpkeep()
    {
        liveness = null;
        republishing = false;
        lock (gate)
        {
            restores.Clear();
            absentSince.Clear();
        }
    }

    // Initialize-CopilotVerboseToggle: the switch the dashboard draws as Detail on this machine's row.
    // Nothing else creates it, so a machine without one would show "Entity not found". Created only
    // when Home Assistant's storage has none, and never deleted and created again: Home Assistant
    // keeps its value across a restart, and a delete and create would put it back to off, the
    // person's choice silently gone. One that is stored but has no state yet is Home Assistant
    // still starting, and is left alone.
    //
    // Not ported: the daemon's one-time move off the pre-1.2x shared switch
    // (input_boolean.agent_bridge_detailed_activity), which deletes it. Every machine of the fleet
    // shared that one; a machine coming to this host comes from agent-ha-bridge 1.33, which made
    // that move long since, and this connector deletes no helper it did not just create.
    async Task DetailedHelperAsync(CancellationToken ct)
    {
        var id = HaContract.DetailedHelperId(Slug);
        var entityId = "input_boolean." + id;
        IReadOnlyList<string> stored;
        try { stored = await ha.ListInputBooleansAsync(ct); }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
        {
            log($"Home Assistant connector could not read Home Assistant's helpers, so this machine's Detailed activity switch was not checked: {Describe(e)}");
            return;
        }
        if (!stored.Contains(id, StringComparer.Ordinal))
        {
            string created;
            try { created = await ha.CreateInputBooleanAsync(HaContract.DetailedHelperName(Slug), HaContract.DetailedHelperIcon, ct); }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
            {
                log($"Home Assistant connector could not create this machine's Detailed activity switch {entityId}: {Describe(e)}");
                return;
            }
            if (created != id)
            {
                // Almost always a list that came back empty while Home Assistant was starting: the
                // switch was there, and Home Assistant numbered the new one beside it. That copy is
                // removed and the original, with its value, kept. Only a numbered copy of this
                // switch, the one just made: anything else is left as it is and said.
                if (Regex.IsMatch(created, "^" + Regex.Escape(id) + "_[0-9]+$", RegexOptions.CultureInvariant))
                {
                    log($"Home Assistant connector: Home Assistant made the Detailed activity switch as {created}, beside {id}; removing the copy and keeping the original");
                    try { await ha.DeleteInputBooleanAsync(created, ct); }
                    catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
                    {
                        log($"Home Assistant connector could not remove input_boolean.{created}: {Describe(e)}");
                    }
                }
                else
                {
                    log($"Home Assistant connector: Home Assistant made the Detailed activity switch as {created}, not {id}; left as it is");
                }
                return;
            }
            log($"Home Assistant connector created this machine's Detailed activity switch {entityId}");
            if (!await HelperHasStateAsync(entityId, ct)) { return; }
        }
        await NameHelperAsync(entityId, ct);
    }

    // Test-CopilotHelperHasState: a helper can be in storage and the registry yet have no state.
    async Task<bool> HelperHasStateAsync(string entityId, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= options.HelperStateAttempts; attempt++)
        {
            try
            {
                if (await ha.GetStateAsync(entityId, ct) is { State.Length: > 0 }) { return true; }
            }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { }
            if (attempt < options.HelperStateAttempts) { await Task.Delay(options.HelperStateDelay, time, ct); }
        }
        return false;
    }

    // The name the dashboard and Home Assistant show for it, given through the registry so its id,
    // which Home Assistant made from its name, stays as it is. Cosmetic, so never worth failing over.
    async Task NameHelperAsync(string entityId, CancellationToken ct)
    {
        var display = HaContract.DetailedHelperDisplayName(options.MachineName);
        try
        {
            var entry = (await ha.ListEntityRegistryAsync(ct)).FirstOrDefault(e => e.EntityId == entityId);
            if (entry is not null && entry.Name != display) { await ha.SetEntityNameAsync(entityId, display, ct); }
        }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { }
    }

    // Confirm-DaemonOnlineHeartbeat, after every beat: the sensor is read straight from Home
    // Assistant, and HaLiveness decides. A sensor that is there and silent is rebuilt as the daemon
    // rebuilds it. One that is not there at all has lost its config, most likely to the
    // dashboard's X pressed while this machine's liveness had lapsed, which removed every entity of
    // the machine with it: so the whole machine is published again, as a start does, rather than
    // the sensor alone. A machine that is running was not gone. The daemon rebuilds only the sensor
    // and brings the rest back piecemeal; this is a deliberate difference (the doc's table).
    async Task ConfirmBeatAsync(DateTimeOffset now, CancellationToken ct)
    {
        var entityId = HaContract.MachineEntityId("binary_sensor", "online", Slug);
        HaLiveness.Read read;
        try
        {
            var state = await ha.GetStateAsync(entityId, ct);
            read = state is null ? HaLiveness.Read.Gone : HaLiveness.Read.Present(state.LastReported);
        }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
        {
            read = HaLiveness.Read.Unreadable;
        }
        liveness ??= new HaLiveness(options.OnlineStall, options.OnlineStallBeats, options.OnlineRepairLimit);
        switch (liveness.Observe(now, read))
        {
            case HaLiveness.Verdict.GiveUp:
                // The cause is deliberately not asserted: telling people Home Assistant had stopped
                // listening sent them to restart a working integration (agent-ha-bridge #202).
                log($"Home Assistant connector: {entityId} has ignored {options.OnlineRepairLimit} rebuilds; leaving it alone rather than flapping it. " +
                    $"Nothing is reaching it from {HaContract.OnlineStateTopic(Slug)} - check that the beat still carries a changing payload, then the broker and Home Assistant's MQTT integration.");
                return;
            case HaLiveness.Verdict.Rebuild:
                break;
            default:
                return;
        }
        var stalled = (int)liveness.Stalled.TotalSeconds;
        if (read.Absent)
        {
            log($"Home Assistant connector: {entityId} has been gone from Home Assistant for {stalled}s while this machine runs (forgotten, or its retained config lost); publishing the machine again");
            republishing = true;
            started = false;
            retryAt = now;
            return;
        }
        log($"Home Assistant connector: {entityId} has taken no beat for {stalled}s; rebuilding it");
        // Reset-CopilotMqttDiscoveryConfig: Home Assistant ignores a config identical to the one it
        // holds, so the same config again changes nothing; retracting it first builds the entity anew.
        var (topic, config) = HaContract.OnlineConfig(Slug, options.MachineName);
        await ha.PublishAsync(topic, "", true, ct);
        await Task.Delay(options.DiscoveryReset, time, ct);
        await PublishAsync(topic, config, true, ct);
        // The beat only once the sensor is back: unretained, one that outruns discovery is dropped and
        // the rebuilt sensor reads offline for another whole interval.
        if (await RegisteredAsync($"agent_bridge_{Slug}_online", entityId, ct))
        {
            foreach (var beat in HaContract.Heartbeat(Slug, time.GetUtcNow())) { await ha.PublishAsync(beat.Topic, beat.Payload, beat.Retain, ct); }
        }
        else
        {
            log($"Home Assistant connector: {entityId} did not come back after the rebuild; trying again next beat");
        }
    }

    // Wait-DaemonEntityRegistered, with the pin (Set-CopilotMqttOnlineEntityId) taken on each try:
    // a rebuilt entity registers afresh, under the name Home Assistant makes of its device. Bounded,
    // because waiting costs the pass more than a missed beat costs the dashboard.
    async Task<bool> RegisteredAsync(string uniqueId, string entityId, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= options.RegisteredAttempts; attempt++)
        {
            try
            {
                var current = (await ha.ListEntityRegistryAsync(ct)).FirstOrDefault(e => e.UniqueId == uniqueId)?.EntityId;
                if (current is not null && current != entityId) { await ha.RenameEntityAsync(current, entityId, ct); }
            }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { }
            try
            {
                if (await ha.GetStateAsync(entityId, ct) is not null) { return true; }
            }
            // Not "not there yet" but a Home Assistant that cannot answer: asking on will not help.
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { return false; }
            if (attempt < options.RegisteredAttempts) { await Task.Delay(options.RegisteredDelay, time, ct); }
        }
        return false;
    }

    // Repair-CopilotSessionEntities, every reconcile, for each session this connector has published
    // for at least EntityRestore (sooner, its entities may not have registered yet):
    //   * one whose card is gone from Home Assistant while it runs here is published again whole
    //     (Restore-DaemonSessionEntities): its entities were removed and nothing else would ever
    //     bring them back. Only on a determinate "not there", and one that has lasted EntityRestore:
    //     a Home Assistant starting up answers 404 for MQTT entities it has not loaded yet, and a
    //     republish parks the selector on Idle, putting a question back on screen for nothing. A
    //     read that failed decides nothing at all;
    //   * one whose reply box or selector Home Assistant holds no value for (they have no state
    //     topic, so a Home Assistant restart brings them back unknown) has the box blanked and its
    //     question armed again, or the selector parked on Idle when there is none.
    // Each is read through its reply box, or its status sensor when this host serves no answers.
    async Task SessionUpkeepAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<(string Id, Published P)> due;
        lock (gate) { due = [.. published.Where(p => !p.Value.Lost && now - p.Value.PublishedAt >= options.EntityRestore).Select(p => (p.Key, p.Value))]; }
        foreach (var (id, p) in due)
        {
            var sentinel = ServesAnswers ? SessionEntities.Reply(p.Node) : SessionEntities.Status(p.Node);
            HaState? state;
            try { state = await ha.GetStateAsync(sentinel, ct); }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { continue; }
            var shortId = id[..Math.Min(8, id.Length)];
            if (state is null)
            {
                Lose(id, p, now);
                continue;
            }
            if (ServesAnswers) { await RePrimeAsync(id, p, state, now, ct); }
            // Seen back: a session torn down again starts its count afresh.
            else
            {
                lock (gate)
                {
                    restores.Remove(id);
                    absentSince.Remove(id);
                }
            }
        }
    }

    // Restore-DaemonSessionEntities' trigger: published again whole on the next pass, once they have
    // been gone for EntityRestore.
    void Lose(string id, Published p, DateTimeOffset now)
    {
        int attempt;
        lock (gate)
        {
            if (!absentSince.TryGetValue(id, out var since))
            {
                absentSince[id] = now;
                return;
            }
            if (now - since < options.EntityRestore) { return; }
            absentSince.Remove(id);
            attempt = restores.TryGetValue(id, out var was) ? was + 1 : 1;
            restores[id] = attempt;
            p.Lost = true;
            dirty.Add(id);
        }
        var shortId = id[..Math.Min(8, id.Length)];
        if (attempt == 1) { log($"Home Assistant connector: the entities of {shortId} ({p.Name}) are gone from Home Assistant while it runs here; publishing them again"); }
        else if (attempt % 10 == 0) { log($"Home Assistant connector: still publishing the entities of {shortId} ({p.Name}) again after {attempt} attempts"); }
    }

    async Task RePrimeAsync(string id, Published p, HaState reply, DateTimeOffset now, CancellationToken ct)
    {
        var lost = Unset(reply.State);
        if (!lost)
        {
            HaState? decision;
            try { decision = await ha.GetStateAsync(SessionEntities.Decision(p.Node), ct); }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { return; }
            // The selector gone on its own, the reply box still there: the set goes again, as when
            // the box is gone, since a selector is published and pinned with the rest.
            if (decision is null)
            {
                Lose(id, p, now);
                return;
            }
            lost = Unset(decision.State);
        }
        lock (gate)
        {
            restores.Remove(id);
            absentSince.Remove(id);
        }
        if (!lost) { return; }
        var shortId = id[..Math.Min(8, id.Length)];
        await ResetReplyBoxAsync(p.Node, ct);
        if (p.Armed is { } armed)
        {
            // On screen again on the next pass, with fresh baselines: the question is still waiting
            // on an answer nobody could give on a selector that has forgotten it. What it has been
            // through is kept, so one already answered is not answered twice.
            lock (gate)
            {
                armed.Rearm = true;
                // Read first where it can be read: Home Assistant may have kept the selector and
                // the choices, and an answer given on them since it came back is still an answer.
                armed.ReadFirst = ServesAnswers && options.ConsumeAnswers;
                dirty.Add(id);
            }
            log($"Home Assistant connector: {p.Node}'s question goes on screen again, which Home Assistant had lost (restarted?)");
            return;
        }
        var slots = p.ArmedSlots >= 0 ? p.ArmedSlots : await ArmedSlotsAsync(p.Node, ct);
        await RunAsync(HaDecision.Clear(p.Node, p.Name, options.MachineName, slots), p.Node, ct);
        p.Cleared = true;
        p.ArmedSlots = 0;
        log($"Home Assistant connector: put {shortId}'s reply box and selector back, which Home Assistant held no value for (restarted?)");
    }

    static bool Unset(string? state) => state is "unknown" or "unavailable";
}
