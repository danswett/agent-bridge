using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using Xunit;

public sealed partial class FleetFindTests
{
    [Fact]
    public async Task Fleet_list_includes_unnamed_live_sessions_with_unnamed_marker()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var op = Op(M("a", true, null) with
        {
            Sessions =
            [
                new("n-a", "SG Builder 1", "working", Started),
                new("n-b", null, "idle", Started),
                new("n-c", "", "waiting", Started),
            ],
        });
        var caller = Hello(AgentProtocol.ScopeRead);
        var named = JsonSerializer.SerializeToElement(
            await op.HandleAsync(caller, Body(new { include = "content" })), Web);
        var rows = named.GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(["n-a", "n-b", "n-c"], rows.Select(s => s.GetProperty("node").GetString()));
        Assert.Equal("SG Builder 1", rows[0].GetProperty("name").GetString());
        Assert.False(rows[0].TryGetProperty("unnamed", out _));
        Assert.All(rows.Skip(1), s =>
        {
            Assert.True(s.GetProperty("unnamed").GetBoolean());
            Assert.False(s.TryGetProperty("name", out _));
        });
        var plain = JsonSerializer.SerializeToElement(await op.HandleAsync(caller, Body(new { })), Web);
        Assert.All(plain.GetProperty("sessions").EnumerateArray(), s =>
        {
            Assert.False(s.TryGetProperty("name", out _));
            Assert.False(s.TryGetProperty("unnamed", out _));
        });
    }

    [Fact]
    public async Task Exact_name_resolves_to_one_node()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var op = Op(M("a", true, "SG Builder 1", verified: true),
            M("b", true, "sg Builder 1", verified: true));
        var result = await Find(op, new { name = "  SG Builder 1  " });
        Assert.Equal("found", result.GetProperty("result").GetString());
        Assert.Equal("n-a", result.GetProperty("session").GetProperty("node").GetString());
        Assert.Equal("account-a", result.GetProperty("fleetScope").GetString());
        Assert.DoesNotContain("SG Builder 1", result.GetRawText());
        Assert.Equal("notFound", (await Find(op, new { name = "SG Builder" })).GetProperty("result").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_name_returns_ambiguity_error_with_candidates_never_picks_one(bool reverse)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var first = M("a", true, "SG Builder 1", verified: true);
        var second = M("b", true, "SG Builder 1", verified: true);
        var result = await Find(Op(reverse ? [second, first] : [first, second]), new { name = "SG Builder 1" });
        Assert.Equal("ambiguous", result.GetProperty("result").GetString());
        Assert.False(result.TryGetProperty("session", out _));
        Assert.Equal(["n-a", "n-b"], result.GetProperty("candidates").EnumerateArray()
            .Select(c => c.GetProperty("node").GetString()).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("SG Builder 1", result.GetRawText());
    }

    [Fact]
    public async Task Unsigned_match_is_never_promoted_to_found()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var result = await Find(Op(M("a", true, "SG Builder 1", verified: true),
            M("b", true, "SG Builder 1")), new { name = "SG Builder 1" });
        Assert.Equal("unverified", result.GetProperty("result").GetString());
        Assert.False(result.TryGetProperty("session", out _));
        Assert.False(result.TryGetProperty("candidates", out _));
        Assert.DoesNotContain("\"node\"", result.GetRawText());
    }

    [Fact]
    public async Task Works_with_HA_blocked_across_all_paired_hosts()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var registry = new SessionRegistry(TimeProvider.System);
        for (var index = 0; index < 4; index++)
        {
            var id = (index + 1).ToString("x16");
            var unnamedId = (index + 11).ToString("x16");
            Assert.Null(registry.Put("account-a", id, Body(new
            {
                v = 1, machine = id, epoch = id, seq = 1, at = DateTimeOffset.UtcNow, truncated = false,
                sessions = new[]
                {
                    new { id, node = "agent_bridge_" + id, name = (string?)"SG Builder 1", state = "idle", since = Started },
                    new { id = unnamedId, node = "agent_bridge_" + unnamedId, name = (string?)null, state = "working", since = Started },
                },
            })));
        }
        // No HA connector, session service or host control is available to this handler.
        var op = new FleetList(store, new RegistryRoster(registry, "account-a"), audit: new AgentAuditLog(root));
        var handler = LocalApi.Handler(null!, null!, null!, credentials: store, fleet: op);
        var reply = JsonSerializer.SerializeToElement(await handler(Hello(AgentProtocol.ScopeRead),
            FleetList.Op, Body(new { include = "content" })), Web);
        Assert.Equal(4, reply.GetProperty("machines").GetArrayLength());
        var rows = reply.GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(8, rows.Length);
        Assert.Equal(4, rows.Count(s => s.TryGetProperty("unnamed", out var marker) && marker.GetBoolean()));
        Assert.All(rows.GroupBy(s => s.GetProperty("machine").GetString()), group => Assert.Equal(2, group.Count()));
    }
}
