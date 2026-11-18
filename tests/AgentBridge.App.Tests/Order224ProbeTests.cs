using AgentBridge.HostLauncher;
using Xunit;

public sealed partial class SlotPreparationTests
{
    [Fact]
    public async Task Order224_invalid_pointer_reaches_the_production_failure_filter()
    {
        LegacyFile();
        File.WriteAllText(Path.Combine(Root, "Update.exe"), "fixture");
        File.WriteAllText(PointerFile, "{}");
        var started = false;
        var error = await Record.ExceptionAsync(async () =>
        {
            await StableEntryMode.RunAsync(Path.Combine(Root, "AgentBridge.exe"), [],
                _ => { started = true; return Task.FromResult(0); });
        });
        Assert.False(started);
        Assert.IsType<InvalidDataException>(error);
        Assert.True(error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException,
            "InvalidDataException bypasses the stable-entry diagnostic and return 1.");
    }
}
