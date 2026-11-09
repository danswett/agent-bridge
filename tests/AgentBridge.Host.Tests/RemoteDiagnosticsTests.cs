using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core.Diagnostics;
using AgentBridge.Host.Delivery;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// diagnostics.get's projection (docs/protocol/v1-diagnostics.md, #802). Who may ask is in
// RemoteAuthorizationTests; here, what an asker gets: closed codes and validated versions only,
// whatever the sources hold, typed missing parts, and bounded replies.
public sealed class RemoteDiagnosticsTests
{
    const string Canary = "CANARY-7f3a";
    const string Token = "ghp_CANARYtoken0123456789abcdefABCDEF0123";
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    static ReadinessCheck Check(ReadinessState state) => new(state, $"{Canary} stderr: token={Token} at C:\\Users\\{Canary}\\.copilot");

    static AgentReadinessReport Report(DateTimeOffset at) => new(at,
    [
        new AgentReadiness("copilot", $"Copilot {Canary}", $"C:\\Users\\{Canary}\\.copilot", $"C:\\{Canary}\\copilot.exe", "1.0.5",
            Check(ReadinessState.Working), Check(ReadinessState.Working), Check(ReadinessState.NeedsAction), Check(ReadinessState.Failed),
            Check(ReadinessState.Unknown), at.AddMinutes(-3)),
        // A launcher's configured id, and a version the agent printed with more than a version in it.
        new AgentReadiness($"custom-{Canary}", Canary, Canary, null, $"1.0 {Token}",
            Check(ReadinessState.Missing), Check(ReadinessState.Blocked), Check(ReadinessState.Missing), Check(ReadinessState.Missing),
            Check(ReadinessState.Missing)),
    ], ReadOnlyReason: $"{Canary} read-only {Token}");

    RemoteDiagnostics Diagnostics(IDiagnosticsJournalReader? journal = null, string version = "0.4.2-beta.1", bool bind = true, Action<string>? log = null)
    {
        var diagnostics = new RemoteDiagnostics(_ => true, journal, version, true,
            () => new DiscoveryHeld($"{Canary} could not read C:\\Users\\{Canary}\\session.jsonl {Token}", [AgentKind.Claude, AgentKind.Copilot]),
            () => (DiagnosticsReadOnly.AgentHaBridge, null), time, log);
        if (bind)
        {
            var started = time.GetUtcNow().AddMinutes(-90);
            diagnostics.Bind(() => new RelayStatus(RelayState.Connected, $"wss://{Canary}.example/?token={Token}", $"as {Canary}", started.AddMinutes(1)),
                () => Report(time.GetUtcNow()), started);
        }
        return diagnostics;
    }

    static JournalEntry Draft(JournalKind kind, JournalOutcome outcome, string? version = "0.4.2") =>
        JournalContract.Draft(kind, outcome, version: version, durationMs: 12);

