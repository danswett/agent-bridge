using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using Xunit;

// The Settings page's script credentials (HA retirement row 1, owner option 1, ruling A): every press asks the host
// for its confirmation first and sends it back, so the host, not the page, decides what the person pressed.
[Trait("Category", "Network")]
public sealed class ScriptCredentialsModelTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    HostConnection connection = null!;
    readonly List<string> calls = [];
    List<object> rows = [];

    public async Task InitializeAsync()
    {
        host.Capabilities.Add(HostOps.ScriptCredentialsCapability);
        host.On(HostOps.ScriptList, _ => new { scripts = rows });
        host.On(HostOps.ScriptConfirm, d => new { name = d.GetProperty("name").GetString(), op = d.GetProperty("op").GetString(),
            confirm = "c-" + d.GetProperty("op").GetString() + "-" + d.GetProperty("name").GetString() });
        foreach (var op in new[] { HostOps.ScriptAdd, HostOps.ScriptRevoke, HostOps.ScriptGrantFleet, HostOps.ScriptRevokeFleet })
        {
            host.On(op, d =>
            {
                var name = d.GetProperty("name").GetString();
                if (d.GetProperty("confirm").GetString() != "c-" + op + "-" + name) { throw new InvalidOperationException("refused: stale confirmation"); }
                calls.Add(op + ":" + name);
                return new { name };
            });
        }
        host.Start();
        connection = host.Connect();
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
    }

    public async Task DisposeAsync() { await connection.DisposeAsync(); await host.DisposeAsync(); }

    static object Row(string name, int live = 1, bool tick = false, string? lastUse = null) =>
        new { name, scope = "read+reply", expiresAt = DateTimeOffset.UtcNow.AddDays(29), live, fleetLaunch = tick, lastUse };

    [Fact]
    public async Task The_list_shows_each_script_with_its_scope_expiry_use_and_launch_tick()
    {
        rows = [Row("pump-wake", lastUse: "2026-11-23T08:00:00Z"), Row("dispatcher", live: 2, tick: true)];
        var model = new ScriptCredentialsModel(connection);
        await model.RefreshAsync();
        Assert.True(model.Available);
        Assert.Equal(["dispatcher", "pump-wake"], model.Rows.Select(r => r.Name));
        var d = model.Rows[0];
        Assert.True(d.FleetLaunch);
        Assert.Equal(2, d.Live);
        Assert.Equal("read+reply", d.Scope);
        Assert.NotNull(model.Rows[1].LastUse);
        Assert.Null(d.LastUse);
    }

    [Fact]
    public async Task A_host_without_the_capability_gets_no_calls_and_shows_nothing()
    {
        await using var older = new FakeHost();
        older.Start();
        await using var link = older.Connect();
        await Wait.UntilAsync(() => link.State == LinkState.Connected, "connected");
        var model = new ScriptCredentialsModel(link);
        await model.RefreshAsync();
        await model.AddAsync("pump-wake");
        Assert.False(model.Available);
        Assert.Empty(model.Rows);
        Assert.Empty(older.Sent(HostOps.ScriptList));
        Assert.Empty(older.Sent(HostOps.ScriptAdd));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("revoke")]
    [InlineData("tick")]
    [InlineData("untick")]
    public async Task Every_press_asks_the_host_for_its_confirmation_and_sends_it_back(string press)
    {
        rows = [Row("pump-wake")];
        var model = new ScriptCredentialsModel(connection);
        await model.RefreshAsync();
        var (op, run) = press switch
        {
            "add" => (HostOps.ScriptAdd, (Func<Task>)(() => model.AddAsync("pump-wake"))),
            "revoke" => (HostOps.ScriptRevoke, () => model.RevokeAsync("pump-wake")),
            "tick" => (HostOps.ScriptGrantFleet, () => model.SetLaunchAsync("pump-wake", true)),
            _ => (HostOps.ScriptRevokeFleet, () => model.SetLaunchAsync("pump-wake", false)),
        };
        await run();
        Assert.Equal([op + ":pump-wake"], calls);
        Assert.Equal(op, Assert.Single(host.Sent(HostOps.ScriptConfirm)).GetProperty("op").GetString());
        Assert.Null(model.Notice);
        // The list is read again after the press, so the page shows what the host now holds.
        Assert.Equal(2, host.Sent(HostOps.ScriptList).Count);
    }

    [Fact]
    public async Task A_refusal_from_the_host_is_shown_and_changes_nothing()
    {
        host.On(HostOps.ScriptAdd, _ => throw new InvalidOperationException("refused: two script credentials are already live for this name"));
        var model = new ScriptCredentialsModel(connection);
        await model.AddAsync("pump-wake");
        Assert.Contains("two script credentials", model.Notice);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("has space")]
    public async Task A_bad_name_is_refused_before_anything_is_sent(string name)
    {
        var model = new ScriptCredentialsModel(connection);
        await model.AddAsync(name);
        Assert.NotNull(model.Notice);
        Assert.Empty(host.Sent(HostOps.ScriptConfirm));
    }

    [Fact]
    public void The_consent_text_says_what_the_press_does_and_never_offers_a_token()
    {
        Assert.Contains("rotat", ScriptCredentialsModel.Consent(HostOps.ScriptAdd, "pump-wake", live: 1), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("create", ScriptCredentialsModel.Consent(HostOps.ScriptAdd, "pump-wake", live: 0), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stops working", ScriptCredentialsModel.Consent(HostOps.ScriptRevoke, "pump-wake", live: 1));
        Assert.Contains("launch", ScriptCredentialsModel.Consent(HostOps.ScriptGrantFleet, "pump-wake", live: 1));
        Assert.All(new[] { HostOps.ScriptAdd, HostOps.ScriptRevoke, HostOps.ScriptGrantFleet, HostOps.ScriptRevokeFleet },
            op => Assert.DoesNotContain("token", ScriptCredentialsModel.Consent(op, "pump-wake", live: 1), StringComparison.OrdinalIgnoreCase));
    }
}
