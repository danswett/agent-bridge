using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AgentBridge.Learning.Tests;

public class TrxCaptureTests
{
    [Fact]
    public void A_failed_test_produces_a_deterministic_signature_without_raw_diagnostics()
    {
        var bytes = Fixture.Trx(diagnostics: "private-token and /private/home/path");
        var first = Capture(bytes);
        Assert.Equal("host|test:App.Tests.CaptionTests.Caption_is_painted", Assert.Single(first.Failures).Signature);
        Assert.Equal(1, first.Failed);
        Assert.Equal(0, first.Unparsed);
        Assert.Equal(JsonData.Write(first), JsonData.Write(Capture(bytes)));
        Assert.DoesNotContain("private-token", JsonData.Write(first));
        Assert.DoesNotContain("Display name", JsonData.Write(first));
    }

    [Fact]
    public void Passing_and_skipped_tests_are_not_mistakes()
    {
        var pass = Capture(Fixture.Trx("Passed", summary: "Completed"));
        var skip = Capture(Fixture.Trx("NotExecuted", summary: "Completed"));
        Assert.Equal(1, pass.Passed);
        Assert.Equal(1, skip.Skipped);
        Assert.Empty(pass.Failures);
        Assert.Empty(skip.Failures);
        Assert.False(pass.RunFailed);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Error")]
    [InlineData("Timeout")]
    [InlineData("Aborted")]
    public void Nonpassing_execution_outcomes_are_captured(string outcome)
    {
        var result = Capture(Fixture.Trx(outcome));
        Assert.True(result.RunFailed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Unparsed);
    }

    [Fact]
    public void Changed_test_guids_and_error_details_do_not_change_the_failure_category()
    {
        var first = Capture(Fixture.Trx());
        var text = Encoding.UTF8.GetString(Fixture.Trx(diagnostics: "different message")).Replace("test-1", "test-2");
        var second = Capture(Encoding.UTF8.GetBytes(text));
        Assert.Equal(first.Failures, second.Failures);
        Assert.NotEqual(first.RecordId, second.RecordId);
    }

    [Fact]
    public void Unknown_outcomes_and_missing_definitions_are_visible_as_unparsed()
    {
        Assert.True(Capture(Fixture.Trx("NewOutcome")).Unparsed > 0);
        var missing = Capture(Fixture.Trx(definition: false));
        Assert.Equal(1, missing.Failed);
        Assert.Equal(1, missing.Unparsed);
        Assert.Equal("host|unparsed", Assert.Single(missing.Failures).Signature);
    }

    [Fact]
    public void A_failed_run_with_no_failed_test_and_inconsistent_counters_is_not_green()
    {
        var failedRun = Capture(Fixture.Trx("Passed", summary: "Failed"));
        Assert.True(failedRun.RunFailed);
        Assert.True(failedRun.Unparsed > 0);
        var inconsistent = Encoding.UTF8.GetString(Fixture.Trx("Passed", summary: "Completed")).Replace("total=\"1\"", "total=\"10\"");
        Assert.True(Capture(Encoding.UTF8.GetBytes(inconsistent)).Unparsed > 0);
    }

    [Fact]
    public void Empty_results_are_reported_rather_than_counted_as_a_successful_test_run()
    {
        var result = Capture(Encoding.UTF8.GetBytes("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed"><Counters total="0" failed="0" /></ResultSummary>
            </TestRun>
            """));
        Assert.True(result.Unparsed > 0);
        Assert.Equal(0, result.Results);
    }

    [Fact]
    public void Xml_entities_and_non_trx_documents_are_refused()
    {
        Assert.Throws<XmlException>(() => Capture(Encoding.UTF8.GetBytes("""
            <!DOCTYPE TestRun [<!ENTITY leak SYSTEM "file:///private/data">]>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">&leak;</TestRun>
            """)));
        Assert.Throws<FormatException>(() => Capture(Encoding.UTF8.GetBytes("<not-trx />")));
    }

    [Fact]
    public void Theory_display_values_are_not_used_as_a_signature_fallback()
    {
        var result = Capture(Fixture.Trx(name: "Test(secret: private-token)"));
        Assert.Equal("host|unparsed", Assert.Single(result.Failures).Signature);
        Assert.DoesNotContain("private-token", JsonData.Write(result));
    }

    [Fact]
    public void Unknown_options_cannot_be_silently_ignored()
    {
        Assert.Throws<ArgumentException>(() => Program.Run(["capture", "--approve", "yes"], new StringWriter()));
        Assert.Throws<ArgumentException>(() => Program.Run(["search", "--repo", "."], new StringWriter()));
    }

    [Fact]
    public void Error_outcomes_use_the_error_counter_instead_of_the_failed_counter()
    {
        var text = Encoding.UTF8.GetString(Fixture.Trx("Error"))
            .Replace("error=\"1\"", "failed=\"0\" error=\"1\"");
        var result = Capture(Encoding.UTF8.GetBytes(text));
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Unparsed);
    }

    [Theory]
    [InlineData("Results")]
    [InlineData("ResultSummary")]
    [InlineData("TestDefinitions")]
    [InlineData("Counters")]
    public void Duplicate_singleton_sections_cannot_hide_a_second_report(string section)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(Fixture.Trx("Passed", summary: "Completed")));
        var root = document.Root!;
        var ns = root.Name.Namespace;
        var parent = section == "Counters" ? root.Element(ns + "ResultSummary")! : root;
        var duplicate = new XElement(parent.Element(ns + section)!);
        if (section == "Results")
            duplicate.Descendants(ns + "UnitTestResult").Single().SetAttributeValue("outcome", "Failed");
        parent.Add(duplicate);
        Assert.Throws<FormatException>(() => Capture(Encoding.UTF8.GetBytes(document.ToString())));
    }

    [Theory]
    [InlineData("executed", "0")]
    [InlineData("executed", "2")]
    [InlineData("passedButRunAborted", "1")]
    [InlineData("disconnected", "1")]
    [InlineData("warning", "1")]
    [InlineData("inProgress", "1")]
    [InlineData("futureOutcome", "1")]
    [InlineData("pending", "invalid")]
    public void Contradictory_or_unsupported_counters_cannot_look_like_a_clean_run(string name, string value)
    {
        var result = Capture(WithCounter(Fixture.Trx("Passed", summary: "Completed"), name, value));
        Assert.True(result.Unparsed > 0);
        Assert.Contains(result.Failures, failure => failure.Signature == "host|unparsed");
    }

    [Fact]
    public void Execution_counts_respect_passes_and_tests_that_did_not_run()
    {
        Assert.Equal(0, Capture(WithCounter(Fixture.Trx("Passed", summary: "Completed"), "executed", "1")).Unparsed);
        Assert.Equal(0, Capture(WithCounter(Fixture.Trx("NotExecuted", summary: "Completed"), "executed", "0")).Unparsed);
        Assert.True(Capture(WithCounter(Fixture.Trx("NotExecuted", summary: "Completed"), "executed", "1")).Unparsed > 0);
        Assert.Equal(0, Capture(WithCounter(Fixture.Trx("Passed", summary: "Completed"), "passedButRunAborted", "0")).Unparsed);
    }

    static byte[] WithCounter(byte[] bytes, string name, string value)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(bytes));
        var root = document.Root!;
        var ns = root.Name.Namespace;
        root.Element(ns + "ResultSummary")!.Element(ns + "Counters")!.SetAttributeValue(name, value);
        return Encoding.UTF8.GetBytes(document.ToString());
    }

    static CaptureResult Capture(byte[] bytes) => TrxCapture.Parse(bytes, "host", Fixture.Sha, "run-1", "ci");
}
