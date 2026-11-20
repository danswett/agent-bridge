using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Protocol;
using Xunit;

public sealed class AgentLaunchCallerCheckTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-launch-caller-").FullName;
    const string Principal = "agent:machine/parent";
    readonly string request = Guid.NewGuid().ToString("D");

    public void Dispose() => Directory.Delete(folder, true);

    static AgentCredential Caller(string scope = AgentProtocol.ScopeReadReplyLaunch, int depth = 0,
        string launch = "person-launch") =>
        new("credential", "parent", launch, 1, DateTimeOffset.UtcNow, scope, depth, ["ancestor"],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1));

    AgentLaunchCallerVerdict Check(AgentCredential caller, string? workspace = "approved",
        string[]? approved = null) =>
        new AgentLaunchCallerCheck(new AgentAuditLog(folder)).Check(caller, Principal, request,
            workspace, approved ?? ["approved"]);

    void AssertRefused(AgentLaunchCallerVerdict result, string reason)
    {
        Assert.False(result.Allowed);
        Assert.Contains(reason, result.Reason);
        var row = JsonSerializer.Deserialize<JsonElement>(Assert.Single(File.ReadAllLines(
            Assert.Single(Directory.GetFiles(Path.Combine(folder, AgentAuditLog.FolderName),
                "launches-*.jsonl"))).Skip(1)));
        Assert.Equal("local", row.GetProperty("direction").GetString());
        Assert.Equal(Principal, row.GetProperty("principal").GetString());
        Assert.Equal(request, row.GetProperty("request").GetString());
        Assert.Equal("ancestor", Assert.Single(row.GetProperty("chain").EnumerateArray()).GetString());
        Assert.Equal(LaunchProtocol.Launch, row.GetProperty("action").GetString());
        Assert.Equal("refused", row.GetProperty("outcome").GetString());
        Assert.Equal(result.Reason, row.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(AgentProtocol.ScopeNone)]
    [InlineData(AgentProtocol.ScopeRead)]
    [InlineData(AgentProtocol.ScopeReadReply)]
    [InlineData("READ+REPLY+LAUNCH")]
    public void A_caller_without_the_exact_launch_scope_is_refused_and_audited(string scope) =>
        AssertRefused(Check(Caller(scope)), "read+reply+launch");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Approved")]
    [InlineData(@"C:\private\workspace")]
    public void A_workspace_not_on_the_approved_list_is_refused_without_copying_it_to_the_audit(string? workspace)
    {
        AssertRefused(Check(Caller(), workspace), "approved workspace");
        Assert.DoesNotContain("private", File.ReadAllText(Assert.Single(Directory.GetFiles(
            Path.Combine(folder, AgentAuditLog.FolderName), "launches-*.jsonl"))));
    }

    [Fact]
    public void An_empty_approved_list_grants_no_workspace() =>
        AssertRefused(Check(Caller(), approved: []), "approved workspace");

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public void A_launcher_without_person_launch_depth_is_refused_and_audited(int depth) =>
        AssertRefused(Check(Caller(depth: depth)), "agent-started");

    [Fact]
    public void An_adopted_launcher_is_refused_even_with_launch_scope_and_zero_depth() =>
        AssertRefused(Check(Caller(launch: AgentCredentials.AdoptedLaunch)), "adopted");

    [Fact]
    public void A_person_started_scoped_launcher_in_an_approved_workspace_is_allowed()
    {
        var result = Check(Caller());

        Assert.True(result.Allowed, result.Reason);
        Assert.Null(result.Reason);
        Assert.False(Directory.Exists(Path.Combine(folder, AgentAuditLog.FolderName)));
    }

    [Fact]
    public void A_refusal_with_an_unwritable_audit_reports_the_audit_failure()
    {
        File.WriteAllText(Path.Combine(folder, AgentAuditLog.FolderName), "not a directory");

        var result = Check(Caller(AgentProtocol.ScopeRead));

        Assert.False(result.Allowed);
        Assert.Contains("auditUnwritable", result.Reason);
    }
}
