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
        var result = JsonSerializer.SerializeToElement(await Handler()(channel, "scripts.add", Name()), ProtocolJson.Options);
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
            await Handler()(new Channel(), "scripts.revoke", Name());
            Assert.Null(credentials.Of(channel));
            Assert.False(credentials.IsLive(first.Credential));
            Assert.Contains("no live credential", credentials.Check(Encoding.ASCII.GetString(clear), ProcessAncestry.Unknown).Refusal);
            Assert.Equal(2, credentials.Count);
            Assert.Equal(2, new AgentCredentials(fixture.Root, "machine1").Count);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Assert.Equal("scripts.revoke", Assert.Single(Audit()).GetProperty("action").GetString());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler()(new Channel(), "scripts.revoke", Name()));
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
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler()(channel, op, Name()));
        Assert.Contains("audit", error.Message);
        Assert.Equal(1, credentials.Count);
        Assert.Equal(1, new AgentCredentials(fixture.Root, "machine1").Count);
    }

    [Fact]
    public async Task Invalid_names_and_a_third_rotation_fail_without_echoing_input_or_replacing_the_secret()
    {
        var handler = Handler();
        await handler(channel, "scripts.add", Name());
        var second = JsonSerializer.SerializeToElement(await handler(channel, "scripts.add", Name()), ProtocolJson.Options);
        var path = second.GetProperty("path").GetString()!;
        var before = File.ReadAllBytes(path);
        var third = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, "scripts.add", Name()));
        Assert.Contains("two", third.Message);
        const string privateInput = "abat1_private-input-not-a-script-name\n";
        var invalid = await Assert.ThrowsAsync<ArgumentException>(() => handler(channel, "scripts.add", Name(privateInput)));
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

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_cli_changes_the_serving_hosts_store_and_prints_only_safe_results()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var source = new AgentBridge.Host.Core.SessionSource(fixture.Options(), fixture.Processes);
        await source.RefreshAsync();
        var service = new SessionService(source, new MachineInfo("machine1", "Test", "windows", "0.1.0"));
        await using var server = LocalServer.Start(LocalEndpoint.For(fixture.Root), LocalApi.Handler(service, null!,
            new FixedCallerGuard(new Caller(CallerKind.HostCli, "fixture")), credentials: credentials, audit: new AgentAuditLog(fixture.Root)));
        var output = new StringWriter();
        var error = new StringWriter();
        foreach (var verb in new[] { "add", "revoke" })
        { Assert.Equal(0, await Cli.ScriptCredentialCommands.RunAsync(["scripts", verb, "pump-wake", "--profile", Path.GetFileName(fixture.Root)],
            Path.GetDirectoryName(fixture.Root)!, output, error)); }
        Assert.Equal(0, credentials.Count);
        Assert.DoesNotContain(AgentProtocol.TokenPrefix, output.ToString() + error);
        Assert.Empty(error.ToString());
        Assert.Equal(2, Audit().Length);
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
        // A refused attempt uses its confirmation up, so the person's real press asks again.
        await Handler(CallerKind.App)(channel, "scripts.revoke", Press(await Confirm("scripts.revoke")));
        Assert.Equal(0, credentials.Count);
    }

    // P1 finding (#3883 receipt 7099362785): a confirmation is random, single-use, short-lived and tied to the connection it
    // was issued to, so a state that comes back (mint, then revoke) never revalidates an old one.
    [Fact]
    public async Task A_used_confirmation_cannot_be_replayed_when_the_state_returns_to_what_it_was()
    {
        var confirm = await Confirm("scripts.add");
        await Handler(CallerKind.App)(channel, "scripts.add", Press(confirm));
        await Handler(CallerKind.App)(channel, "scripts.revoke", Press(await Confirm("scripts.revoke")));
        Assert.Equal(0, credentials.Count);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(confirm)));
        Assert.Contains("person's press", error.Message);
        Assert.Equal(0, credentials.Count);
    }

    // R9's two proven replays (#3883 2/3): tick, untick, then the old tick re-enables launch:fleet; add, revoke, then the old add mints again.
    [Fact]
    public async Task An_old_tick_confirmation_cannot_re_enable_launch_after_a_tick_and_an_untick()
    {
        credentials.MintScript("pump-wake");
        File.WriteAllText(Path.Combine(fixture.Root, HostSettings.FileName), JsonSerializer.Serialize(new { agents = new { enabled = true } }));
        var tick = await Confirm("scripts.grant-fleet");
        await Handler(CallerKind.App)(channel, "scripts.grant-fleet", Press(tick));
        await Handler(CallerKind.App)(channel, "scripts.revoke-fleet", Press(await Confirm("scripts.revoke-fleet")));
        Assert.False(Assert.Single(credentials.ListScripts()).FleetLaunch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.grant-fleet", Press(tick)));
        Assert.False(Assert.Single(credentials.ListScripts()).FleetLaunch);
    }

    [Fact]
    public async Task An_old_add_confirmation_cannot_mint_again_after_an_add_and_a_revoke()
    {
        var add = await Confirm("scripts.add");
        await Handler(CallerKind.App)(channel, "scripts.add", Press(add));
        await Handler(CallerKind.App)(channel, "scripts.revoke", Press(await Confirm("scripts.revoke")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(add)));
        Assert.Empty(credentials.ListScripts());
    }

    // A double press with the one confirmation: the check, the audit attempt and the change run under the store's lock, so exactly
    // one wins (#3883 2/3, ScriptCredentialCommands.cs 38-44). Two confirmations asked at once may cancel each other, but never
    // make two credentials.
    [Fact]
    public async Task Two_presses_at_once_with_the_same_confirmation_make_exactly_one_credential()
    {
        for (var i = 0; i < 25; i++)
        {
            var name = "pump-wake-" + i;
            var c = await Confirm("scripts.add", name);
            using var start = new ManualResetEventSlim();
            var first = Task.Run(() => { start.Wait(); return Handler(CallerKind.App)(channel, "scripts.add", Press(c, name)); });
            var second = Task.Run(() => { start.Wait(); return Handler(CallerKind.App)(channel, "scripts.add", Press(c, name)); });
            start.Set();
            var results = await Task.WhenAll(Settle(first), Settle(second));
            Assert.Equal(1, results.Count(r => r));
            Assert.Equal(1, credentials.ListScripts().Single(s => s.Name == name).Live);
        }
    }

    [Fact]
    public async Task Two_confirmations_asked_at_once_for_the_same_state_never_make_two_credentials()
    {
        for (var i = 0; i < 25; i++)
        {
            var name = "dispatcher-" + i;
            var a = await Confirm("scripts.add", name);
            var b = await Confirm("scripts.add", name);
            using var start = new ManualResetEventSlim();
            var first = Task.Run(() => { start.Wait(); return Handler(CallerKind.App)(channel, "scripts.add", Press(a, name)); });
            var second = Task.Run(() => { start.Wait(); return Handler(CallerKind.App)(channel, "scripts.add", Press(b, name)); });
            start.Set();
            var results = await Task.WhenAll(Settle(first), Settle(second));
            Assert.True(results.Count(r => r) <= 1);
            Assert.True(credentials.ListScripts().SingleOrDefault(s => s.Name == name)?.Live is null or 1);
        }
    }

    static async Task<bool> Settle(Task<object?> press)
    {
        try { await press; return true; }
        catch (InvalidOperationException) { return false; }
    }

    [Fact]
    public async Task A_press_that_fails_still_uses_up_its_confirmation()
    {
        credentials.MintScript("pump-wake");
        credentials.MintScript("pump-wake");
        var confirm = await Confirm("scripts.add");
        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(confirm)));
        Assert.Contains("two", first.Message);
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(confirm)));
        Assert.Contains("person's press", again.Message);
    }

    [Fact]
    public async Task A_confirmation_expires_after_two_minutes()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var timed = new AgentCredentials(fixture.Root, "machine1", clock, logs.Add);
        var handler = LocalApi.Handler(new SessionService(null!, new MachineInfo("machine1", "Test", "windows", "0.1.0")),
            null!, new FixedCallerGuard(new Caller(CallerKind.App, "fixture")), log: logs.Add, credentials: timed, audit: new AgentAuditLog(fixture.Root, log: logs.Add));
        string Ask(object r) => JsonSerializer.SerializeToElement(r, ProtocolJson.Options).GetProperty("confirm").GetString()!;
        var request = JsonSerializer.SerializeToElement(new { op = "scripts.add", name = "pump-wake" });
        var late = Ask((await handler(channel, "scripts.confirm", request))!);
        clock.Advance(TimeSpan.FromSeconds(121));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, "scripts.add", Press(late)));
        Assert.Equal(0, timed.Count);
        var fresh = Ask((await handler(channel, "scripts.confirm", request))!);
        clock.Advance(TimeSpan.FromSeconds(90));
        await handler(channel, "scripts.add", Press(fresh));
        Assert.Equal(1, timed.Count);
    }

    [Fact]
    public async Task A_confirmation_only_works_on_the_connection_that_asked_for_it()
    {
        var elsewhere = new Channel();
        var confirm = await Confirm("scripts.add");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(elsewhere, "scripts.add", Press(confirm)));
        Assert.Equal(0, credentials.Count);
        // The wrong connection used it up too: a stolen value is spent whoever tries it first.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(confirm)));
        Assert.Equal(0, credentials.Count);
    }

    [Fact]
    public async Task A_new_confirmation_for_a_script_replaces_the_one_before_it()
    {
        var first = await Confirm("scripts.add");
        var second = await Confirm("scripts.add");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(first)));
        // The refused try was an attempt, so it used up what was outstanding for the script: the person asks again.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(second)));
        Assert.Equal(0, credentials.Count);
        await Handler(CallerKind.App)(channel, "scripts.add", Press(await Confirm("scripts.add")));
        Assert.Equal(1, credentials.Count);
    }

    [Fact]
    public async Task A_wrong_try_for_a_script_uses_up_its_outstanding_confirmation()
    {
        var real = await Confirm("scripts.add");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press("guess")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(real)));
        Assert.Equal(0, credentials.Count);
    }

    [Fact]
    public async Task Outstanding_confirmations_are_bounded_across_scripts_and_the_oldest_goes_first()
    {
        var first = await Confirm("scripts.add", "script-0");
        string last = first;
        for (var i = 1; i <= 80; i++) { last = await Confirm("scripts.add", "script-" + i); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(CallerKind.App)(channel, "scripts.add", Press(first, "script-0")));
        await Handler(CallerKind.App)(channel, "scripts.add", Press(last, "script-80"));
        Assert.Single(credentials.ListScripts());
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
