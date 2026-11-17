using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using AgentBridge.Roster;
using Xunit;

public sealed class FleetStartedAtTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    static async Task<JsonElement> PublishAsync(ISessionSource source)
    {
        JsonElement record = default;
        Assert.True(await new FleetPublisher(source, "machine").PublishAsync((r, _) => { record = r; return Task.CompletedTask; }));
        return record;
    }

    [Theory]
    [InlineData(AgentKind.Copilot)]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    public async Task Only_a_bound_process_start_is_published_floored_to_utc_and_resume_renews_it(AgentKind agent)
    {
        using var fixture = new Fixture();
        var start = Fixture.T0.AddMilliseconds(-1).ToOffset(TimeSpan.FromHours(5.5));
        if (agent == AgentKind.Copilot) { fixture.CopilotSession(Id, 100); }
        else if (agent == AgentKind.Claude) { fixture.ClaudeRegistration(Id, 100, null); }
        else { fixture.CodexRegistration(Id, 100, null); }
        fixture.Processes.Run(agent, 100, startedAt: start);
        await using var source = fixture.Source();
        await source.RefreshAsync();
        Assert.Null(source.Held);
        Assert.Equal(start, source.ProcessOf(Id)!.StartedAt);
        Assert.Equal("2026-10-28T03:59:59Z", (await PublishAsync(source)).GetProperty("sessions")[0].GetProperty("startedAt").GetString());
        fixture.Processes.RunUndated(agent, 100);
        await source.RefreshAsync();
        Assert.False((await PublishAsync(source)).GetProperty("sessions")[0].TryGetProperty("startedAt", out _));
        fixture.Processes.Exit(agent, 100);
        await source.RefreshAsync();
        Assert.Empty(source.Snapshot());
        fixture.Processes.Run(agent, 100, startedAt: Fixture.T0);
        await source.RefreshAsync();
        Assert.Equal("2026-10-28T04:00:00Z", (await PublishAsync(source)).GetProperty("sessions")[0].GetProperty("startedAt").GetString());
    }

    [Fact]
    public async Task Missing_process_evidence_never_uses_transcript_start_or_state_time_and_legacy_records_still_read()
    {
        var source = new FleetPublisherTests.Source();
        source.Sessions = [FleetPublisherTests.NewSession(1) with { StartedAt = Fixture.T0 }];
        foreach (var binding in new SessionProcess?[] { null, new(100, null) })
        {
            source.Binding = _ => binding;
            var record = await PublishAsync(source);
            Assert.False(record.GetProperty("sessions")[0].TryGetProperty("startedAt", out _));
            var machines = RelayFleetRoster.Parse([JsonSerializer.SerializeToElement(new { machine = "machine", online = true, record })]);
            Assert.Single(Assert.Single(machines).Sessions);
        }
    }

    [Fact]
    public async Task Signed_byte_fitting_keeps_recent_entries_reports_omissions_and_preserves_wire_caps()
    {
        var source = new FleetPublisherTests.Source
        {
            Sessions = Enumerable.Range(1, 64).Select(i => FleetPublisherTests.NewSession(i) with { StatusSince = Fixture.T0.AddTicks(i) }).ToList(),
            Binding = _ => new(100, Fixture.T0),
        };
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        byte[] point = [4, .. q.X!, .. q.Y!];
        // Ordinary 64-row records fit; oversize the fixture header to exercise defensive byte fitting.
        var calls = 0;
        var publisher = new FleetPublisher(source, new string('m', 2048), _ => new string('+', 64), sign: r =>
        {
            calls++;
            Assert.True(Encoding.UTF8.GetByteCount(r.GetRawText()) + 105 <= 16 * 1024, "Oversized candidate reached signer");
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(new { t = "registry-put", id = long.MinValue, record = r }, ProtocolJson.Options).Length + 105 <= 24 * 1024);
            return FleetRecordSigning.Sign(r, bytes => P256.FromRaw(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        });
        Assert.True(await publisher.PublishAsync((record, _) =>
        {
            var entries = record.GetProperty("sessions");
            Assert.InRange(entries.GetArrayLength(), 1, 63);
            Assert.True(record.GetProperty("truncated").GetBoolean());
            Assert.Equal(64, record.GetProperty("total").GetInt32());
            Assert.Equal(source.Sessions.AsEnumerable().Reverse().Take(entries.GetArrayLength()).Select(s => s.Id[..16]),
                entries.EnumerateArray().Select(s => s.GetProperty("id").GetString()));
            Assert.All(entries.EnumerateArray(), s => Assert.Equal("2026-10-28T04:00:00Z", s.GetProperty("startedAt").GetString()));
            Assert.True(FleetRecordSigning.Verify(record, point));
            Assert.True(Encoding.UTF8.GetByteCount(record.GetRawText()) <= 16 * 1024);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(new { t = "registry-put", id = long.MinValue, record }, ProtocolJson.Options).Length <= 24 * 1024);
            return Task.CompletedTask;
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task An_oversized_header_is_refused_without_calling_the_signer()
    {
        var calls = 0;
        var publisher = new FleetPublisher(new FleetPublisherTests.Source(), new string('m', 16 * 1024),
            sign: r => { calls++; return r; });
        await Assert.ThrowsAsync<InvalidDataException>(() => publisher.PublishAsync((_, _) =>
            throw new InvalidOperationException("Oversized record reached transport")));
        Assert.Equal(0, calls);
    }

    [Fact, Trait("Category", "Process")]
    public async Task Restarting_a_disposable_publisher_host_keeps_the_live_session_start_but_resets_epoch_and_sequence()
    {
        using var fixture = new Fixture();
        using var agent = HostCliProcessTests.StartFixtureAgent(fixture);
        try
        {
            Assert.Equal("ready", await agent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            fixture.CopilotSession(Id, agent.Id, lockWrite: DateTimeOffset.UtcNow);
            var first = await RunHostAsync(fixture, agent.Id);
            Assert.False(agent.HasExited);
            var restarted = await RunHostAsync(fixture, agent.Id);
            Assert.False(agent.HasExited);
            var before = first[^1];
            var after = restarted[0];
            var startedAt = before.GetProperty("sessions")[0].GetProperty("startedAt").GetString();
            Assert.Equal(agent.StartTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture), startedAt);
            Assert.Equal(startedAt, after.GetProperty("sessions")[0].GetProperty("startedAt").GetString());
            Assert.NotEqual(before.GetProperty("epoch").GetString(), after.GetProperty("epoch").GetString());
            Assert.Equal(2, before.GetProperty("seq").GetInt64());
            Assert.Equal(1, after.GetProperty("seq").GetInt64());
            Assert.True(after.GetProperty("at").GetDateTimeOffset() > before.GetProperty("at").GetDateTimeOffset());
        }
        finally
        {
            if (!agent.HasExited) { agent.Kill(); }
            await agent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    static async Task<JsonElement[]> RunHostAsync(Fixture fixture, int pid)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { typeof(AgentBridge.Host.Tests.Program).Assembly.Location, "fleet-start-child",
            Path.Combine(fixture.Root, "home"), pid.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        { start.ArgumentList.Add(arg); }
        using var host = Process.Start(start)!;
        var output = host.StandardOutput.ReadToEndAsync();
        var error = host.StandardError.ReadToEndAsync();
        try { await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally
        {
            if (!host.HasExited) { host.Kill(entireProcessTree: true); }
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(0, host.ExitCode);
        Assert.Empty(await error);
        var records = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonSerializer.Deserialize<JsonElement>(s)).ToArray();
        Assert.Equal(2, records.Length);
        return records;
    }

    public static async Task<int> PublishChildAsync(string home, int pid)
    {
        // Inspect only the fixture's real process; never enroll other sessions on this machine.
        var seen = new SystemProcessInspector().GetAgentProcesses(AgentKind.Copilot).Single(p => p.Id == pid);
        var processes = new FakeProcesses();
        processes.Run(AgentKind.Copilot, seen.Id, seen.CommandLine, seen.StartedAt);
        var options = ReadCommandRoots.Resolve(["sessions", "--read-root", home], () => throw new InvalidOperationException("fixture roots required"));
        await using var source = new SessionSource(options, processes);
        await source.RefreshAsync();
        Assert.Null(source.Held);
        Assert.Single(source.Snapshot());
        var publisher = new FleetPublisher(source, "fixture-host");
        for (var i = 0; i < 2; i++)
        {
            await publisher.PublishAsync((record, _) => { Console.WriteLine(record.GetRawText()); return Task.CompletedTask; }, force: true);
        }
        return 0;
    }
}
