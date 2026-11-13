using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Host.Tests;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

[SupportedOSPlatform("windows")]
public sealed class HostSignCommandsTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("host-sign-command-").FullName;
    string Profile => Path.Combine(root, "default");
    public void Dispose() => Directory.Delete(root, true);
    int Run(TextWriter output, TextWriter error, CallerKind kind = CallerKind.HostCli, bool credential = false) =>
        HostSignCommands.Run(["host-sign", "init"], root, () => new(kind, "fixture"), credential, output, error);

    [WindowsFact("hostSign initialization uses protected Windows custody")]
    public void Explicit_creation_is_idempotent_without_rewriting_existing_custody()
    {
        var first = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, Run(first, error));
        using var created = JsonDocument.Parse(first.ToString());
        Assert.Equal("created", created.RootElement.GetProperty("state").GetString());
        Assert.Equal(HostSignCustody.Read(Profile).KeyId, created.RootElement.GetProperty("keyId").GetString());
        Assert.Equal(new[] { "keyId", "state" }, created.RootElement.EnumerateObject().Select(p => p.Name).Order());
        var files = Directory.GetFiles(Profile, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (Bytes: File.ReadAllBytes(p), Time: File.GetLastWriteTimeUtc(p)));
        var second = new StringWriter();
        Assert.Equal(0, Run(second, error));
        using var existing = JsonDocument.Parse(second.ToString());
        Assert.Equal("existing", existing.RootElement.GetProperty("state").GetString());
        Assert.Equal(created.RootElement.GetProperty("keyId").GetString(), existing.RootElement.GetProperty("keyId").GetString());
        Assert.Equal(files.Keys.Order(), Directory.GetFiles(Profile, "*", SearchOption.AllDirectories).Order());
        Assert.All(files, f => { Assert.Equal(f.Value.Bytes, File.ReadAllBytes(f.Key)); Assert.Equal(f.Value.Time, File.GetLastWriteTimeUtc(f.Key)); });
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData(CallerKind.Agent, false)]
    [InlineData(CallerKind.Other, false)]
    [InlineData(CallerKind.App, false)]
    [InlineData(CallerKind.HostCli, true)]
    public void Nonperson_and_credentialed_callers_are_refused_before_any_profile_access(CallerKind kind, bool credential)
    {
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(4, Run(output, error, kind, credential));
        Assert.Equal("", output.ToString());
        Assert.Equal("{\"state\":\"refused\"}" + Environment.NewLine, error.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Theory]
    [InlineData("rotate", "default", "invalid-command")]
    [InlineData("init", "private-sentinel:invalid", "invalid-profile")]
    public void Invalid_commands_and_profiles_emit_only_a_fixed_state_without_writes(string verb, string profile, string state)
    {
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(2, HostSignCommands.Run(["host-sign", verb, "--profile", profile], root,
            () => new(CallerKind.HostCli, "fixture"), false, output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal($"{{\"state\":\"{state}\"}}" + Environment.NewLine, error.ToString());
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [HostSignKeyTests.WindowsHostSignTheory]
    [InlineData("signing-key.dpapi", false)]
    [InlineData("signing-key.json", true)]
    [InlineData("host-sign.public", true)]
    public void Damaged_or_torn_custody_reports_unsigned_without_repair(string file, bool remove)
    {
        using (HostSignKey.LoadOrCreate(Profile, KeyStorage.Dpapi)) { }
        var path = Path.Combine(Profile, "host-sign", file);
        if (remove) { File.Delete(path); } else { File.WriteAllText(path, "private-provider-sentinel"); }
        var before = Directory.GetFiles(Profile, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(1, Run(output, error));
        Assert.Equal("{\"state\":\"missing-key\"}" + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(Profile, "*", SearchOption.AllDirectories).Order());
        Assert.All(before, f => Assert.Equal(f.Value, File.ReadAllBytes(f.Key)));
        Assert.Equal("missing-key", HostSignCustody.Read(Profile).Result);
        Assert.False(FleetRecordSigning.SignExisting(JsonSerializer.SerializeToElement(new { }), Profile).TryGetProperty("sig", out _));
    }

    [WindowsFact("hostSign CLI refuses before identity access")]
    public async Task Real_command_refuses_an_agent_before_an_invalid_profile_is_opened()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { typeof(AgentBridge.Host.Tests.Program).Assembly.Location, "host-sign-command-child" }) { start.ArgumentList.Add(arg); }
        start.Environment["AGENT_BRIDGE_SESSION"] = "fixture";
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); } }
        Assert.Equal(4, child.ExitCode);
        Assert.Equal("", await output);
        Assert.Equal("{\"state\":\"refused\"}" + Environment.NewLine, await error);
    }
}
