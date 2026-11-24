using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using Xunit;

// #2768: the owner may raise the FLEET caps temporarily. The raise is a block in the host's config at the machine (never a wire request); the host stamps the first
// time it sees a given block, so the 4 hours are stored, not a timer: a restart keeps them, an edited block starts new ones, and a longer expiry cannot be written.
public sealed class LimitRaiseTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-limit-raise-").FullName;
    static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    public void Dispose() => Directory.Delete(folder, true);

    static AgentLaunchLimits Limits(string agents) => HostSettings.Parse("{\"agents\":" + agents + "}", "config.json").Settings.Agents.Limits.Launch;
    static AgentLaunchLimits Raised(int perHour = 20, int live = 12, string basis = "") =>
        Limits("{\"enabled\":true,\"limits\":{" + basis + "\"raise\":{\"fleetPerHour\":" + perHour + ",\"liveFleetWorkers\":" + live + "}}}");
    string[] Rows() => Directory.Exists(Path.Combine(folder, AgentAuditLog.FolderName))
        ? Directory.GetFiles(Path.Combine(folder, AgentAuditLog.FolderName), "agents-*.jsonl").SelectMany(File.ReadAllLines).Where(r => r.Contains("fleet.limit.raise")).ToArray() : [];

    [Fact]
    public void A_temporary_raise_lapses_after_four_hours_and_the_refusal_names_the_limit_in_force()
    {
        var audit = new AgentAuditLog(folder);
        var raised = Raised();
        var now = LimitRaise.Effective(folder, raised, T0, audit);
        Assert.Equal((20, 12), (now.PerHour, now.Live));
        Assert.Contains("temporary raise until 14:00 UTC", now.Note);
        var late = LimitRaise.Effective(folder, raised, T0 + TimeSpan.FromHours(4) - TimeSpan.FromSeconds(1), audit);
        Assert.Equal((20, 12), (late.PerHour, late.Live));
        var lapsed = LimitRaise.Effective(folder, raised, T0 + TimeSpan.FromHours(4), audit);
        Assert.Equal((8, 6, null), (lapsed.PerHour, lapsed.Live, lapsed.Note));
        // The cap refusal says which limits were in force, and the raise while it lasted.
        var store = new LaunchProvenance(Path.Combine(folder, "p.json"), _ => { });
        Assert.Null(store.ReserveFleet(Guid.NewGuid(), "one", null, new(1, 60, "temporary raise until 14:00 UTC"), T0, _ => null));
        var refusal = store.ReserveFleet(Guid.NewGuid(), "two", null, new(1, 60, "temporary raise until 14:00 UTC"), T0, _ => null);
        Assert.Contains("limits in force: 1 an hour, 60 live; temporary raise until 14:00 UTC", refusal);
    }

    [Fact]
    public void A_restart_keeps_the_expiry_and_a_lapsed_raise_is_never_extended_by_reading_it_again()
    {
        var raised = Raised();
        Assert.Equal(20, LimitRaise.Effective(folder, raised, T0, null).PerHour);
        Assert.Equal(20, LimitRaise.Effective(folder, raised, T0 + TimeSpan.FromHours(3), null).PerHour); // a fresh read after a restart: same 4 hours
        Assert.Equal(8, LimitRaise.Effective(folder, raised, T0 + TimeSpan.FromHours(5), null).PerHour);
        Assert.Equal(8, LimitRaise.Effective(folder, raised, T0 + TimeSpan.FromHours(6), null).PerHour);
    }

    [Fact]
    public void An_edited_block_starts_a_new_four_hours()
    {
        LimitRaise.Effective(folder, Raised(), T0, null);
        Assert.Equal(8, LimitRaise.Effective(folder, Raised(), T0 + TimeSpan.FromHours(5), null).PerHour);
        var edited = LimitRaise.Effective(folder, Raised(perHour: 30), T0 + TimeSpan.FromHours(5), null);
        Assert.Equal(30, edited.PerHour);
        Assert.Contains("until 19:00 UTC", edited.Note);
    }

    [Fact]
    public void A_raise_above_60_is_60_and_a_raise_below_the_base_never_lowers_it()
    {
        var high = LimitRaise.Effective(folder, Raised(perHour: 70, live: 61), T0, null);
        Assert.Equal((60, 60), (high.PerHour, high.Live));
        var low = LimitRaise.Effective(folder, Raised(perHour: 5, live: 4, basis: "\"fleetPerHour\":10,\"liveFleetWorkers\":9,"), T0, null);
        Assert.Equal((10, 9), (low.PerHour, low.Live));
    }

    [Fact]
    public void A_raise_whose_stamp_cannot_be_written_is_ignored_and_a_clock_that_moves_back_ignores_it()
    {
        Directory.CreateDirectory(Path.Combine(folder, "limit-raise.json"));
        Assert.Equal((8, 6), (LimitRaise.Effective(folder, Raised(), T0, null).PerHour, LimitRaise.Effective(folder, Raised(), T0, null).Live));
        Directory.Delete(Path.Combine(folder, "limit-raise.json"));
        Assert.Equal(20, LimitRaise.Effective(folder, Raised(), T0, null).PerHour);
        Assert.Equal(8, LimitRaise.Effective(folder, Raised(), T0 - TimeSpan.FromHours(2), null).PerHour);
    }

    [Fact]
    public void Starting_and_lapsing_are_each_audited_once()
    {
        var audit = new AgentAuditLog(folder);
        var raised = Raised();
        for (var i = 0; i < 3; i++) { LimitRaise.Effective(folder, raised, T0 + TimeSpan.FromMinutes(i), audit); }
        LimitRaise.Effective(folder, raised, T0 + TimeSpan.FromHours(4), audit);
        LimitRaise.Effective(folder, raised, T0 + TimeSpan.FromHours(5), audit);
        var rows = Rows();
        Assert.Equal(2, rows.Length);
        Assert.Contains("started", rows[0]);
        Assert.Contains("fleetPerHour 20", rows[0]);
        Assert.Contains("lapsed", rows[1]);
    }

    [Fact]
    public void No_raise_block_means_the_base_limits_and_no_stamp()
    {
        var plain = Limits("{\"enabled\":true,\"limits\":{\"fleetPerHour\":12}}");
        var effective = LimitRaise.Effective(folder, plain, T0, null);
        Assert.Equal((12, 6, null), (effective.PerHour, effective.Live, effective.Note));
        Assert.False(File.Exists(Path.Combine(folder, "limit-raise.json")));
    }

    [Fact]
    public void The_raise_is_set_only_in_the_config_file_never_through_config_set()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), "{\"version\":1}");
        foreach (var key in new[] { "agents.limits.raise.fleetPerHour", "agents.limits.raise", "agents.limits.fleetPerHour", "agents.fleetLaunch.enabled" })
        {
            Assert.NotEqual(0, ConfigCommands.Run(["config", "set", key, "60"], folder, output, error));
        }
        Assert.DoesNotContain("raise", File.ReadAllText(Path.Combine(folder, HostSettings.FileName)));
    }

    [Fact]
    public void The_raise_block_is_read_with_unknown_keys_said_and_invalid_values_ignored()
    {
        var parsed = HostSettings.Parse("""{"agents":{"enabled":true,"limits":{"raise":{"fleetPerHour":"many","liveFleetWorkers":0,"other":1}}}}""", "config.json").Settings;
        Assert.False(parsed.Agents.Limits.Launch.HasRaise);
        Assert.Contains(parsed.AgentsProblems, p => p.Contains("raise.other"));
    }
}
