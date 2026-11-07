using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Transport;
using Xunit;

public sealed class HostRosterTests
{
    static RelayRoster Held(World w, params string[] labels) =>
        new("observe", RosterFixture.Generation, [.. labels.Select(l => w.Entry(l).Canonical())]);

    [Fact]
    public void A_genesis_and_a_signed_add_verify_and_the_report_names_the_heads()
    {
        var w = new World();
        w.Genesis();
        w.Add("addB", "G", "B", "G");

        var check = HostRoster.Verify(Held(w, "G", "addB"), RosterFixture.Account, () => w.Now);

        Assert.Equal("verified", check.Outcome);
        Assert.Equal((2, 2, 0, 0), (check.Entries, check.Accepted, check.Refused, check.Unreadable));
        Assert.Single(check.Heads);
        Assert.Contains("enforcing nothing", check.Summary);
    }

    [Fact]
    public void An_entry_signed_by_a_key_that_is_not_the_roster_s_is_not_accepted()
    {
        var w = new World();
        w.Genesis();
        w.Add("addB", "G", "B", "G", signAs: "X");

        var check = HostRoster.Verify(Held(w, "G", "addB"), RosterFixture.Account, () => w.Now);

        Assert.Equal(1, check.Accepted);
        Assert.Equal(1, check.Quarantined + check.Refused);
    }

    [Fact]
    public void Text_that_is_not_an_entry_is_counted_and_does_not_stop_the_rest()
    {
        var w = new World();
        w.Genesis();

        var check = HostRoster.Verify(new("observe", null, ["not an entry", w.Entry("G").Canonical()]), RosterFixture.Account, () => w.Now);

        Assert.Equal((1, 1), (check.Unreadable, check.Accepted));
    }

    [Fact]
    public void An_empty_roster_or_another_account_s_genesis_is_reported_not_verified()
    {
        var w = new World();
        w.Genesis();

        var empty = HostRoster.Verify(new("observe", null, []), RosterFixture.Account, () => w.Now);
        var other = HostRoster.Verify(Held(w, "G"), "someone-else@example", () => w.Now);

        Assert.Contains("no roster yet", empty.Outcome);
        Assert.Contains("no genesis for this account", other.Outcome);
        Assert.Contains("enforcing nothing", other.Summary);
    }

    [Theory]
    [InlineData(null, "off", 0)]
    [InlineData("{ \"roster\": \"off\" }", "off", 0)]
    [InlineData("{ \"roster\": \"enforce\" }", "off", 1)]
    [InlineData("[]", "off", 1)]
    public void Only_off_is_a_value_this_host_acts_on_and_anything_else_is_reported_and_read_as_off(string? json, string roster, int problems)
    {
        JsonElement? section = json is null ? null : JsonDocument.Parse(json).RootElement;

        var (settings, found) = SecuritySettings.Read(section);

        Assert.Equal(roster, settings.Roster);
        Assert.Equal(problems, found.Count);
    }
}
