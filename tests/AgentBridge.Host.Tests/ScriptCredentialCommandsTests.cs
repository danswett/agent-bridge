using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class ScriptCredentialCommandsTests : IDisposable
{
    readonly Fixture fixture = new();
    readonly List<string> logs = [];
    readonly AgentCredentials credentials;
    readonly Channel channel = new();

    public ScriptCredentialCommandsTests() => credentials = new(fixture.Root, "machine1", log: logs.Add);
    public void Dispose() => fixture.Dispose();

    Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(CallerKind kind = CallerKind.HostCli, AgentAuditLog? audit = null) =>
        LocalApi.Handler(new SessionService(null!, new MachineInfo("machine1", "Test", "windows", "0.1.0")),
            null!, new FixedCallerGuard(new Caller(kind, "fixture")), log: logs.Add,
            credentials: credentials, audit: audit ?? new AgentAuditLog(fixture.Root, log: logs.Add));

    static JsonElement Name(string name = "pump-wake") => JsonSerializer.SerializeToElement(new { name });
    JsonElement[] Audit() => Directory.GetFiles(Path.Combine(fixture.Root, AgentAuditLog.FolderName), "*.jsonl")
        .SelectMany(File.ReadAllLines).Select(s => JsonSerializer.Deserialize<JsonElement>(s))
        .Where(r => r.TryGetProperty("action", out _)).ToArray();

    [Fact]
    public void Revoking_a_script_name_removes_both_rotations_durably_without_revoking_other_grants()
    {
        var first = credentials.MintScript("pump-wake");
        credentials.MintScript("pump-wake");
        credentials.MintScript("other-script");
        credentials.Mint("session1", "launch1", 100, DateTimeOffset.UtcNow, AgentProtocol.ScopeRead);
        credentials.Accept(channel, first.Credential);
        Assert.NotNull(credentials.Of(channel));
        Assert.True(credentials.RevokeScript("pump-wake"));
        Assert.Null(credentials.Of(channel));
        Assert.False(credentials.IsLive(first.Credential));
        Assert.Equal(2, credentials.Count);
        Assert.Equal(2, new AgentCredentials(fixture.Root, "machine1").Count);
        Assert.False(credentials.RevokeScript("pump-wake"));
    }

    [Fact]
    public void Revoking_a_script_name_reports_invalid_input_and_persistence_failure_without_claiming_success()
    {
        credentials.MintScript("pump-wake");
        var invalid = Assert.Throws<ArgumentException>(() => credentials.RevokeScript("../private-input"));
        Assert.DoesNotContain("private-input", invalid.Message);
        Assert.Equal(1, credentials.Count);
        Directory.CreateDirectory(Path.Combine(fixture.Root, AgentCredentials.FileName + ".tmp"));
        var failed = Assert.Throws<InvalidOperationException>(() => credentials.RevokeScript("pump-wake"));
        Assert.Contains("could not save", failed.Message);
        Assert.NotNull(credentials.Problem);
    }

    [Fact]
    public async Task Add_places_a_protected_secret_once_and_only_returns_safe_metadata()
    {
        var result = JsonSerializer.SerializeToElement(await Do("scripts.add"), ProtocolJson.Options);
        Assert.Equal("script:machine1/pump-wake", result.GetProperty("principal").GetString());
        Assert.Equal(AgentProtocol.ScopeReadReply, result.GetProperty("scope").GetString());
        Assert.InRange(result.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow, TimeSpan.FromDays(29), TimeSpan.FromDays(30));
        var file = result.GetProperty("path").GetString()!;
        var clear = ReadSecret(file);
        try
        {
            var token = Encoding.ASCII.GetString(clear);
            Assert.StartsWith(AgentProtocol.TokenPrefix, token);
            Assert.DoesNotContain(token, result.GetRawText() + string.Join("\n", logs) + string.Join("\n", Audit().Select(r => r.GetRawText())));
            Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(fixture.Root, AgentCredentials.FileName)));
            Assert.Equal(1, new AgentCredentials(fixture.Root, "machine1").Count);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
        var row = Assert.Single(Audit());
        Assert.Equal("scripts.add", row.GetProperty("action").GetString());
        Assert.Equal("person:local", row.GetProperty("principal").GetString());
    }

    [Fact]
    public async Task Revoke_removes_both_rotations_from_the_running_store_and_the_next_use_is_refused()
    {
        var first = credentials.MintScript("pump-wake");
        credentials.MintScript("pump-wake");
        credentials.MintScript("other-script");
        credentials.Mint("session1", "launch1", 100, DateTimeOffset.UtcNow, AgentProtocol.ScopeRead);
        var clear = ReadSecret(first.Path);
        try
        {
            // A live authenticated channel must lose the grant in this same store, not only after reload.
            credentials.Accept(channel, first.Credential);
            Assert.NotNull(credentials.Of(channel));
            await Do("scripts.revoke", on: new Channel());
            Assert.Null(credentials.Of(channel));
            Assert.False(credentials.IsLive(first.Credential));
            Assert.Contains("no live credential", credentials.Check(Encoding.ASCII.GetString(clear), ProcessAncestry.Unknown).Refusal);
            Assert.Equal(2, credentials.Count);
            Assert.Equal(2, new AgentCredentials(fixture.Root, "machine1").Count);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Assert.Equal("scripts.revoke", Assert.Single(Audit()).GetProperty("action").GetString());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Do("scripts.revoke", on: new Channel()));
        Assert.Contains("no script credential", error.Message);
    }

    [Theory]
    [InlineData("scripts.add", CallerKind.Agent)]
    [InlineData("scripts.revoke", CallerKind.Agent)]
    [InlineData("scripts.add", CallerKind.Other)]
    [InlineData("scripts.add", CallerKind.App)]
    [InlineData("scripts.revoke", CallerKind.App)]
    public async Task Only_the_persons_host_command_line_can_change_script_credentials(string op, CallerKind kind)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(kind)(channel, op, Name()));
        Assert.Contains("person", error.Message);
        Assert.Equal(0, credentials.Count);
    }

    [Fact]
    public async Task Management_rechecks_ancestry_instead_of_reusing_a_cached_person_classification()
    {
        var image = Path.Combine(fixture.Root, "host.exe");
        var started = DateTimeOffset.UtcNow;
        var known = new List<KnownProcess>();
        var guard = new CallerGuard(() => known, null, agentNames: [],
            ancestryOf: _ => new ProcessAncestry([new PeerProcess(100, started, image)], true), hostImage: image);
        Assert.Equal(CallerKind.HostCli, guard.Classify(channel).Kind);
        known.Add(new KnownProcess(100, started, "an agent session"));
        var handler = LocalApi.Handler(new SessionService(null!, new MachineInfo("machine1", "Test", "windows", "0.1.0")),
            null!, guard, credentials: credentials, audit: new AgentAuditLog(fixture.Root));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, "scripts.add", Name()));
        Assert.Equal(0, credentials.Count);
    }

    [Theory]
    [InlineData("scripts.add", false)]
    [InlineData("scripts.revoke", false)]
    [InlineData("scripts.add", true)]
    [InlineData("scripts.revoke", true)]
    public async Task A_credential_cannot_mint_or_revoke_even_with_host_cli_classification(string op, bool session)
    {
        var grant = session ? credentials.Check(credentials.Mint("session1", "launch1", 100, DateTimeOffset.UtcNow, AgentProtocol.ScopeRead),
            new ProcessAncestry([new PeerProcess(100, DateTimeOffset.UtcNow, "agent.exe")], true)).Accepted!
            : credentials.MintScript("pump-wake").Credential;
        credentials.Accept(channel, grant);
        var count = credentials.Count;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler()(channel, op, Name()));
        Assert.Contains("refused", error.Message);
        Assert.Equal(count, credentials.Count);
        if (session)
        {
            credentials.RevokeSession("session1", "fixture");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Handler()(channel, op, Name()));
            Assert.Equal(0, credentials.Count);
        }
    }

    [Theory]
    [InlineData("scripts.add")]
    [InlineData("scripts.revoke")]
    public async Task An_unwritable_audit_refuses_the_change(string op)
    {
        credentials.MintScript("pump-wake");
        File.WriteAllText(Path.Combine(fixture.Root, AgentAuditLog.FolderName), "blocked");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(op));
        Assert.Contains("audit", error.Message);
        Assert.Equal(1, credentials.Count);
        Assert.Equal(1, new AgentCredentials(fixture.Root, "machine1").Count);
    }

    [Fact]
    public async Task Invalid_names_and_a_third_rotation_fail_without_echoing_input_or_replacing_the_secret()
    {
        await Do("scripts.add");
        var second = JsonSerializer.SerializeToElement(await Do("scripts.add"), ProtocolJson.Options);
        var path = second.GetProperty("path").GetString()!;
        var before = File.ReadAllBytes(path);
        var third = await Assert.ThrowsAsync<InvalidOperationException>(() => Do("scripts.add"));
        Assert.Contains("two", third.Message);
        const string privateInput = "abat1_private-input-not-a-script-name\n";
        var invalid = await Assert.ThrowsAsync<ArgumentException>(() => Do("scripts.add", privateInput));
        Assert.DoesNotContain(privateInput, invalid.Message + string.Join("\n", logs));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(2, credentials.Count);
    }

    static byte[] ReadSecret(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser) : bytes;
    }

    [Theory]
    [InlineData("../private-input", "default")]
    [InlineData("pump-wake", "..")]
    public async Task Invalid_cli_names_and_profiles_do_not_open_or_create_a_store(string name, string profile)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(2, await Cli.ScriptCredentialCommands.RunAsync(["scripts", "add", name, "--profile", profile], fixture.Root, output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("usage:", error.ToString());
        Assert.DoesNotContain("private-input", error.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    // P3: the app path exists, so the terminal no longer changes script credentials (owner Nov 19 rule: no terminal step).
    [Theory]
    [InlineData("add")]
    [InlineData("revoke")]
    [InlineData("grant-fleet")]
    [InlineData("revoke-fleet")]
    public async Task The_cli_points_at_the_app_and_changes_nothing_without_opening_a_store(string verb)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(4, await Cli.ScriptCredentialCommands.RunAsync(["scripts", verb, "pump-wake"], fixture.Root, output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("Settings", error.ToString());
        Assert.Contains("Slipgates app", error.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    [Theory]
    [InlineData("scripts.add")]
    [InlineData("scripts.revoke")]
    [InlineData("scripts.grant-fleet")]
    [InlineData("scripts.revoke-fleet")]
    [InlineData("scripts.confirm")]
    public async Task The_host_refuses_the_command_line_even_with_a_valid_confirmation_and_changes_nothing(string op)
    {
        credentials.MintScript("pump-wake");
        var confirm = await Confirm(op == "scripts.confirm" ? "scripts.add" : op);
        var data = op == "scripts.confirm" ? JsonSerializer.SerializeToElement(new { op = "scripts.add", name = "pump-wake" }) : Press(confirm);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.HostCli)(channel, op, data));
        Assert.Contains("Slipgates app", error.Message);
        Assert.Equal(1, credentials.Count);
        Assert.All(credentials.ListScripts(), s => Assert.False(s.FleetLaunch));
    }

    [Fact]
    public async Task The_command_line_can_still_read_the_list_which_changes_nothing()
    {
        credentials.MintScript("pump-wake");
        var list = JsonSerializer.SerializeToElement(await Handler(CallerKind.HostCli)(channel, "scripts.list", JsonSerializer.SerializeToElement(new { })), ProtocolJson.Options);
        Assert.Equal("pump-wake", Assert.Single(list.GetProperty("scripts").EnumerateArray()).GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("add")]
    [InlineData("revoke")]
    public async Task The_cli_refuses_an_agent_session_before_loading_even_an_invalid_profile(string verb)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { typeof(AgentCredentials).Assembly.Location, "scripts", verb, "pump-wake", "--profile", ".." })
        { start.ArgumentList.Add(arg); }
        start.Environment["AGENT_BRIDGE_SESSION"] = "fixture-agent-session";
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(4, process.ExitCode);
        Assert.Empty(await output);
        Assert.Contains("person", await error);
    }

    // App press (HA retirement row 1, owner option 1, ruling A): the local app may mint, revoke, rotate and tick the fleet
    // launch grant, but only with the host's confirmation bound to the action, the script name and the current state.
    async Task<string> Confirm(string op, string name = "pump-wake", CallerKind kind = CallerKind.App) =>
        JsonSerializer.SerializeToElement(await Handler(kind)(channel, "scripts.confirm", JsonSerializer.SerializeToElement(new { op, name })),
            ProtocolJson.Options).GetProperty("confirm").GetString()!;
    static JsonElement Press(string confirm, string name = "pump-wake") => JsonSerializer.SerializeToElement(new { name, confirm });
    // One app press, as the app does it: the host's confirmation for the action, then the press.
    async Task<object?> Do(string op, string name = "pump-wake", IProtocolChannel? on = null)
    {
        // Both calls on the connection the press comes from, as the app's one pipe connection does.
        var from = on ?? channel;
        var asked = JsonSerializer.SerializeToElement(await Handler(CallerKind.App)(from, "scripts.confirm",
            JsonSerializer.SerializeToElement(new { op, name })), ProtocolJson.Options);
        return await Handler(CallerKind.App)(from, op, Press(asked.GetProperty("confirm").GetString()!, name));
    }

    [Theory]
    [InlineData("scripts.add")]
    [InlineData("scripts.revoke")]
    [InlineData("scripts.grant-fleet")]
    [InlineData("scripts.revoke-fleet")]
    public async Task An_app_press_without_the_bound_confirmation_is_refused_and_changes_nothing(string op)
    {
        credentials.MintScript("pump-wake");
        var before = credentials.Count;
        foreach (var data in new[] { Name(), Press("0000"), Press("") })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, op, data));
            Assert.Contains("person's press", error.Message);
        }
        Assert.Equal(before, credentials.Count);
        Assert.All(credentials.ListScripts(), s => Assert.False(s.FleetLaunch));
    }

    [Fact]
    public async Task An_app_press_with_the_bound_confirmation_mints_once_and_is_audited_as_the_person()
    {
        var confirm = await Confirm("scripts.add");
        var result = JsonSerializer.SerializeToElement(await Handler(CallerKind.App)(channel, "scripts.add", Press(confirm)), ProtocolJson.Options);
        Assert.Equal("script:machine1/pump-wake", result.GetProperty("principal").GetString());
        Assert.Equal(1, credentials.Count);
        Assert.Equal("scripts.add", Assert.Single(Audit()).GetProperty("action").GetString());
        // The state changed, so the same confirmation cannot be replayed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(confirm)));
        Assert.Equal(1, credentials.Count);
    }

    [Fact]
    public async Task A_confirmation_is_bound_to_its_action_its_script_and_the_current_state()
    {
        var add = await Confirm("scripts.add");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(add, "other-script")));
        credentials.MintScript("pump-wake");
        var revoke = await Confirm("scripts.revoke");
        Assert.NotEqual(add, revoke);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.grant-fleet", Press(revoke)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(revoke)));
        Assert.Equal(1, credentials.Count);
        await Handler(CallerKind.App)(channel, "scripts.revoke", Press(revoke));
        Assert.Equal(0, credentials.Count);
    }

    [Fact]
    public async Task An_app_rotate_is_a_second_mint_and_a_revoke_press_ends_both()
    {
        await Handler(CallerKind.App)(channel, "scripts.add", Press(await Confirm("scripts.add")));
        await Handler(CallerKind.App)(channel, "scripts.add", Press(await Confirm("scripts.add")));
        Assert.Equal(2, Assert.Single(credentials.ListScripts()).Live);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Handler(CallerKind.App)(channel, "scripts.add", Press(await Confirm("scripts.add"))));
        await Handler(CallerKind.App)(channel, "scripts.revoke", Press(await Confirm("scripts.revoke")));
        Assert.Empty(credentials.ListScripts());
    }

    [Fact]
    public async Task The_launch_fleet_tick_and_untick_are_app_presses_bound_to_state()
    {
        credentials.MintScript("pump-wake");
        // The host's own policy still decides: with agents off the tick is refused even with a valid confirmation.
        var early = await Confirm("scripts.grant-fleet");
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.grant-fleet", Press(early)));
        Assert.Contains("agents.enabled", off.Message);
        File.WriteAllText(Path.Combine(fixture.Root, HostSettings.FileName), JsonSerializer.Serialize(new { agents = new { enabled = true } }));
        var tick = await Confirm("scripts.grant-fleet");
        await Handler(CallerKind.App)(channel, "scripts.grant-fleet", Press(tick));
        Assert.True(Assert.Single(credentials.ListScripts()).FleetLaunch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.grant-fleet", Press(tick)));
        await Handler(CallerKind.App)(channel, "scripts.revoke-fleet", Press(await Confirm("scripts.revoke-fleet")));
        Assert.False(Assert.Single(credentials.ListScripts()).FleetLaunch);
    }

    [Theory]
    [InlineData(CallerKind.Agent)]
    [InlineData(CallerKind.Other)]
    public async Task Only_the_app_or_the_host_command_line_can_ask_for_a_confirmation_or_the_list(CallerKind kind)
    {
        credentials.MintScript("pump-wake");
        foreach (var op in new[] { "scripts.confirm", "scripts.list" })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Handler(kind)(channel, op, JsonSerializer.SerializeToElement(new { op = "scripts.add", name = "pump-wake" })));
            Assert.Contains("person", error.Message);
        }
    }

    [Fact]
    public async Task A_credentialed_connection_cannot_press_or_list_even_when_classified_as_the_app()
    {
        var script = credentials.MintScript("pump-wake");
        credentials.Accept(channel, script.Credential);
        foreach (var op in new[] { "scripts.list", "scripts.confirm", "scripts.add" })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Handler(CallerKind.App)(channel, op, JsonSerializer.SerializeToElement(new { op = "scripts.add", name = "pump-wake", confirm = "x" })));
        }
        Assert.Equal(1, credentials.Count);
    }

    [Fact]
    public async Task The_list_says_when_a_script_last_authenticated_since_this_host_started()
    {
        var script = credentials.MintScript("pump-wake");
        var before = JsonSerializer.SerializeToElement(await Handler(CallerKind.App)(channel, "scripts.list", JsonSerializer.SerializeToElement(new { })), ProtocolJson.Options);
        // Null is left out of the wire form, so "never" is a missing field.
        Assert.False(Assert.Single(before.GetProperty("scripts").EnumerateArray()).TryGetProperty("lastUse", out var never) && never.ValueKind != JsonValueKind.Null);
        credentials.Accept(channel, script.Credential);
        var after = JsonSerializer.SerializeToElement(await Handler(CallerKind.App)(new Channel(), "scripts.list", JsonSerializer.SerializeToElement(new { })), ProtocolJson.Options);
        Assert.NotEqual(JsonValueKind.Null, Assert.Single(after.GetProperty("scripts").EnumerateArray()).GetProperty("lastUse").ValueKind);
    }

    [Fact]
    public async Task The_list_gives_the_app_metadata_only_never_a_token_hash_path_or_credential_id()
    {
        var first = credentials.MintScript("pump-wake");
        credentials.MintScript("other-script");
        var raw = JsonSerializer.SerializeToElement(await Handler(CallerKind.App)(channel, "scripts.list", JsonSerializer.SerializeToElement(new { })),
            ProtocolJson.Options);
        var rows = raw.GetProperty("scripts").EnumerateArray().ToArray();
        Assert.Equal(["other-script", "pump-wake"], rows.Select(r => r.GetProperty("name").GetString()!).Order().ToArray());
        Assert.All(rows, r => Assert.Equal(AgentProtocol.ScopeReadReply, r.GetProperty("scope").GetString()));
        Assert.DoesNotContain(first.Path, raw.GetRawText());
        Assert.DoesNotContain(first.Credential.Id, raw.GetRawText());
        Assert.DoesNotContain(AgentProtocol.TokenPrefix, raw.GetRawText());
    }

    sealed class Channel : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
