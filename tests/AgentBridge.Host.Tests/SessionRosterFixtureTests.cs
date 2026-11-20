using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class SessionRosterFixtureTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Multiple_discovery_paths_publish_each_session_id_only_once()
    {
        using var fixture = new Fixture();
        fixture.ClaudeRegistration(Id, 700, null);
        fixture.CodexRegistration(Id, 950, null);
        fixture.Processes.Run(AgentKind.Claude, 700, "claude.exe");
        fixture.Processes.Run(AgentKind.Codex, 950, "codex.exe");
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        Assert.Equal(Id, Assert.Single(source.Snapshot()).Id);
        using var subscription = source.Subscribe();
        Assert.Equal(1, subscription.Result.Count);
    }

    [Fact]
    public async Task Case_distinct_registration_ids_remain_distinct_across_roots()
    {
        using var f = new Fixture();
        const string lower = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        var upper = lower.ToUpperInvariant();
        f.CodexRegistration(lower, 950, null);
        var other = Directory.CreateDirectory(Path.Combine(f.Root, "other-registrations")).FullName;
        File.WriteAllText(Path.Combine(other, upper + ".json"), JsonSerializer.Serialize(new
        {
            SessionId = upper, ProcessId = 951, Updated = Fixture.T0,
        }));
        f.Processes.Run(AgentKind.Codex, 950, "codex.exe");
        f.Processes.Run(AgentKind.Codex, 951, "codex.exe");
        await using var source = new SessionSource(f.Options(codexRoots: [f.CodexRegistrations, other]),
            f.Processes, f.Time, hostId: "host-id");
        await source.RefreshAsync();
        Assert.Equal([upper, lower], source.Snapshot().Select(s => s.Id).Order(StringComparer.Ordinal));
        Assert.Null(source.Held);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_roster_reads_and_ordered_changes_keep_unknown_outcomes_and_drop_ended_sessions(bool patch)
    {
        using var fixture = new Fixture();
        var events = fixture.CopilotSession(Id, 100);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        fixture.Processes.Run(AgentKind.Copilot, 100, startedAt: Fixture.T0.AddMinutes(-5));
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        var service = new SessionService(source, new MachineInfo("host-id", "Fixture host", "windows", "0.1.0"));
        var channel = new CaptureChannel();
        var empty = JsonSerializer.SerializeToElement(new { });
        async Task<JsonElement> Read(string op, object? data = null) =>
            JsonSerializer.SerializeToElement(await service.HandleAsync(channel, op,
                data is null ? empty : JsonSerializer.SerializeToElement(data)), ProtocolJson.Options);
        var machine = (await Read(Ops.Hello)).GetProperty("machine").GetProperty("id").GetString()!;
        AssertRow(Assert.Single((await Read(Ops.SessionsList)).GetProperty("sessions").EnumerateArray()), machine);
        AssertRow(await Read(Ops.SessionGet, new { id = Id }), machine);
        var subscription = Assert.IsType<ChannelReply>(await service.HandleAsync(channel, Ops.SessionsSubscribe,
            JsonSerializer.SerializeToElement(new { patch })));
        var feed = subscription.Then(channel);
        try
        {
            var snapshot = await channel.NextAsync();
            var synced = await channel.NextAsync();
            Assert.Equal((Events.SessionUpsert, Events.SessionsSynced), (snapshot.Name, synced.Name));
            Assert.Equal(snapshot.Seq + 1, synced.Seq);
            AssertRow(snapshot.Data, machine);
            Assert.Equal("working", snapshot.Data.GetProperty("status").GetString());

            Fixture.Append(events, Copilot.Assistant("turn done", 3), Copilot.TurnEnd(4));
            await source.RefreshAsync();
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            var live = await channel.NextAsync();
            Assert.Equal(patch ? Events.SessionPatch : Events.SessionUpsert, live.Name);
            Assert.Equal(synced.Seq + 1, live.Seq);
            var row = patch ? JsonSerializer.SerializeToElement(SessionPatches.Apply(
                JsonNode.Parse(snapshot.Data.GetRawText())!.AsObject(), snapshot.Seq,
                live.Data.Deserialize<SessionPatch>(ProtocolJson.Options)!)) : live.Data;
            AssertRow(row, machine);
            Assert.Equal("idle", row.GetProperty("status").GetString());

            await source.SetEndingAsync(Id, true);
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            var ending = await channel.NextAsync();
            Assert.Equal(live.Seq + 1, ending.Seq);
            Fixture.Append(events, Copilot.Assistant("pending change", 5));
            await source.RefreshAsync();
            fixture.Processes.Exit(AgentKind.Copilot, 100);
            await source.RefreshAsync();
            var removed = await channel.NextAsync();
            Assert.Equal((Events.SessionRemove, Id, ending.Seq + 1),
                (removed.Name, removed.Data.GetProperty("id").GetString(), removed.Seq));
            Assert.Empty(source.Snapshot());
            Assert.Empty((await Read(Ops.SessionsList)).GetProperty("sessions").EnumerateArray());
            Assert.True((await Read(Ops.SessionGet, new { id = Id })).GetProperty("missing").GetBoolean());
            var replacement = Assert.IsType<ChannelReply>(await service.HandleAsync(channel, Ops.SessionsSubscribe, empty));
            Assert.Equal(0, JsonSerializer.SerializeToElement(replacement.Result).GetProperty("Count").GetInt32());
            var resynced = replacement.Then(channel);
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(Events.SessionsSynced, (await channel.NextAsync()).Name);
            channel.Close();
            await resynced.WaitAsync(Patience);
        }
        finally
        {
            channel.Close();
            await feed.WaitAsync(Patience);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_crash_is_removed_only_after_discovery_knows_the_process_is_gone(bool unreadable)
    {
        using var fixture = new Fixture();
        Fixture.Append(fixture.CopilotSession(Id, 100), Copilot.User("go", 1), Copilot.TurnStart(2));
        fixture.Processes.Run(AgentKind.Copilot, 100);
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        fixture.Processes.Exit(AgentKind.Copilot, 100);
        fixture.Processes.Unreadable(AgentKind.Copilot, unreadable);
        await source.RefreshAsync();
        if (unreadable)
        {
            Assert.NotNull(source.Held);
            AssertRow(JsonSerializer.SerializeToElement(Assert.Single(source.List().Sessions), ProtocolJson.Options), "host-id");
            fixture.Processes.Unreadable(AgentKind.Copilot, false);
            await source.RefreshAsync();
        }
        Assert.Null(source.Held);
        Assert.Empty(source.List().Sessions);
        Assert.Null(source.Get(Id));
        using var subscription = source.Subscribe();
        Assert.Equal(0, subscription.Result.Count);
    }

    [Fact]
    public async Task A_host_restart_and_same_id_reregistration_do_not_reuse_ending_or_completion_evidence()
    {
        using var fixture = new Fixture();
        Fixture.Append(fixture.CopilotSession(Id, 100), Copilot.User("go", 1), Copilot.TurnStart(2));
        fixture.Processes.Run(AgentKind.Copilot, 100);
        var options = fixture.Options(stateFile: Path.Combine(fixture.Root, "host-state.json"));
        await using (var first = new SessionSource(options, fixture.Processes, fixture.Time, hostId: "host-id"))
        {
            await first.RefreshAsync();
            await first.SetEndingAsync(Id, true);
            Assert.Equal(SessionStatus.Ending, first.Get(Id)!.Status);
        }
        await using var restarted = new SessionSource(options, fixture.Processes, fixture.Time, hostId: "host-id");
        await restarted.RefreshAsync();
        Assert.Equal(SessionStatus.Working, restarted.Get(Id)!.Status);
        AssertRow(JsonSerializer.SerializeToElement(restarted.Get(Id), ProtocolJson.Options), "host-id");
        fixture.Processes.Exit(AgentKind.Copilot, 100);
        await restarted.RefreshAsync();
        Assert.Empty(restarted.Snapshot());
        fixture.CopilotSession(Id, 101);
        fixture.Processes.Run(AgentKind.Copilot, 101);
        await restarted.RefreshAsync();
        var returned = Assert.Single(restarted.List().Sessions);
        Assert.Equal((Id, 101, SessionStatus.Working), (returned.Id, returned.ProcessId, returned.Status));
        AssertRow(JsonSerializer.SerializeToElement(returned, ProtocolJson.Options), "host-id");
    }

    [Fact]
    public async Task Roster_evidence_survives_the_session_budget_in_list_get_and_subscription()
    {
        using var fixture = new Fixture();
        var text = new string('\u0001', 16_000);
        Fixture.Append(fixture.CopilotSession(Id, 100), Copilot.User(text, 1),
            Copilot.Assistant(text, 2, reasoning: text));
        fixture.Processes.Run(AgentKind.Copilot, 100);
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        Assert.True(ProtocolJson.ByteCount(source.GetUnbudgeted(Id)!) > SessionBudget.MaxSessionBytes);
        var service = new SessionService(source, new MachineInfo("host-id", "Fixture host", "windows", "0.1.0"));
        var channel = new CaptureChannel();
        var data = JsonSerializer.SerializeToElement(new { id = Id });
        var list = Assert.IsType<SessionList>(await service.HandleAsync(channel, Ops.SessionsList, data));
        var get = Assert.IsType<Session>(await service.HandleAsync(channel, Ops.SessionGet, data));
        using var subscription = source.Subscribe();
        Assert.True(subscription.Events.TryRead(out var upsert));
        var sent = Assert.IsType<Session>(upsert!.Data);
        foreach (var row in new[] { Assert.Single(list.Sessions), get, sent })
        {
            Assert.InRange(ProtocolJson.ByteCount(row), 1, SessionBudget.MaxSessionBytes);
            AssertRow(JsonSerializer.SerializeToElement(row, ProtocolJson.Options), "host-id");
        }
        Assert.InRange(ProtocolJson.ByteCount(list), 1, ProtocolJson.MaxMessageBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_bypasses_both_a_queued_upsert_and_a_held_quiet_change(bool patch)
    {
        using var fixture = new Fixture();
        var events = fixture.CopilotSession(Id, 100);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        fixture.Processes.Run(AgentKind.Copilot, 100);
        var options = fixture.Options();
        await using var source = new SessionSource(options, fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        var service = new SessionService(source, new MachineInfo("host-id", "Fixture host", "windows", "0.1.0"))
            { Time = fixture.Time };
        SessionService.Feed? captured = null;
        service.FeedCreated += created => captured = (SessionService.Feed)created;
        var channel = new CaptureChannel();
        var reply = Assert.IsType<ChannelReply>(await service.HandleAsync(channel, Ops.SessionsSubscribe,
            JsonSerializer.SerializeToElement(new { patch })));
        var running = reply.Then(channel);
        try
        {
            await channel.NextAsync();
            await channel.NextAsync();
            await service.HandleAsync(channel, Ops.SessionsFocus, JsonSerializer.SerializeToElement(new { visible = false }));
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            Fixture.Append(events, Copilot.Assistant("first", 3));
            await source.RefreshAsync();
            var first = await channel.NextAsync();
            fixture.Time.Advance(options.CoalesceInterval);
            Fixture.Append(events, Copilot.Assistant("quiet held", 4));
            await source.RefreshAsync();
            await HostWait.UntilAsync(() => captured!.Held == 1, "quiet roster change held", Patience);
            Fixture.Append(events, Copilot.Assistant("coalesced", 5));
            await source.RefreshAsync();
            Assert.Equal("coalesced", source.Get(Id)!.Activity.Response);
            var beforeRemoval = fixture.Time.GetUtcNow();
            fixture.Processes.Exit(AgentKind.Copilot, 100);
            await source.RefreshAsync();
            var removed = await channel.NextAsync();
            Assert.Equal((Events.SessionRemove, first.Seq + 1), (removed.Name, removed.Seq));
            Assert.Equal(Id, removed.Data.GetProperty("id").GetString());
            Assert.Equal(beforeRemoval, fixture.Time.GetUtcNow());
            Assert.Equal(0, captured!.Held);
            Assert.Empty(source.List().Sessions);
            Assert.Null(source.Get(Id));
            fixture.Time.Advance(service.QuietInterval + options.CoalesceInterval);
            using var fresh = source.Subscribe();
            Assert.Equal(0, fresh.Result.Count);
            Assert.True(fresh.Events.TryRead(out var synced));
            Assert.Equal(Events.SessionsSynced, synced!.Name);
            Assert.False(fresh.Events.TryRead(out _));
        }
        finally
        {
            channel.Close();
            await running.WaitAsync(Patience);
        }
    }

    [Theory]
    [InlineData("alive")]
    [InlineData("unidentified")]
    [InlineData("gone")]
    [InlineData("pidless")]
    [InlineData("reused")]
    public async Task Positive_ended_evidence_removes_a_session_even_while_another_process_holds_its_agent(string process)
    {
        using var fixture = new Fixture();
        fixture.CodexRegistration(Id, 950, null);
        fixture.Processes.Run(AgentKind.Codex, 950, "codex.exe");
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        Assert.Equal(SessionStatus.Working, source.Get(Id)!.Status);
        using var subscription = source.Subscribe();
        while (subscription.Events.TryRead(out _)) { }
        fixture.Processes.RunUndated(AgentKind.Codex, 960, "codex.exe");
        fixture.CodexRegistration(Id, process == "pidless" ? 0 : 950, null, status: "ended", ended: true);
        if (process == "unidentified") { fixture.Processes.Unidentify(AgentKind.Codex, 950); }
        if (process == "gone") { fixture.Processes.Exit(AgentKind.Codex, 950); }
        if (process == "reused") { fixture.Processes.Run(AgentKind.Codex, 950, "codex.exe", Fixture.T0.AddMinutes(5)); }
        await source.RefreshAsync();
        Assert.NotNull(source.Held);
        Assert.Null(source.Get(Id));
        Assert.Empty(source.List().Sessions);
        var changes = new List<SessionEvent>();
        while (subscription.Events.TryRead(out var change)) { changes.Add(change); }
        Assert.Equal(Id, Assert.IsType<SessionRemoved>(Assert.Single(changes, c => c.Name == Events.SessionRemove).Data).Id);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("empty")]
    [InlineData("corrupt")]
    [InlineData("missing")]
    public async Task Disk_resume_guards_never_supply_host_roster_membership_when_discovery_is_incomplete(string state)
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "sessions-listed.json");
        string? saved = state switch
        {
            "valid" => JsonSerializer.Serialize(new { sessions = new[] { new { id = Id, agent = "codex", pid = 950, processStartedAt = Fixture.T0.AddHours(-1) } } }),
            "empty" => "",
            "corrupt" => "{",
            _ => null,
        };
        if (saved is not null) { File.WriteAllText(path, saved); }
        fixture.CodexRegistration(Id, 950, null);
        File.WriteAllText(Path.Combine(fixture.CodexRegistrations, Id + ".json"), "{");
        fixture.Processes.Run(AgentKind.Codex, 950, "codex.exe");
        await using var source = new SessionSource(fixture.Options(stateFile: path), fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        Assert.NotNull(source.Held);
        Assert.Empty(source.Snapshot());
        Assert.Null(source.Get(Id));
        using var subscription = source.Subscribe();
        Assert.Equal(0, subscription.Result.Count);
    }

    [Fact]
    public async Task A_full_roster_holds_overflow_and_admits_it_when_an_ended_session_frees_a_slot()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(0, 251).Select(i => $"{i:D8}-0000-4000-8000-000000000000").ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            fixture.CodexRegistration(ids[i], 1000 + i, null);
            fixture.Processes.Run(AgentKind.Codex, 1000 + i, "codex.exe");
        }
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        Assert.Equal(250, source.Snapshot().Count);
        Assert.Contains("250-session roster limit", source.Held!.Reason);
        var omitted = Assert.Single(ids.Except(source.Snapshot().Select(s => s.Id)));
        var ended = source.Snapshot()[0].Id;
        fixture.CodexRegistration(ended, 1000 + Array.IndexOf(ids, ended), null, status: "ended", ended: true);
        await source.RefreshAsync();
        Assert.Equal(250, source.Snapshot().Count);
        Assert.Null(source.Get(ended));
        Assert.NotNull(source.Get(omitted));
        Assert.Null(source.Held);
        using var subscription = source.Subscribe();
        Assert.Equal(250, subscription.Result.Count);
    }

    [Fact]
    public async Task An_ended_Claude_registration_cannot_be_readmitted_by_its_own_session_file_during_a_hold()
    {
        using var fixture = new Fixture();
        fixture.ClaudeSessionFile(700, Id);
        fixture.ClaudeRegistration(Id, 700, null);
        fixture.Processes.Run(AgentKind.Claude, 700, "claude.exe");
        fixture.ClaudeSessionFile(701, "99999999-9999-4999-8999-999999999999");
        fixture.Processes.RunUndated(AgentKind.Claude, 701, "claude.exe");
        fixture.Processes.Unidentify(AgentKind.Claude, 701);
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time, hostId: "host-id");
        await source.RefreshAsync();
        Assert.Equal(Id, Assert.Single(source.List().Sessions).Id);
        Assert.Contains("could not be read (pid 701)", source.Held!.Reason);
        using var subscription = source.Subscribe();
        while (subscription.Events.TryRead(out _)) { }
        var path = Path.Combine(fixture.ClaudeRegistrations, Id + ".json");
        var registration = JsonNode.Parse(File.ReadAllText(path))!;
        registration["Ended"] = true;
        File.WriteAllText(path, registration.ToJsonString());

        await source.RefreshAsync();

        var changes = new List<SessionEvent>();
        while (subscription.Events.TryRead(out var change)) { changes.Add(change); }
        Assert.Equal([Events.SessionRemove], changes.Select(c => c.Name));
        Assert.Equal(Id, Assert.IsType<SessionRemoved>(changes[0].Data).Id);
        Assert.Null(source.Get(Id));
        Assert.Empty(source.List().Sessions);
        Assert.NotNull(source.Held);
        using var fresh = source.Subscribe();
        Assert.Equal(0, fresh.Result.Count);
        await source.RefreshAsync();
        Assert.False(subscription.Events.TryRead(out _));
        Assert.Null(source.Get(Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overflow_is_held_before_mid_pass_reads_and_old_holds_survive_until_admission_completes(bool previousHold)
    {
        using var fixture = new Fixture();
        var admissionPatience = TimeSpan.FromSeconds(30);
        var ids = Enumerable.Range(0, 251).Select(i => $"{i:D8}-0000-4000-8000-000000000000").ToArray();
        for (var i = 0; i < 250; i++)
        {
            fixture.CodexRegistration(ids[i], 1000 + i, null);
            fixture.Processes.Run(AgentKind.Codex, 1000 + i, "codex.exe");
        }
        if (previousHold)
        {
            fixture.ClaudeSessionFile(700, Id);
            fixture.Processes.RunUndated(AgentKind.Claude, 700, "claude.exe");
            fixture.Processes.Unidentify(AgentKind.Claude, 700);
        }
        TaskCompletionSource? entered = null;
        using var proceed = new ManualResetEventSlim(true);
        await using var source = new SessionSource(fixture.Options(), fixture.Processes, fixture.Time, answerAttempted: _ =>
        {
            if (entered is { } paused)
            {
                paused.TrySetResult();
                if (!proceed.Wait(admissionPatience)) { throw new TimeoutException("Paused roster pass was not released."); }
            }
            return false;
        }, hostId: "host-id");
        await source.RefreshAsync();
        Assert.Equal(250, source.Snapshot().Count);
        fixture.CodexRegistration(ids[250], 1250, null);
        fixture.Processes.Run(AgentKind.Codex, 1250, "codex.exe");
        fixture.Processes.Exit(AgentKind.Claude, 700);

        async Task DuringAdmission(Action inspect)
        {
            proceed.Reset();
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pass = Task.Factory.StartNew(() => source.RefreshAsync(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            try { await entered.Task.WaitAsync(admissionPatience); inspect(); }
            finally { entered = null; proceed.Set(); await pass.WaitAsync(admissionPatience); }
        }
        void AssertHeld()
        {
            Assert.Contains(AgentKind.Codex, Assert.IsType<DiscoveryHeld>(source.Held).Agents);
            Assert.Contains(AgentKind.Codex, source.List().DiscoveryHeld!.Agents);
            Assert.Null(source.Get(ids[250]));
            using var subscription = source.Subscribe();
            SessionEvent? last = null;
            while (subscription.Events.TryRead(out var change)) { last = change; }
            Assert.Contains(AgentKind.Codex, Assert.IsType<SessionsSynced>(last!.Data).DiscoveryHeld!.Agents);
        }
        await DuringAdmission(() =>
        {
            AssertHeld();
            Assert.Contains("250-session roster limit", source.Held!.Reason);
            if (previousHold) { Assert.Contains(AgentKind.Claude, source.Held.Agents); }
        });
        Assert.Equal([AgentKind.Codex], source.Held!.Agents);
        var ended = source.Snapshot()[0].Id;
        fixture.CodexRegistration(ended, 1000 + Array.IndexOf(ids, ended), null, status: "ended", ended: true);
        await DuringAdmission(() => { AssertHeld(); Assert.Null(source.Get(ended)); });
        Assert.NotNull(source.Get(ids[250]));
        Assert.Null(source.Held);
        Assert.Equal(250, source.Snapshot().Count);
    }

    [Theory]
    [InlineData(AgentKind.Claude, 700, false)]
    [InlineData(AgentKind.Claude, 701, false)]
    [InlineData(AgentKind.Codex, 700, false)]
    [InlineData(AgentKind.Codex, 701, false)]
    [InlineData(AgentKind.Claude, 701, true)]
    [InlineData(AgentKind.Codex, 701, true)]
    public async Task Native_successor_generation_supersedes_an_ended_registration_without_an_unlisted_unheld_moment(AgentKind agent, int successor, bool listed)
    {
        using var f = new Fixture();
        if (agent == AgentKind.Claude) { f.ClaudeRegistration(Id, 700, null); }
        else { f.CodexRegistration(Id, 700, null); }
        var exe = agent == AgentKind.Claude ? "claude.exe" : "codex.exe";
        var (replacing, gap) = (false, false);
        SessionSource? seen = null;
        await using var source = new SessionSource(f.Options(), f.Processes, f.Time, answerAttempted: _ =>
        {
            gap |= replacing && seen!.Get(Id) is null && seen.Held is null;
            return false;
        }, hostId: "host-id");
        seen = source;
        if (listed)
        {
            f.Processes.Run(agent, 700, exe);
            await source.RefreshAsync();
            Assert.Equal(700, source.Get(Id)!.ProcessId);
            f.Processes.Exit(agent, 700);
        }
        var root = agent == AgentKind.Claude ? f.ClaudeRegistrations : f.CodexRegistrations;
        var path = Path.Combine(root, Id + ".json");
        var registration = JsonNode.Parse(File.ReadAllText(path))!;
        registration["Ended"] = true;
        File.WriteAllText(path, registration.ToJsonString());
        var birth = Fixture.T0.AddMinutes(5);
        f.Processes.Run(agent, successor, exe, birth);
        if (agent == AgentKind.Claude) { f.ClaudeSessionFile(successor, Id, birth); }
        else { f.Processes.Holders[f.CodexLock(Id)] = [successor]; }
        replacing = listed;
        await source.RefreshAsync();
        Assert.Equal(successor, Assert.Single(source.Snapshot()).ProcessId);
        Assert.False(gap, "A mid-pass reader saw neither the live successor nor a discovery hold.");
        await source.RefreshAsync();
        Assert.Equal(successor, Assert.Single(source.Snapshot()).ProcessId);
        Assert.Null(source.Held);
    }

    [Theory]
    [InlineData(AgentKind.Claude, false)]
    [InlineData(AgentKind.Claude, true)]
    [InlineData(AgentKind.Codex, false)]
    [InlineData(AgentKind.Codex, true)]
    public async Task An_undated_native_successor_holds_until_its_birth_is_proved_or_it_exits(AgentKind agent, bool exits)
    {
        using var f = new Fixture();
        if (agent == AgentKind.Claude) { f.ClaudeRegistration(Id, 700, null); }
        else { f.CodexRegistration(Id, 700, null); }
        var exe = agent == AgentKind.Claude ? "claude.exe" : "codex.exe";
        f.Processes.Run(agent, 700, exe);
        await using var source = new SessionSource(f.Options(), f.Processes, f.Time, hostId: "host-id");
        await source.RefreshAsync();
        Assert.Equal(700, source.Get(Id)!.ProcessId);
        using var subscription = source.Subscribe();
        while (subscription.Events.TryRead(out _)) { }
        var root = agent == AgentKind.Claude ? f.ClaudeRegistrations : f.CodexRegistrations;
        var path = Path.Combine(root, Id + ".json");
        var registration = JsonNode.Parse(File.ReadAllText(path))!;
        registration["Ended"] = true;
        File.WriteAllText(path, registration.ToJsonString());
        f.Processes.Exit(agent, 700);
        f.Processes.RunUndated(agent, 701, exe);
        var birth = Fixture.T0.AddMinutes(5);
        if (agent == AgentKind.Claude) { f.ClaudeSessionFile(701, Id, birth); }
        else { f.Processes.Holders[f.CodexLock(Id)] = [701]; }
        await source.RefreshAsync();
        Assert.Contains(agent, Assert.IsType<DiscoveryHeld>(source.Held).Agents);
        Assert.Null(source.Get(Id));
        Assert.True(subscription.Events.TryRead(out var first));
        Assert.Equal(Events.DiscoveryChanged, first!.Name);
        f.Time.Advance(TimeSpan.FromHours(1));
        await source.RefreshAsync();
        Assert.Contains(agent, source.Held!.Agents);
        if (exits) { f.Processes.Exit(agent, 701); }
        else { f.Processes.Run(agent, 701, exe, birth); }
        await source.RefreshAsync();
        Assert.Null(source.Held);
        if (exits) { Assert.Empty(source.Snapshot()); }
        else { Assert.Equal(701, Assert.Single(source.Snapshot()).ProcessId); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task An_overflowed_runtime_keeps_its_resume_guard_while_it_runs_across_passes_and_restarts(bool restart, bool dated = true)
    {
        using var f = new Fixture();
        var ids = Enumerable.Range(0, 251).Select(i => $"{i:D8}-0000-4000-8000-000000000000").ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            f.CopilotSession(ids[i], null);
            var line = $"node C:/npm/node_modules/@github/copilot/index.js --session-id {ids[i]}";
            if (dated) { f.Processes.Run(AgentKind.Copilot, 1000 + i, line); } else { f.Processes.RunUndated(AgentKind.Copilot, 1000 + i, line); }
        }
        var options = f.Options(stateFile: Path.Combine(f.Root, "sessions-listed.json"));
        var source = new SessionSource(options, f.Processes, f.Time, hostId: "host-id");
        try
        {
            await source.RefreshAsync();
            var pid = 1000 + Array.IndexOf(ids, Assert.Single(ids.Except(source.Snapshot().Select(s => s.Id))));
            f.Processes.Unidentify(AgentKind.Copilot, pid);
            if (restart) { await source.DisposeAsync(); source = new SessionSource(options, f.Processes, f.Time, hostId: "host-id"); }
            await source.RefreshAsync();
            await source.RefreshAsync();
            Assert.Contains($"could not be read (pid {pid})", source.Held!.Reason);
            f.Processes.Exit(AgentKind.Copilot, pid);
            await source.RefreshAsync();
            Assert.Null(source.Held);
            Assert.Empty((HashSet<string>)typeof(SessionSource).GetField("_omitted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(source)!);
        }
        finally { await source.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_host_restart_keeps_resume_guard_but_not_roster_membership_for_an_unreadable_runtime(bool reused)
    {
        using var f = new Fixture();
        Fixture.Append(f.CopilotSession(Id, null), Copilot.User("resumed", 1));
        var command = $"node C:/npm/node_modules/@github/copilot/index.js --session-id {Id}";
        f.Processes.Run(AgentKind.Copilot, 300, command);
        var options = f.Options(stateFile: Path.Combine(f.Root, "sessions-listed.json"));
        await using (var before = new SessionSource(options, f.Processes, f.Time, hostId: "host-id"))
        {
            await before.RefreshAsync();
            Assert.Single(before.Snapshot());
        }
        f.Processes.Unidentify(AgentKind.Copilot, 300, reused ? Fixture.T0.AddMinutes(2) : null);
        for (var restart = 0; restart < 2; restart++)
        {
            await using var after = new SessionSource(options, f.Processes, f.Time, hostId: "host-id");
            await after.RefreshAsync();
            await after.RefreshAsync();
            Assert.Empty(after.Snapshot());
            Assert.Equal(!reused, after.Held?.Agents.Contains(AgentKind.Copilot) == true);
            Assert.Null(after.ProcessOf(Id));
        }
        f.Processes.Exit(AgentKind.Copilot, 300);
        await using var departed = new SessionSource(options, f.Processes, f.Time, hostId: "host-id");
        await departed.RefreshAsync();
        Assert.Null(departed.Held);
    }

    static void AssertRow(JsonElement row, string machine)
    {
        Assert.Equal(machine, row.GetProperty("machine").GetString());
        Assert.False(row.TryGetProperty("startedAt", out _)); // A process birth is not a proven session start.
        foreach (var name in new[] { "owner", "pr", "finished" })
        {
            Assert.Equal("unknown", row.GetProperty(name).GetProperty("state").GetString());
        }
    }

    sealed class CaptureChannel : IProtocolChannel
    {
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Channel<ChannelEvent> events = Channel.CreateUnbounded<ChannelEvent>();
        long sequence;
        public string PeerId => "fixture-client";
        public string PeerName => "Fixture subscriber";
        public string PeerKind => "fixture";
        public bool IsOpen => !closed.Task.IsCompleted;
        public Task Closed => closed.Task;
        public void Close() => closed.TrySetResult();
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var seq = Interlocked.Increment(ref sequence);
            Assert.True(events.Writer.TryWrite(new ChannelEvent(name, seq, JsonSerializer.SerializeToElement(data, ProtocolJson.Options))));
            return Task.FromResult(seq);
        }
        public async Task<ChannelEvent> NextAsync()
        {
            using var timeout = new CancellationTokenSource(Patience);
            return await events.Reader.ReadAsync(timeout.Token);
        }
    }
}
