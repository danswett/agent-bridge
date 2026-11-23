using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Roster;
using Xunit;

// #55 R3b (#1359): which peers security.roster "enforce" lets run an op. The channel side (the refusal, XX, IK and early
// requests) is AgentBridge.Transport.Tests.RosterGateTests; this is the roster's answer to it.
public sealed class HostRosterGateTests
{
    readonly World w = new();

    public HostRosterGateTests() { w.Genesis(); w.Add("B", "G", "B", "G"); }

    [Fact]
    public void With_no_roster_every_device_is_refused()
    {
        var gate = new HostRosterGate(() => null);
        Assert.False(gate.Allows(w.Id("G"), "hello"));
        Assert.False(gate.Allows(w.Id("B"), "sessions.list"));
    }

    [Fact]
    public void A_device_the_roster_does_not_list_is_refused_and_a_listed_one_is_served()
    {
        var gate = new HostRosterGate(() => w.Verifier("G", "B"));
        Assert.True(gate.Allows(w.Id("G"), "sessions.list"));
        Assert.True(gate.Allows(w.Id("B"), "sessions.list"));
        Assert.False(gate.Allows(w.Id("X"), "sessions.list"));
        Assert.False(gate.Allows("not-a-device-id", "hello"));
    }

    [Fact]
    public void A_removed_device_is_refused()
    {
        w.Remove("R", "G", "B", "B");
        var gate = new HostRosterGate(() => w.Verifier());
        Assert.True(gate.Allows(w.Id("G"), "hello"));
        Assert.False(gate.Allows(w.Id("B"), "hello"));
    }

    [Fact]
    public void A_device_on_probation_is_refused_an_op_that_needs_a_senior_device_and_served_the_rest()
    {
        var verifier = w.Verifier("G", "B");  // its clock is w.Now, read as each question is asked
        var gate = new HostRosterGate(() => verifier, new HashSet<string> { "senior.op" });
        Assert.False(gate.Allows(w.Id("B"), "senior.op"));
        Assert.True(gate.Allows(w.Id("B"), "sessions.list"));
        Assert.True(gate.Allows(w.Id("G"), "senior.op"));
        w.Now += RosterVerifier.Probation + TimeSpan.FromHours(1);
        Assert.True(gate.Allows(w.Id("B"), "senior.op"));
    }

    [Theory]
    [InlineData("enforce", "enforce", 0)]
    [InlineData("observe", "observe", 0)]
    [InlineData("off", "off", 0)]
    [InlineData("always", "off", 1)]
    public void The_enforce_mode_is_read_only_when_named_and_anything_else_is_off(string value, string mode, int problems)
    {
        var (settings, said) = SecuritySettings.Read(JsonDocument.Parse($"{{\"roster\":\"{value}\"}}").RootElement);
        Assert.Equal(mode, settings.Roster);
        Assert.Equal(problems, said.Count);
        Assert.Equal("off", SecuritySettings.Read(null).Settings.Roster);
    }
}