    [Fact]
    public async Task Nothing_from_the_sources_but_codes_versions_and_times_reaches_the_snapshot()
    {
        var journal = new DiagnosticsJournal(time);
        Assert.True(journal.Record(Draft(JournalKind.HostStarted, JournalOutcome.Ok)));
        // The journal validates what it keeps: a version that is not one is withheld there too.
        journal.Record(Draft(JournalKind.HostStarted, JournalOutcome.Failed, $"{Canary} {Token}"));

        var snapshot = await Diagnostics(journal).SnapshotAsync(DiagnosticsProtocol.DefaultEntries, time.GetUtcNow());
        var json = JsonSerializer.Serialize(snapshot, ProtocolJson.Options);

        Assert.DoesNotContain(Canary, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ghp_", json, StringComparison.Ordinal);
        Assert.DoesNotContain("wss:", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", json, StringComparison.Ordinal);
        Assert.Equal("0.4.2-beta.1", snapshot.Host.Version);
        Assert.Equal(90 * 60, snapshot.Host.UptimeSeconds);
        Assert.Equal(DiagnosticsMissing.NotRecorded, snapshot.LastRestart.CauseMissing);
        Assert.Equal(DiagnosticsRelayState.Connected, snapshot.Relay.State);
        Assert.Equal(DiagnosticsReadOnly.AgentHaBridge, snapshot.Holds.ReadOnly);
        Assert.Equal([AgentKind.Copilot, AgentKind.Claude], snapshot.Holds.DiscoveryHeld);
        var copilot = snapshot.Readiness.Agents[0];
        Assert.Equal((DiagnosticsAgentId.Copilot, "1.0.5", DiagnosticsCheck.Failed), (copilot.Id, copilot.Version, copilot.Authentication));
        var other = snapshot.Readiness.Agents[1];
        Assert.Equal((DiagnosticsAgentId.Other, (string?)null, DiagnosticsMissing.Invalid), (other.Id, other.Version, other.VersionMissing));
        Assert.NotEmpty(snapshot.Journal.Entries);
        foreach (var entry in snapshot.Journal.Entries)
        {
            Assert.True(JournalCodec.TryRead(JsonSerializer.SerializeToUtf8Bytes(entry), out var read));
            Assert.NotNull(read);
        }
    }

    [Fact]
    public async Task What_is_not_known_is_said_as_a_code_never_as_a_healthy_default()
    {
        var snapshot = await Diagnostics(journal: null, version: $"dev {Canary}", bind: false).SnapshotAsync(10, time.GetUtcNow());

        Assert.Null(snapshot.Host.Version);
        Assert.Equal(DiagnosticsMissing.Invalid, snapshot.Host.VersionMissing);
        Assert.Equal((DiagnosticsRelayState.Unknown, DiagnosticsMissing.NotStarted), (snapshot.Relay.State, snapshot.Relay.Missing));
        Assert.Equal(DiagnosticsMissing.NotStarted, snapshot.Readiness.Missing);
        Assert.Empty(snapshot.Readiness.Agents);
        Assert.Equal(DiagnosticsMissing.NotConfigured, snapshot.Journal.Missing);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(snapshot, ProtocolJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_is_bounded_by_entries_and_bytes_whatever_is_asked()
    {
        var journal = new DiagnosticsJournal(time);
        for (var i = 0; i < JournalContract.MaxEntries; i++)
        {
            journal.Record(Draft(JournalKind.DeliveryStage, JournalOutcome.Ok));
        }
        var diagnostics = Diagnostics(journal);

        var few = await diagnostics.SnapshotAsync(DiagnosticsProtocol.ReadRequest(JsonDocument.Parse("""{"maxEntries":3}""").RootElement), time.GetUtcNow());
        Assert.Equal(3, few.Journal.Entries.Count);
        var most = await diagnostics.SnapshotAsync(DiagnosticsProtocol.ReadRequest(JsonDocument.Parse("""{"maxEntries":100000}""").RootElement), time.GetUtcNow());
        Assert.True(most.Journal.Entries.Count <= DiagnosticsProtocol.MaxEntries);
        Assert.True(most.Journal.Entries.Sum(e => e.GetRawText().Length) <= DiagnosticsProtocol.MaxJournalBytes);
        Assert.True(ProtocolJson.ByteCount(most) <= DiagnosticsProtocol.MaxResponseBytes);
        // The response goes in one channel message, even to a peer that does not inflate.
        Assert.True(ProtocolJson.ByteCount(new { re = long.MaxValue, ok = true, result = most }) <= SecureChannel.MaxMessageBytes);
    }

    [Fact]
    public async Task One_read_at_a_time_and_a_device_at_most_every_two_seconds_with_no_read_for_a_refusal()
    {
        var journal = new HeldJournal();
        var diagnostics = Diagnostics(journal);

        var first = diagnostics.ReadAsync("device-a", 5);
        await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Another device while the first read runs: refused at once, never queued behind it.
        Assert.Equal(RemoteDiagnostics.TooSoon, (await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostics.ReadAsync("device-b", 5))).Message);
        journal.Release();
        Assert.Equal(DiagnosticsProtocol.Schema, (await first.WaitAsync(TimeSpan.FromSeconds(10))).Snapshot.Schema);

        // The same device again before MinInterval: refused without a read.
        Assert.Equal(RemoteDiagnostics.TooSoon, (await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostics.ReadAsync("device-a", 5))).Message);
        Assert.Equal(1, journal.Reads);
        // The device refused while the other read ran was not counted as having asked.
        await diagnostics.ReadAsync("device-b", 5);
        time.Advance(DiagnosticsProtocol.MinInterval);
        await diagnostics.ReadAsync("device-a", 5);
        Assert.Equal(3, journal.Reads);
    }

    [Fact]
    public async Task A_read_that_cannot_finish_by_the_deadline_is_given_up_and_frees_the_slot()
    {
        var journal = new HeldJournal();
        var diagnostics = Diagnostics(journal);

        var first = diagnostics.ReadAsync("device-a", 5);
        await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(first.IsCompleted);
        time.Advance(DiagnosticsProtocol.ServerDeadline);

        // The fixed text, never the cancellation's own.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(RemoteDiagnostics.Unfinished, refused.Message);
        Assert.Null(refused.InnerException);
        journal.Release();
        Assert.Equal(DiagnosticsProtocol.Schema, (await diagnostics.ReadAsync("device-b", 5).WaitAsync(TimeSpan.FromSeconds(10))).Snapshot.Schema);
    }

    [Fact]
    public async Task A_read_whose_caller_cancels_is_given_up_and_frees_the_slot()
    {
        var journal = new HeldJournal();
        var diagnostics = Diagnostics(journal);
        using var cancel = new CancellationTokenSource();

        var first = diagnostics.ReadAsync("device-a", 5, cancellationToken: cancel.Token);
        await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();

        Assert.Equal(RemoteDiagnostics.Unfinished, (await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)))).Message);
        journal.Release();
        await diagnostics.ReadAsync("device-b", 5).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, journal.Reads);
    }

    [Fact]
    public async Task A_channel_already_closed_gets_no_read()
    {
        var journal = new HeldJournal();
        journal.Release();
        var diagnostics = Diagnostics(journal);

        Assert.Equal(RemoteDiagnostics.Unfinished,
            (await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostics.ReadAsync("device-a", 5, Task.CompletedTask))).Message);
        Assert.Equal(0, journal.Reads);
    }

    [Fact]
    public async Task The_hosts_log_is_never_written_while_a_read_is_bounded_and_what_went_wrong_is_logged_after_the_reply()
    {
        // The host's log as one stuck on its console or file: a read that wrote to it would hang.
        using var stuck = new ManualResetEventSlim();
        var logged = 0;
        void Log(string _) { Interlocked.Increment(ref logged); stuck.Wait(TimeSpan.FromSeconds(30)); }
        var journal = new HeldJournal();
        var diagnostics = Diagnostics(journal, log: Log);

        var first = diagnostics.ReadAsync("device-a", 5);
        await journal.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(DiagnosticsProtocol.ServerDeadline);
        Assert.Equal(RemoteDiagnostics.Unfinished, (await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)))).Message);
        Assert.Equal(1, diagnostics.GivenUp);

        journal.Release();
        var served = await diagnostics.ReadAsync("device-b", 5).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, Volatile.Read(ref logged));
        Assert.Contains("1 given up", served.Note, StringComparison.Ordinal);

        // A journal that cannot be read is sent as failed, and its type, never its text, is logged.
        var failing = Diagnostics(new FailingJournal(), log: Log);
        var read = await failing.ReadAsync("device-a", 5).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DiagnosticsMissing.Failed, read.Snapshot.Journal.Missing);
        Assert.Contains($"({nameof(IOException)})", read.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, read.Note, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref logged));
        stuck.Set();
    }

    [Fact]
    public async Task The_relay_is_read_from_the_host_control_even_while_its_status_is_stuck_on_a_probe()
    {
        var folder = Path.Combine(Path.GetTempPath(), "abh-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var control = new HostControl(new HostControlOptions
            {
                Profile = "test",
                Folder = folder,
                Version = "0.4.2",
                Machine = new MachineInfo("host-id", "Test host", "windows", "0.4.2"),
                Running = new HostSettingsView(RemoteApproval: true),
                Account = () => null,
                Relay = () => new Uri($"wss://{Canary}.example/?token={Token}"),
                // agent-ha-bridge's look stuck in Windows, while Status holds HostControl's lock.
                AgentHaBridge = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); return null; },
                Time = time,
                SignIn = (_, _, _) => Task.FromException<SignInResult>(new InvalidOperationException()),
                SignedIn = (_, _) => { },
                Stop = () => { },
                Restart = () => { },
            });
            control.SetRelay(RelayState.Connected, $"as {Canary}");
            var diagnostics = Diagnostics();
            diagnostics.Bind(() => control.RelayNow, () => null, control.StartedAt);

            var status = Task.Run(() => control.Status(fresh: true));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var read = diagnostics.ReadAsync("device-a", 5);
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10))));
            var relay = (await read).Snapshot.Relay;
            Assert.Equal((DiagnosticsRelayState.Connected, time.GetUtcNow()), (relay.State, relay.Since));
            Assert.False(status.IsCompleted);
            Assert.DoesNotContain(Canary, JsonSerializer.Serialize(relay, ProtocolJson.Options), StringComparison.Ordinal);

            release.Set();
            await status.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            Directory.Delete(folder, recursive: true);
        }
    }

    sealed class FailingJournal : IDiagnosticsJournalReader
    {
        public JournalSnapshot Read(int maxEntries, int maxBytes) => throw new InvalidOperationException("diagnostics.get reads with ReadAsync");

        public ValueTask<JournalSnapshot> ReadAsync(int maxEntries, int maxBytes, CancellationToken cancellationToken) =>
            ValueTask.FromException<JournalSnapshot>(new IOException($"C:\\Users\\{Canary}\\diagnostics-journal.jsonl {Token}"));
    }

    // A journal whose read waits until released, as one held by a writer slowed by its file.
    sealed class HeldJournal : IDiagnosticsJournalReader
    {
        int reads;
        readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads => Volatile.Read(ref reads);
        public void Release() => released.TrySetResult();

        public JournalSnapshot Read(int maxEntries, int maxBytes) => throw new InvalidOperationException("diagnostics.get reads with ReadAsync");

        public async ValueTask<JournalSnapshot> ReadAsync(int maxEntries, int maxBytes, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref reads);
            Entered.TrySetResult();
            await released.Task.WaitAsync(cancellationToken);
            return new JournalSnapshot([], 0, 0, 0, 0, 0, 0, 0, false, 0);
        }
    }
    [Theory]
    [InlineData("""{"maxEntries":5,"principal":"agent"}""")]
    [InlineData("""{"origin":"copilot"}""")]
    // An agent's word nested in what a person's device sends is refused, never followed.
    [InlineData("""{"maxEntries":5,"on":{"principal":"agent","origin":"copilot"}}""")]
    [InlineData("""{"signed":{"by":"agent","role":"person"},"maxEntries":5}""")]
    [InlineData("""{"maxEntries":{"principal":"agent"}}""")]
    [InlineData("""{"maxEntries":"5"}""")]
    [InlineData("""{"maxEntries":0}""")]
    [InlineData("""{"maxEntries":5,"maxEntries":6}""")]
    [InlineData("""[5]""")]
    public void A_request_names_only_maxEntries(string request) =>
        Assert.Throws<ArgumentException>(() => DiagnosticsProtocol.ReadRequest(JsonDocument.Parse(request).RootElement));

    [Fact]
    public async Task This_machines_pipe_never_reads_diagnostics_even_when_the_check_would_pass()
    {
        var diagnostics = Diagnostics(new DiagnosticsJournal(time));
        var pipe = new PipeChannel();

        Assert.False(diagnostics.Offered(pipe));
        Assert.Equal(RemoteDiagnostics.Refusal, (await Assert.ThrowsAsync<InvalidOperationException>(() => diagnostics.HandleAsync(pipe, default))).Message);
    }

    [Fact]
    public async Task G5_is_reported_from_the_last_look_and_a_read_never_looks_or_waits_on_one()
    {
        var probe = new StuckProbe();
        var g5 = new SharedAgentHaBridgeProbe(probe, time);
        RemoteDiagnostics Read() => new(_ => true, null, "0.4.2", false, () => null, () => RemoteDiagnostics.AgentHaBridge(g5), time);
        (DiagnosticsReadOnly?, DiagnosticsMissing?) Unknown(DiagnosticsMissing why) => (null, why);

        // No look yet: unknown, sent as null, never none.
        var none = await Read().SnapshotAsync(DiagnosticsProtocol.DefaultEntries, time.GetUtcNow());
        Assert.Equal(Unknown(DiagnosticsMissing.NotStarted), (none.Holds.ReadOnly, none.Holds.ReadOnlyMissing));
        Assert.Contains("\"readOnly\":null", JsonSerializer.Serialize(none, ProtocolJson.Options), StringComparison.Ordinal);
        Assert.Equal(0, probe.Looks);

        g5.Active();
        Assert.Equal((DiagnosticsReadOnly.None, (DiagnosticsMissing?)null), RemoteDiagnostics.AgentHaBridge(g5));
        probe.Reason = "running";
        g5.Active();
        Assert.Equal((DiagnosticsReadOnly.AgentHaBridge, (DiagnosticsMissing?)null), RemoteDiagnostics.AgentHaBridge(g5));
        // A look that could not tell is refused like running, but reported as unknown, not as running.
        probe.Reason = IAgentHaBridgeProbe.Unchecked;
        g5.Active();
        Assert.Equal(Unknown(DiagnosticsMissing.Failed), RemoteDiagnostics.AgentHaBridge(g5));
        time.Advance(SharedAgentHaBridgeProbe.Reported);
        Assert.Equal(Unknown(DiagnosticsMissing.NotStarted), RemoteDiagnostics.AgentHaBridge(g5));
        Assert.Equal(3, probe.Looks);

        // A stale cache and a look stuck in Windows, holding Recent's lock: a read still answers at once.
        probe.Release.Reset();
        probe.Entered.Reset();
        var stuck = Task.Run(() => g5.Recent());
        try
        {
            Assert.True(probe.Entered.Wait(TimeSpan.FromSeconds(10)));
            var read = Read().SnapshotAsync(DiagnosticsProtocol.DefaultEntries, time.GetUtcNow());
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.Equal(Unknown(DiagnosticsMissing.NotStarted), ((await read).Holds.ReadOnly, (await read).Holds.ReadOnlyMissing));
            Assert.False(stuck.IsCompleted);
            Assert.Equal(4, probe.Looks);
        }
        finally { probe.Release.Set(); }
        await stuck;
    }

    sealed class StuckProbe : IAgentHaBridgeProbe
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new(true);
        public volatile string? Reason;
        int looks;
        public int Looks => Volatile.Read(ref looks);

        public string? Active()
        {
            Interlocked.Increment(ref looks);
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            return Reason;
        }
    }

    sealed class PipeChannel : IProtocolChannel
    {
        public string PeerId => "local-4242";
        public string PeerName => "copilot";
        public string PeerKind => "app";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
