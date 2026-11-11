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
}
