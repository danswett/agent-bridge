using System.Text.Json;
using AgentBridge.Host.Fleet;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class FleetNamesTests
{
    readonly WarningRoster roster = new();
    const string OtherId = "99999999-9999-4999-8999-999999999999";

    async Task<string?> PublishedNameAsync()
    {
        JsonElement record = default;
        await new FleetPublisher(source, "host", service.FleetNames!.Get).PublishAsync((r, _) => { record = r; return Task.CompletedTask; });
        return record.GetProperty("sessions")[0].TryGetProperty("name", out var name) ? name.GetString() : null;
    }

    [Theory]
    [InlineData("away", true, false, "SG Quality Lead", "present")]
    [InlineData("host", true, false, "SG Quality Lead", "absent")]
    [InlineData("away", false, false, "SG Quality Lead", "absent")]
    [InlineData("away", true, false, "sg quality lead", "absent")]
    [InlineData("away", true, false, "Unrelated", "absent")]
    [InlineData("away", true, true, "Unrelated", "unknown")]
    [InlineData("away", false, true, "Unrelated", "absent")]
    [InlineData("away", true, true, "SG Quality Lead", "present")]
    public async Task Duplicate_checks_use_live_exact_names_and_only_exclude_the_host_derived_self(
        string machine, bool online, bool truncated, string label, string expected)
    {
        var node = HaContract.NodeId(Id);
        roster.Read = _ => Task.FromResult(new FleetRosterSnapshot("scope",
            [new(machine, online, [new(node, label, "idle", Fixture.T0, node["agent_bridge_".Length..])], truncated)]));
        var result = await CallAsync(new { id = Id, name = " SG Quality Lead ", machine = "away" });
        Assert.Equal(expected, result.GetProperty("duplicateStatus").GetString());
        Assert.Equal(1, roster.Reads);
        Assert.Equal(["duplicateStatus", "id", "name"], result.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task A_newer_live_local_squatter_reloaded_from_host_state_still_warns_the_original_session()
    {
        fixture.CopilotSession(OtherId, 101, name: "Unrelated", lockWrite: Fixture.T0.AddMinutes(1));
        fixture.Processes.Run(AgentKind.Copilot, 101, startedAt: Fixture.T0.AddMinutes(1));
        await source.RefreshAsync();
        File.WriteAllText(PathToNames, JsonSerializer.Serialize(new Dictionary<string, string>
            { [Id] = "SG Quality Lead", [OtherId] = "SG Quality Lead" }));
        service = MakeService();
        roster.Read = _ => throw new InvalidOperationException("registry unavailable");
        var result = await CallAsync(new { id = Id, name = "SG Quality Lead" });
        Assert.Equal("present", result.GetProperty("duplicateStatus").GetString());
        Assert.Equal("SG Quality Lead", Saved()[Id]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Unavailable_unscoped_or_malformed_rosters_are_unknown_not_absent(bool emptyScope, bool malformed)
    {
        roster.Read = _ => malformed ? throw new FormatException("invalid registry timestamp")
            : emptyScope ? Task.FromResult(new FleetRosterSnapshot("", []))
            : throw new InvalidOperationException("registry unavailable");
        var result = await CallAsync(new { id = Id, name = "SG Quality Lead" });
        Assert.Equal("unknown", result.GetProperty("duplicateStatus").GetString());
        Assert.Equal("SG Quality Lead", Saved()[Id]);
    }

    [Fact]
    public async Task Validation_precedes_the_warning_read_and_does_not_change_a_previous_publication()
    {
        await CallAsync(new { id = Id, name = "Original" });
        var reads = roster.Reads;
        await Assert.ThrowsAsync<ArgumentException>(() => CallAsync(new { id = Id, name = "bad/name" }));
        Assert.Equal(reads, roster.Reads);
        Assert.Equal("Original", Saved()[Id]);
        Assert.Equal("Original", await PublishedNameAsync());
    }

    [Fact]
    public async Task A_warning_check_precedes_the_write_and_a_failed_write_never_claims_saved()
    {
        await CallAsync(new { id = Id, name = "Original" });
        File.Delete(PathToNames);
        Directory.CreateDirectory(PathToNames);
        roster.Read = _ =>
        {
            Assert.Equal("Original", service.FleetNames!.Get(Id));
            return Task.FromResult(new FleetRosterSnapshot("scope", []));
        };
        var failure = await Record.ExceptionAsync(() => CallAsync(new { id = Id, name = "Replacement" }));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(2, roster.Reads);
        Assert.Equal("Original", await PublishedNameAsync());
    }

    [Fact]
    public async Task A_stalled_check_cancels_at_two_seconds_saves_once_and_ignores_a_late_result()
    {
        var pending = new TaskCompletionSource<FleetRosterSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        roster.Read = _ => pending.Task;
        var saving = CallAsync(new { id = Id, name = "Original" });
        try
        {
            Assert.False(saving.IsCompleted);
            Assert.False(File.Exists(PathToNames));
            fixture.Time.Advance(TimeSpan.FromMilliseconds(1999));
            Assert.False(saving.IsCompleted);
            fixture.Time.Advance(TimeSpan.FromMilliseconds(1));
            var result = await saving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("unknown", result.GetProperty("duplicateStatus").GetString());
            Assert.True(roster.Token.IsCancellationRequested);
            Assert.Equal("Original", Saved()[Id]);
            File.SetLastWriteTimeUtc(PathToNames, Fixture.T0.UtcDateTime);
            pending.SetResult(new("scope", [new("away", true, [new("other", "Original", "idle", Fixture.T0)])]));
            await pending.Task;
            Assert.Equal("unknown", result.GetProperty("duplicateStatus").GetString());
            Assert.Equal(Fixture.T0.UtcDateTime, File.GetLastWriteTimeUtc(PathToNames));
            Assert.Equal(1, roster.Reads);
        }
        finally { pending.TrySetResult(new("scope", [])); }
    }

    [Fact]
    public async Task Closing_the_call_during_a_check_cancels_the_read_and_prevents_saving()
    {
        var pending = new TaskCompletionSource<FleetRosterSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        roster.Read = _ => pending.Task;
        var saving = CallAsync(new { id = Id, name = "Never saved" });
        try
        {
            channel.Closing.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(roster.Token.IsCancellationRequested);
            Assert.False(File.Exists(PathToNames));
        }
        finally { pending.TrySetResult(new("scope", [])); }
    }

    sealed class WarningRoster : IScopedFleetRoster
    {
        public int Reads;
        public CancellationToken Token;
        public Func<CancellationToken, Task<FleetRosterSnapshot>> Read = _ => Task.FromResult(new FleetRosterSnapshot("scope", []));
        public Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct) { Reads++; Token = ct; return Read(ct); }
        public async Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) => (await ReadScopedAsync(ct)).Machines;
    }
}
