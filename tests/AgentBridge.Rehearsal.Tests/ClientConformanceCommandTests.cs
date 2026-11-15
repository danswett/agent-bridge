using System.Text.Json;
using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class ClientConformanceCommandTests
{
    static readonly ConformanceRun Pwa = new("PWA", "1.0", 1, new string('a', 40), "recorded-run");
    static readonly ConformanceRun Windows = Pwa with { Client = "Windows" };
    static readonly Uri Link = new("https://example.test/runs/recorded");

    static Parsed Input(Scratch s, string results, string client = "PWA") => Cli.Parse(
        ["conformance", "--client", client, "--runs", s.File("runs.json", JsonSerializer.Serialize(new[] { Pwa, Windows }, Evidence.Json)),
        "--capabilities", s.File("capabilities.json", """[{"name":"reply","live":true}]"""),
        "--results", s.File("results.jsonl", results)]);

    static string Lines(params ConformanceResult[] rows) => string.Join("\n", ClientConformance.JsonLines(rows));

    static (string Client, string Version, int ProtocolVersion, string ImplementationHead, string RunId,
        string Capability, string Verdict, string Reason, string? Evidence)[] Records(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            var run = root.GetProperty("run");
            var evidence = root.GetProperty("evidence");
            return (run.GetProperty("client").GetString()!, run.GetProperty("version").GetString()!,
                run.GetProperty("protocolVersion").GetInt32(), run.GetProperty("implementationHead").GetString()!,
                run.GetProperty("runId").GetString()!, root.GetProperty("capability").GetString()!,
                root.GetProperty("verdict").GetString()!, root.GetProperty("reason").GetString()!,
                evidence.ValueKind == JsonValueKind.Null ? null : evidence.GetString());
        }).ToArray();

    [Theory]
    [InlineData((int)ConformanceVerdict.Pass, "pass", 0)]
    [InlineData((int)ConformanceVerdict.Fail, "fail", 1)]
    [InlineData((int)ConformanceVerdict.NotSupported, "notSupported", 1)]
    [InlineData((int)ConformanceVerdict.Missing, "missing", 1)]
    public void The_named_clients_verdict_and_link_are_printed_and_only_pass_exits_zero(int verdict, string label, int exit)
    {
        using var s = new Scratch();
        using var output = new StringWriter();
        var input = Input(s, Lines(new(Windows, "reply", ConformanceVerdict.Fail, "other client"),
            new(Pwa, "reply", (ConformanceVerdict)verdict, "recorded reason", Link)));
        Assert.Equal(exit, ClientConformanceCommand.Run(input, output));
        using var json = JsonDocument.Parse(Assert.Single(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal("PWA", json.RootElement.GetProperty("run").GetProperty("client").GetString());
        Assert.Equal(label, json.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(Link.AbsoluteUri, json.RootElement.GetProperty("evidence").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_absent_required_result_or_live_link_is_missing_and_exits_nonzero(bool withoutLink)
    {
        using var s = new Scratch();
        using var output = new StringWriter();
        var input = Input(s, withoutLink ? Lines(new ConformanceResult(Pwa, "reply", ConformanceVerdict.Pass, "no link")) : "");
        Assert.Equal(1, ClientConformanceCommand.Run(input, output));
        Assert.Contains("\"verdict\":\"missing\"", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"run":null,"capability":"reply","verdict":"pass","reason":"invalid"}""")]
    public void Malformed_or_incomplete_recorded_results_are_refused_before_any_output(string results)
    {
        using var s = new Scratch();
        using var output = new StringWriter();
        Assert.Throws<JsonException>(() => ClientConformanceCommand.Run(Input(s, results), output));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void Duplicate_results_and_unknown_clients_are_refused_instead_of_printing_success()
    {
        using var s = new Scratch();
        using var output = new StringWriter();
        var pass = new ConformanceResult(Pwa, "reply", ConformanceVerdict.Pass, "recorded", Link);
        Assert.Throws<ArgumentException>(() => ClientConformanceCommand.Run(Input(s, Lines(pass, pass)), output));
        Assert.Throws<UsageException>(() => ClientConformanceCommand.Run(Input(s, "", "unknown"), output));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void Windows_without_a_callback_preserves_the_existing_recorded_result_path()
    {
        using var s = new Scratch();
        using var output = new StringWriter();
        var pass = new ConformanceResult(Windows, "reply", ConformanceVerdict.Pass, "recorded", Link);

        Assert.Equal(0, ClientConformanceCommand.Run(Input(s, Lines(pass), "Windows"), output));

        var record = Assert.Single(Records(output.ToString()));
        Assert.Equal(("Windows", "reply", "pass", Link.AbsoluteUri),
            (record.Client, record.Capability, record.Verdict, record.Evidence));
    }

    [Fact]
    public void Windows_command_records_the_required_inventory_and_exits_zero_only_when_all_stub_rows_pass()
    {
        using var s = new Scratch();
        using var output = new StringWriter();
        var required = ConformanceInventory.ForClient("Windows");
        var stub = new WindowsConformanceStub();

        Assert.Equal(0, ClientConformanceCommand.Run(Input(s, "", "Windows"), output, stub.Run));

        Assert.Equal(required.Select(capability => capability.Name), stub.Calls);
        var records = Records(output.ToString());
        Assert.Equal(required.Select(capability => capability.Name), records.Select(record => record.Capability));
        Assert.All(records, record =>
        {
            Assert.Equal("Windows", record.Client);
            Assert.Equal(Windows.Version, record.Version);
            Assert.Equal(Windows.ProtocolVersion, record.ProtocolVersion);
            Assert.Equal(Windows.ImplementationHead, record.ImplementationHead);
            Assert.Equal(Windows.RunId, record.RunId);
            Assert.Equal("pass", record.Verdict);
            Assert.Equal("stub passed", record.Reason);
            Assert.Equal("https://example.test/runs/windows-stub", record.Evidence);
        });
    }

    [Fact]
    public void Windows_command_exits_nonzero_and_preserves_omitted_and_failing_stub_rows()
    {
        using var s = new Scratch();
        using var output = new StringWriter();
        var required = ConformanceInventory.ForClient("Windows");
        var stub = new WindowsConformanceStub(omittedCapability: "reply", failingCapability: "session-stop");

        Assert.Equal(1, ClientConformanceCommand.Run(Input(s, "", "Windows"), output, stub.Run));

        var records = Records(output.ToString()).ToDictionary(record => record.Capability);
        Assert.Equal(required.Count, records.Count);
        Assert.Equal(("missing", "No result recorded."), (records["reply"].Verdict, records["reply"].Reason));
        Assert.Equal(("fail", "stub failure"), (records["session-stop"].Verdict, records["session-stop"].Reason));
    }
}
