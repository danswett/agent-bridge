using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Windows;
using Xunit;

public sealed class ReplyWriteEvidenceTests
{
    [Theory]
    [InlineData("writing 0|wrote 0|writing 1 queue", null)]
    [InlineData("writing 0|wrote 0|writing 1 queue|fail partial 0", false)]
    [InlineData("writing 0|wrote 0|writing 1 queue|fail partial 1", true)]
    public void Queue_evidence_never_claims_unknown_submission_is_absent(string protocol, bool? submitted)
    {
        var evidence = new WorkerInjector.ReplyWriteEvidence(new KeyPlan([new("report", SubmitKey.None, 0, 0), new("", SubmitKey.Queue, 0, 0)]));
        foreach (var line in protocol.Split('|')) evidence.Read(line);
        var result = evidence.Apply(new(InjectionStatus.Interrupted, "stopped"));
        Assert.Equal(6, result.TextWritten);
        Assert.Equal(submitted, result.SubmitWritten);
        Assert.Null(result.EnterWritten);
    }

    [Fact]
    public void A_first_Queue_send_is_never_classified_as_unsubmitted_text()
    {
        var evidence = new WorkerInjector.ReplyWriteEvidence(new KeyPlan([new("report", SubmitKey.Queue, 0, 0), new("", true, 0, 0)]));
        evidence.Read("writing 0"); evidence.Read("wrote 0");
        Assert.Null(evidence.Apply(new(InjectionStatus.Interrupted, "stopped")).TextWritten);
    }

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
        Assert.Equal(entered, result.SubmitWritten);
        Assert.Equal(count is { } length ? InjectionResult.Fingerprint(words[..length]) : null, result.TextFingerprint);
    }
}
