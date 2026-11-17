using System.Text.Json;
using AgentBridge.Host.Fleet;
using Xunit;

public sealed class FleetNameEnrollmentNoticeTests
{
    [Fact]
    public void Disclosure_warns_that_the_relay_sees_the_label_and_excludes_private_details()
    {
        using var output = new StringWriter();
        FleetNameEnrollmentNotice.WriteDisclosure("SG Quality Lead", output);
        var text = output.ToString();
        Assert.Contains("The relay sees this label.", text, StringComparison.Ordinal);
        Assert.Contains("public-safe label", text, StringComparison.Ordinal);
        Assert.Contains("account names, email addresses, repository names or paths", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"duplicateStatus":"present"}""", "Another live session uses this label; lookup may refuse or select a newer session.")]
    [InlineData("""{"duplicateStatus":"unknown"}""", "Could not determine whether another live session uses this label")]
    [InlineData("""{}""", "Could not determine whether another live session uses this label")]
    public void Saved_rename_shows_present_or_unknown_as_a_visible_warning(string json, string expected)
    {
        using var result = JsonDocument.Parse(json);
        using var output = new StringWriter();
        FleetNameEnrollmentNotice.WriteWarning("SG Quality Lead", result.RootElement, output);
        Assert.Contains(expected, output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Clearing_a_name_skips_the_disclosure_and_duplicate_warning(string name)
    {
        using var result = JsonDocument.Parse("""{"duplicateStatus":"unknown"}""");
        using var output = new StringWriter();
        FleetNameEnrollmentNotice.WriteDisclosure(name, output);
        FleetNameEnrollmentNotice.WriteWarning(name, result.RootElement, output);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void A_confirmed_absent_result_does_not_show_a_duplicate_warning()
    {
        using var result = JsonDocument.Parse("""{"duplicateStatus":"absent"}""");
        using var output = new StringWriter();
        FleetNameEnrollmentNotice.WriteWarning("SG Quality Lead", result.RootElement, output);
        Assert.Empty(output.ToString());
    }
}
