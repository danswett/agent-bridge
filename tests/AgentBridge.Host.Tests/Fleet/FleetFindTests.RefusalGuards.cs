using AgentBridge.Host.Fleet;
using Xunit;

public sealed partial class FleetFindTests
{
    static FleetMachine VerifiedFixtureWithStartClaims(string machine,
        params (string Id, DateTimeOffset Since, string? Start)[] sessions)
    {
        var rows = sessions.Select(s =>
        {
            var row = new Dictionary<string, object>
            {
                ["id"] = s.Id,
                ["node"] = "n-" + s.Id,
                ["name"] = "SG Quality",
                ["state"] = "working",
                ["since"] = s.Since,
            };
            if (s.Start is { } start) { row["startedAt"] = start; }
            return row;
        });
        var frame = Body(new { machine, online = true, record = new { sessions = rows } });
        // Verified is fixture input, not signature or clock-evidence verification.
        return Assert.Single(RelayFleetRoster.Parse([frame])) with { Verified = true };
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(123, false, false)]
    [InlineData(123, true, false)]
    [InlineData(123, false, true)]
    [InlineData(123, true, true)]
    public async Task Same_host_duplicates_remain_ambiguous_for_verified_fixtures_even_above_the_start_gap(
        int gapSeconds, bool reverse, bool mixedHosts)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var later = Started.AddSeconds(gapSeconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        (string Id, DateTimeOffset Since, string? Start)[] sessions =
        [
            ("z-old", Started.AddDays(1), "2026-11-07T12:00:00Z"),
            ("a-new", Started.AddDays(-1), later),
        ];
        var sameHost = VerifiedFixtureWithStartClaims("a", reverse ? sessions.Reverse().ToArray() : sessions);
        var machines = mixedHosts ? new[] { sameHost, M("b", true, "SG Quality", verified: true) } : [sameHost];
        if (reverse) { Array.Reverse(machines); }
        var result = await Find(Op(machines), new { name = "SG Quality" });
        Assert.Equal("ambiguous", result.GetProperty("result").GetString());
        Assert.False(result.TryGetProperty("session", out _));
        Assert.Equal(mixedHosts ? 3 : 2, result.GetProperty("candidates").GetArrayLength());
        Assert.DoesNotContain("startedAt", result.GetRawText());
        Assert.DoesNotContain("SG Quality", result.GetRawText());
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "2026-11-07T12:00:00Z", false)]
    [InlineData("2026-11-07T12:00:00Z", "2026-11-07T12:00:00Z", false)]
    [InlineData("2026-11-07T12:00:00Z", "2026-11-07T12:00:00Z", true)]
    [InlineData("2026-11-07T12:00:00Z", "2026-11-07T12:02:03Z", false)]
    [InlineData("2026-11-07T12:00:00Z", "2026-11-07T12:02:03Z", true)]
    [InlineData("2026-11-07T12:00:00Z", "2026-11-07T12:10:00Z", false)]
    [InlineData("2026-11-07T12:00:00Z", "2026-11-07T12:10:00Z", true)]
    public async Task Missing_equal_or_unproven_start_claims_never_select_a_cross_host_winner(
        string? earlier, string? later, bool reverse)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var first = VerifiedFixtureWithStartClaims("a", ("z-old", Started.AddDays(1), earlier));
        var second = VerifiedFixtureWithStartClaims("b", ("a-new", Started.AddDays(-1), later));
        var result = await Find(Op(reverse ? [second, first] : [first, second]), new { name = "SG Quality" });
        Assert.Equal("ambiguous", result.GetProperty("result").GetString());
        Assert.False(result.TryGetProperty("session", out _));
        var candidates = result.GetProperty("candidates");
        Assert.Equal(2, candidates.GetArrayLength());
        Assert.Equal(new[] { Started.AddDays(-1), Started.AddDays(1) },
            candidates.EnumerateArray().Select(c => c.GetProperty("since").GetDateTimeOffset()).Order());
        Assert.DoesNotContain("startedAt", result.GetRawText());
        Assert.DoesNotContain("SG Quality", result.GetRawText());
    }

    [Theory]
    [InlineData(true, false, false, "incomplete")]
    [InlineData(true, false, true, "incomplete")]
    [InlineData(true, true, false, "incomplete")]
    [InlineData(true, true, true, "incomplete")]
    [InlineData(false, true, false, "unverified")]
    [InlineData(false, true, true, "unverified")]
    public async Task Incomplete_then_unverified_refusals_precede_verified_selection_without_a_routing_winner(
        bool truncated, bool unverified, bool reverse, string expected)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var duplicate = VerifiedFixtureWithStartClaims("a",
            ("z-old", Started, "2026-11-07T12:00:00Z"),
            ("a-new", Started.AddDays(1), "2026-11-07T12:10:00Z"));
        var peer = VerifiedFixtureWithStartClaims("b", ("peer", Started, "2026-11-07T12:20:00Z"))
            with
        { Truncated = truncated, Verified = !unverified };
        var result = await Find(Op(reverse ? [peer, duplicate] : [duplicate, peer]), new { name = "SG Quality" });
        Assert.Equal(expected, result.GetProperty("result").GetString());
        Assert.Equal("account-a", result.GetProperty("fleetScope").GetString());
        Assert.False(result.TryGetProperty("session", out _));
        Assert.False(result.TryGetProperty("candidates", out _));
        Assert.DoesNotContain("\"node\"", result.GetRawText());
        Assert.DoesNotContain("startedAt", result.GetRawText());
        Assert.DoesNotContain("SG Quality", result.GetRawText());
    }
}
