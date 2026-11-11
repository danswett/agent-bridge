using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AuditCommandsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "audit-command-" + Guid.NewGuid().ToString("N"));
    string Profile => Path.Combine(root, "default");
    string Audit => Path.Combine(Profile, AgentAuditLog.FolderName);
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }

    (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = AuditCommands.Run(["audit", .. args], root, output, error);
        return (code, output.ToString(), error.ToString());
    }

    void Append(DateTimeOffset at, string action, string outcome, string request)
    {
        var entry = new AgentAuditEntry("local", request, "agent:test-machine/test-session", [], action, "target", outcome);
        Assert.True(new AgentAuditLog(Profile, new FakeTimeProvider(at)).Append(entry, "private prompt \u00e9"));
    }

    [Fact]
    public void Filters_combine_across_UTC_midnight_and_both_streams_without_changing_files()
    {
        var before = DateTimeOffset.Parse("2026-11-10T23:59:00Z");
        Append(before, "session.launch", "started", "old");
        Append(before.AddMinutes(1), "session.launch", "started", "first");
        Append(before.AddMinutes(2), "session.launch", "refused", "refused");
        Append(before.AddMinutes(3), "agent.send", "attempted", "send");
        var files = Directory.GetFiles(Audit).ToDictionary(f => f, File.ReadAllBytes);

        var result = Run("--since", "2026-11-10T16:00:00-08:00", "--outcome", "started", "--action", "session.launch");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using var row = JsonDocument.Parse(result.Output);
        Assert.Equal("first", row.RootElement.GetProperty("request").GetString());
        Assert.Equal(TimeSpan.FromHours(-8), row.RootElement.GetProperty("at").GetDateTimeOffset().Offset);
        var all = Run();
        Assert.Equal(0, all.Code);
        Assert.Equal(4, all.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("send", all.Output);
        Assert.DoesNotContain("private prompt", all.Output);
        Assert.Equal(files.Keys.Order(), Directory.GetFiles(Audit).Order());
        foreach (var (file, bytes) in files) { Assert.Equal(bytes, File.ReadAllBytes(file)); }
    }

    [Fact]
    public void Known_metadata_only_is_printed_and_v1_daily_headers_are_not_actions()
    {
        Append(DateTimeOffset.Parse("2026-11-11T00:01:00Z"), "session.launch", "started", "one");
        var file = Assert.Single(Directory.GetFiles(Audit, "launches-*.jsonl"));
        var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file))!;
        row["v"] = JsonSerializer.SerializeToElement(1);
        row["prompt"] = JsonSerializer.SerializeToElement("private unknown prompt");
        row["token"] = JsonSerializer.SerializeToElement("private unknown credential");
        row["previousSha256"] = JsonSerializer.SerializeToElement(new string('0', 64));
        File.WriteAllText(file, "{\"v\":1,\"previousSha256\":\"\"}\n" + JsonSerializer.Serialize(row) + "\n");
        File.WriteAllText(Path.Combine(Audit, "not-an-audit.jsonl"), "private unrelated file");
        File.WriteAllText(Path.Combine(Profile, "config.json"), "private broken configuration");

        var result = Run();
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        using var printed = JsonDocument.Parse(result.Output);
        Assert.Equal("agent:test-machine/test-session", printed.RootElement.GetProperty("principal").GetString());
        Assert.Equal("started", printed.RootElement.GetProperty("outcome").GetString());
        Assert.False(printed.RootElement.TryGetProperty("prompt", out _));
        Assert.DoesNotContain("private", result.Output + result.Error);
        Assert.DoesNotContain("intact", result.Output + result.Error);
    }

    [Theory]
    [InlineData("--since", "2026-11-11T00:00:00")]
    [InlineData("--since", "not-a-time")]
    [InlineData("--profile", "..")]
    [InlineData("--outcome")]
    [InlineData("--unknown", "value")]
    [InlineData("archive")]
    [InlineData("--action", "agent.send", "--action", "session.launch")]
    public void Invalid_arguments_are_refused_before_any_profile_is_created(params string[] args)
    {
        var result = Run(args);
        Assert.Equal(2, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.Empty(result.Output);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_missing_profile_is_an_empty_read_and_is_never_created()
    {
        var result = Run("--profile", "absent");
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Output);
        Assert.Empty(result.Error);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("{\"v\":2,\"prompt\":\"private corrupt content\"}\n")]
    [InlineData("{\"v\":1,\"action\":\"session.launch\"}\n")]
    [InlineData("private corrupt content\n")]
    [InlineData("{\"v\":1,\"previousSha256\":42}\n")]
    public void Unsupported_or_malformed_complete_records_fail_visibly_without_echoing_content(string content)
    {
        Directory.CreateDirectory(Audit);
        File.WriteAllText(Path.Combine(Audit, "agents-2026-11-11.jsonl"), content);
        var result = Run("--outcome", "started");
        Assert.Equal(1, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.Empty(result.Output);
        Assert.DoesNotContain("private", result.Error);
        Assert.DoesNotContain(root, result.Error);
    }

    [Fact]
    public void An_incomplete_final_JSON_prefix_is_reported_and_not_counted_as_an_action()
    {
        Append(DateTimeOffset.Parse("2026-11-11T00:01:00Z"), "agent.send", "attempted", "complete");
        File.AppendAllText(Assert.Single(Directory.GetFiles(Audit, "agents-*.jsonl")), "{\"at\":\"unfinished");
        var result = Run();
        Assert.Equal(0, result.Code);
        Assert.Contains("incomplete", result.Error);
        using var row = JsonDocument.Parse(result.Output);
        Assert.Equal("complete", row.RootElement.GetProperty("request").GetString());
    }
}
