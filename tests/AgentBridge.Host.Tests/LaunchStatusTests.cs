using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;
public sealed class LaunchStatusTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ab-launch-status-" + Guid.NewGuid().ToString("N"));
    static readonly string Request = Guid.NewGuid().ToString("D");
    const string Session = "0123456789abcdef";
    static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-21T17:00:00Z");
    readonly StringWriter output = new(), error = new();
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    [Theory]
    [InlineData("started", true, 0)]
    [InlineData("started", false, 7)]
    [InlineData("starting", false, 7)]
    [InlineData("unconfirmed", true, 7)]
    [InlineData("unknown", false, 3)]
    [InlineData("failed", false, 5)]
    [InlineData("refused", false, 5)]
    [InlineData("busy", false, 7)]
    public async Task Status_has_explicit_exit_codes_without_submitting_a_launch(string outcome, bool registered, int expected)
    {
        var row = new LaunchStatus(Request, outcome, outcome == "started" || registered ? Session : null, "allowAll", registered, At);
        var code = await LaunchStatusCommands.RunAsync(["launches", "status", "--request", Request, "--json"], root, output, error,
            (_, op, data) =>
            {
                Assert.Equal("launches.status", op);
                Assert.Equal(Request, JsonSerializer.SerializeToElement(data).GetProperty("request").GetString());
                return Task.FromResult(JsonSerializer.SerializeToElement(row, ProtocolJson.Options));
            });
        Assert.Equal(expected, code);
        Assert.Equal(outcome, JsonDocument.Parse(output.ToString()).RootElement.GetProperty("outcome").GetString());
    }
    [Fact]
    public async Task Malformed_request_never_connects_and_host_down_reports_stage_and_reason()
    {
        Assert.Equal(2, await LaunchStatusCommands.RunAsync(["launches", "status", "--request", "not-a-guid"], root, output, error,
            (_, _, _) => throw new InvalidOperationException("must not connect")));
        Assert.Equal(6, await LaunchStatusCommands.RunAsync(["launches", "status", "--request", Request], root, output, error));
        Assert.Contains("stage=", error.ToString());
        Assert.Contains("reason=", error.ToString());
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"request\":\"wrong\",\"outcome\":\"started\",\"registered\":true}")]
    public async Task Invalid_response_is_not_success(string json)
    {
        Assert.Equal(6, await LaunchStatusCommands.RunAsync(["launches", "status", "--request", Request, "--json"], root, output, error,
            (_, _, _) => Task.FromResult(JsonDocument.Parse(json).RootElement.Clone())));
        Assert.Empty(output.ToString());
    }
    [Fact]
    public async Task List_reads_once_with_since_and_empty_list_is_success()
    {
        Assert.Equal(0, await LaunchStatusCommands.RunAsync(["launches", "list", "--since", "30", "--json"], root, output, error,
            (_, op, data) =>
            {
                Assert.Equal("launches.list", op);
                Assert.Equal(30, JsonSerializer.SerializeToElement(data).GetProperty("since").GetInt32());
                return Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<LaunchStatus>()));
            }));
        Assert.Equal("[]", output.ToString().Trim());
    }
    [Fact]
    public void Corrupt_ledger_is_empty_and_alias_keeps_late_success()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "results.json");
        File.WriteAllText(path, "corrupt");
        var logs = new List<string>();
        var ledger = new LaunchStatusLedger(root, logs.Add);
        Assert.Empty(ledger.Read());
        Assert.Single(logs);
        var source = Guid.NewGuid().ToString("D");
        ledger.Publish(new(Request, "unconfirmed", null, "allowAll", false, At) { SourceRequest = source });
        ledger = new LaunchStatusLedger(root, logs.Add);
        ledger.Publish(new(source, "started", Guid.NewGuid().ToString("D"), "allowAll", false, At.AddMinutes(5)));
        Assert.Equal(Request, Assert.Single(ledger.Read()).Request);
        Assert.Equal("started", Assert.Single(ledger.Read()).Outcome);
    }
    [Fact]
    public void Capacity_preserves_unexpired_outcomes_and_only_evicts_expired_rows()
    {
        var logs = new List<string>();
        var ledger = new LaunchStatusLedger(root, logs.Add);
        var first = Guid.NewGuid().ToString("D");
        ledger.Publish(new(first, "refused", null, null, false, At));
        for (var i = 1; i < LaunchStatusLedger.Capacity; i++)
        { ledger.Publish(new(Guid.NewGuid().ToString("D"), "refused", null, null, false, At.AddSeconds(i))); }
        var next = new LaunchStatus(Guid.NewGuid().ToString("D"), "starting", null, null, false, At.AddSeconds(128)) { Principal = "restore" };
        Assert.False(ledger.Publish(next));
        ledger = new LaunchStatusLedger(root, logs.Add);
        Assert.Contains(ledger.Read(), r => r.Request == first);
        Assert.DoesNotContain(ledger.Read(), r => r.Request == next.Request);
        Assert.Contains(logs, line => line.Contains("capacity"));
        ledger.Publish(next with { Time = At.AddMinutes(15).AddSeconds(1) });
        Assert.DoesNotContain(ledger.Read(), r => r.Request == first);
        Assert.Contains(new LaunchStatusLedger(root, logs.Add).Read(), r => r.Request == next.Request);
    }
    [Fact]
    public void Reused_request_UUID_does_not_inherit_another_principals_outcome_or_session()
    {
        var ledger = new LaunchStatusLedger(root, _ => { });
        ledger.Publish(new(Request, "started", Guid.NewGuid().ToString("D"), "allowAll", false, At) { Principal = "first" });
        ledger.Publish(new(Request, "unconfirmed", null, null, false, At.AddSeconds(1)) { Principal = "second" });
        var second = new LaunchStatusLedger(root, _ => { }).Read().Single(r => r.Principal == "second");
        Assert.Equal("unconfirmed", second.Outcome);
        Assert.Null(second.Session);
        Assert.Null(second.Permissions);
        ledger.Publish(new(Request, "unconfirmed", null, null, false, At.AddMinutes(16)) { Principal = "first" });
        Assert.Null(Assert.Single(ledger.Read()).Session);
    }
    [Fact]
    public void Non_GUID_session_status_is_logged_without_interrupting_launch_completion()
    {
        var logs = new List<string>();
        var ledger = new LaunchStatusLedger(root, logs.Add);
        Assert.False(ledger.Publish(new(Request, "started", "native-session", "allowAll", false, At)));
        Assert.Empty(ledger.Read());
        Assert.Contains(logs, line => line.Contains("invalid"));
    }
}
