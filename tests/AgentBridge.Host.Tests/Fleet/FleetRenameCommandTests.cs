using System.Text.Json;
using AgentBridge.Host.Fleet;
using Xunit;

public sealed class FleetRenameCommandTests
{
    [Theory]
    [InlineData("present", "Another live session")]
    [InlineData("absent", "No duplicate was found")]
    [InlineData(null, "unknown")]
    public async Task Cli_discloses_before_calling_and_prints_a_saved_result_only_after_success(string? status, string warning)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var json = JsonSerializer.SerializeToElement(status is null ? new { name = "Role" }
            : (object)new { name = "Role", duplicateStatus = status });
        await FleetRenameCommand.RunAsync(() =>
        {
            Assert.Contains("The relay sees this label.", error.ToString());
            Assert.DoesNotContain("saved", error.ToString());
            Assert.Empty(output.ToString());
            return Task.FromResult(json);
        }, output, error);
        Assert.Equal(json.GetRawText() + Environment.NewLine, output.ToString());
        Assert.Contains("saved", error.ToString());
        Assert.Contains(warning, error.ToString());
    }

    [Fact]
    public async Task Failed_cli_save_does_not_print_a_saved_result()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        await Assert.ThrowsAsync<IOException>(() => FleetRenameCommand.RunAsync(
            () => throw new IOException("write refused"), output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("The relay sees this label.", error.ToString());
        Assert.DoesNotContain("saved", error.ToString());
    }
}
