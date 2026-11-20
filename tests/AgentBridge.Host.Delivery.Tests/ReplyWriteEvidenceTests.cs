using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Windows;
using Xunit;

public sealed class ReplyWriteEvidenceTests
{
    [Theory]
    [InlineData("writing 0|fail partial 8", 4, false)]
    [InlineData("writing 0|fail partial 9", 5, false)]
    [InlineData("writing 0", null, false)]
    [InlineData("writing 0|wrote 0|fail screen 0", 12, false)]
    [InlineData("writing 0|wrote 0|writing 1 enter", 12, null)]
    [InlineData("writing 0|wrote 0|writing 1 enter|fail partial 0", 12, false)]
    [InlineData("writing 0|wrote 0|writing 1 enter|fail partial 1", 12, true)]
    [InlineData("writing 0|wrote 0|writing 1 enter|wrote 1", 12, true)]
    public void Native_records_prove_an_exact_prefix_and_distinguish_an_unknown_Enter(string protocol, int? count, bool? entered)
    {
        const string words = "agent report";
        var evidence = new WorkerInjector.ReplyWriteEvidence(new KeyPlan([new(words, false, 0, 0), new("", true, 0, 0)]));
        foreach (var line in protocol.Split('|')) evidence.Read(line);
        var result = evidence.Apply(new(InjectionStatus.Interrupted, "worker stopped"));
        Assert.Equal(count, result.TextWritten);
        Assert.Equal(entered, result.EnterWritten);
        Assert.Equal(count is { } length ? InjectionResult.Fingerprint(words[..length]) : null, result.TextFingerprint);
    }
}
