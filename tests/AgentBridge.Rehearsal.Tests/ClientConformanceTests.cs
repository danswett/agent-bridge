using System.Text.Json;
using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class ClientConformanceTests
{
    static readonly ConformanceRun Pwa = new("PWA", "1.0", 1, new string('a', 40), "fixture-run");
    static readonly ConformanceRun Windows = Pwa with { Client = "Windows" };
    static readonly ConformanceCapability Reply = new("reply");

    [Theory]
    [InlineData((int)ConformanceVerdict.Pass, "pass")]
    [InlineData((int)ConformanceVerdict.Fail, "fail")]
    [InlineData((int)ConformanceVerdict.NotSupported, "notSupported")]
    [InlineData((int)ConformanceVerdict.Missing, "missing")]
    public void Each_outcome_keeps_its_reason_run_identity_and_evidence_in_one_json_line(int verdict, string label)
    {
        var result = new ConformanceResult(Pwa, "reply", (ConformanceVerdict)verdict, "recorded reason",
            new Uri("https://example.test/runs/fixture"));
        var rows = ClientConformance.Build([Pwa], [Reply], [result]);
        using var json = JsonDocument.Parse(Assert.Single(ClientConformance.JsonLines(rows)));
        Assert.Equal(label, json.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("recorded reason", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal("fixture-run", json.RootElement.GetProperty("run").GetProperty("runId").GetString());
        Assert.Equal("https://example.test/runs/fixture", json.RootElement.GetProperty("evidence").GetString());
    }

    [Fact]
    public void Every_required_client_and_capability_gets_a_row_even_when_no_driver_reported_it()
    {
        var rows = ClientConformance.Build([Pwa, Windows], [Reply, new("presence")],
            [new(Pwa, "reply", ConformanceVerdict.Pass, "acknowledged")]);
        Assert.Equal(4, rows.Count);
        Assert.Single(rows, r => r.Verdict == ConformanceVerdict.Pass);
        Assert.Equal(3, rows.Count(r => r.Verdict == ConformanceVerdict.Missing));
        Assert.Equal(2, rows.Count(r => r.Run == Windows && r.Verdict == ConformanceVerdict.Missing));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative-run")]
    [InlineData("file:///run.json")]
    public void A_live_pass_without_a_recorded_run_link_is_missing(string? link)
    {
        var result = new ConformanceResult(Pwa, "reply", ConformanceVerdict.Pass, "claimed pass",
            link is null ? null : new Uri(link, UriKind.RelativeOrAbsolute));
        var row = Assert.Single(ClientConformance.Build([Pwa], [Reply with { Live = true }], [result]));
        Assert.Equal(ConformanceVerdict.Missing, row.Verdict);
        Assert.Contains("recorded run", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_live_pass_with_a_recorded_run_link_keeps_its_evidence()
    {
        var result = new ConformanceResult(Pwa, "reply", ConformanceVerdict.Pass, "recorded",
            new Uri("https://example.test/runs/live"));
        Assert.Equal(result, Assert.Single(ClientConformance.Build([Pwa], [Reply with { Live = true }], [result])));
    }

    [Fact]
    public void Duplicate_or_out_of_matrix_results_are_refused_instead_of_overwriting_evidence()
    {
        var pass = new ConformanceResult(Pwa, "reply", ConformanceVerdict.Pass, "acknowledged");
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([Pwa], [Reply], [pass, pass]));
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([Pwa], [Reply],
            [pass, pass with { Verdict = ConformanceVerdict.Fail }]));
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([Pwa], [Reply],
            [pass with { Run = Windows }]));
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([Pwa], [Reply],
            [pass with { Capability = "unknown" }]));
    }

    [Fact]
    public void Empty_or_ambiguous_matrices_and_invalid_results_are_not_successful_empty_reports()
    {
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([], [Reply], []));
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([Pwa], [], []));
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([Pwa, Pwa], [Reply], []));
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([Pwa], [Reply, Reply], []));
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([Pwa with { RunId = "" }], [Reply], []));
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([Pwa], [new("")], []));
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([Pwa], [Reply],
            [new(Pwa, "reply", (ConformanceVerdict)99, "invalid")]));
        Assert.Throws<ArgumentException>(() => ClientConformance.Build([Pwa], [Reply],
            [new(Pwa, "reply", ConformanceVerdict.Pass, "")]));
    }
}
