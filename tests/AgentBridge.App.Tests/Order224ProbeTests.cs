using AgentBridge.HostLauncher;
using Xunit;

public sealed partial class SlotPreparationTests
{
    [Theory]
    [InlineData("missing-fields")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("syntax")]
    [InlineData("bad-reference")]
    [InlineData("corrupt-slot")]
    public async Task Order224_invalid_pointer_reaches_the_production_failure_filter(string damage)
    {
        LegacyFile();
        File.WriteAllText(Path.Combine(Root, "Update.exe"), "fixture");
        if (damage is "bad-reference" or "corrupt-slot")
        {
            var target = PointerTarget("retained");
            new AgentBridge.App.Core.SlotPointer(Root).Publish(target);
            if (damage == "bad-reference")
                File.WriteAllText(PointerFile, File.ReadAllText(PointerFile)
                    .Replace(target.Package, "../outside.nupkg", StringComparison.Ordinal));
            else { File.WriteAllText(Path.Combine(Root, target.Slot.RelativePath, "AgentBridge.exe"), "corrupt"); }
        }
        else
        {
            File.WriteAllText(PointerFile, damage switch
            { "empty" => "", "oversized" => new string(' ', 8193), "syntax" => "{", _ => "{}" });
        }
        var started = false;
        var logs = new List<string>();
        var code = await StableEntryMode.RunGuardedAsync(Path.Combine(Root, "AgentBridge.exe"), [], logs.Add,
            _ => { started = true; return Task.FromResult(0); });
        Assert.False(started);
        Assert.Equal(1, code);
        Assert.StartsWith("stable-entry failed: ", Assert.Single(logs));
        Assert.Contains(damage == "syntax" ? "Json" : nameof(InvalidDataException), logs[0]);
    }
}
