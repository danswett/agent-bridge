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
        .SelectMany(File.ReadAllLines).Select(s => JsonSerializer.Deserialize<JsonElement>(s)).ToArray();

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

    [Theory]
    [InlineData("scripts.add")]
    [InlineData("scripts.revoke")]
    public async Task A_script_credential_cannot_mint_or_revoke_even_with_host_cli_classification(string op)
    {
        credentials.Accept(channel, credentials.MintScript("pump-wake").Credential);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Handler()(channel, op, Name()));
        Assert.Contains("person", error.Message);
        Assert.Equal(1, credentials.Count);
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
