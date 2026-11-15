using Xunit;

namespace AgentBridge.Rehearsal.Tests;

internal sealed class WindowsConformanceStub(string? omittedCapability = null, string? failingCapability = null)
{
    static readonly Uri Link = new("https://example.test/runs/windows-stub");
    readonly List<string> calls = [];

    public IReadOnlyList<string> Calls => calls;

    public ConformanceResult? Run(ConformanceRun run, ConformanceCapability capability)
    {
        calls.Add(capability.Name);
        if (capability.Name == omittedCapability)
            return null;
        var failed = capability.Name == failingCapability;
        return new(run, capability.Name, failed ? ConformanceVerdict.Fail : ConformanceVerdict.Pass,
            failed ? "stub failure" : "stub passed", Link);
    }
}

public class WindowsConformanceDriverTests
{
    static readonly ConformanceRun Windows = new("Windows", "1.0", 1, new string('b', 40), "windows-run");

    [Fact]
    public void The_driver_records_each_required_inventory_row_from_the_windows_stub()
    {
        var required = ConformanceInventory.ForClient("Windows");
        var stub = new WindowsConformanceStub();

        var results = WindowsConformanceDriver.Run(Windows, required, stub.Run);

        Assert.Equal(required.Select(capability => capability.Name), stub.Calls);
        Assert.Equal(required.Select(capability => capability.Name), results.Select(result => result.Capability));
        Assert.All(results, result =>
        {
            Assert.Equal(Windows, result.Run);
            Assert.Equal(ConformanceVerdict.Pass, result.Verdict);
        });
    }
}
