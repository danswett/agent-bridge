namespace AgentBridge.Quality.Tests;

public class RingCommandTests
{
    const string Exclusions = "Category!=Process&Category!=Network&Category!=HomeAssistantIntegration";

    [Theory]
    [InlineData("dotnet test tests/A.Tests -c Release", "dotnet test tests/A.Tests -c Release --filter \"" + Exclusions + "\"")]
    [InlineData("dotnet test tests/HA.Tests -c Release --filter \"Category!=HomeAssistantIntegration\"",
        "dotnet test tests/HA.Tests -c Release --filter \"Category!=HomeAssistantIntegration&Category!=Process&Category!=Network\"")]
    [InlineData("dotnet test x --filter \"A|B\" --nologo", "dotnet test x --filter \"(A|B)&" + Exclusions + "\" --nologo")]
    [InlineData("cd pwa && node --test \"test/unit/*.test.mjs\"", "cd pwa && node --test \"test/unit/*.test.mjs\"")]
    public void Ring_zero_adds_the_in_process_filter_to_dotnet_test_merged_with_the_commands_own(string command, string expected) =>
        Assert.Equal(expected, RingCommand.For(command, 0));

    [Fact]
    public void Later_rings_run_the_catalogs_command_as_written()
    {
        const string command = "dotnet test tests/A.Tests -c Release";

        Assert.Equal(command, RingCommand.For(command, 1));
    }
}
