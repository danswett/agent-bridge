using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class DeliveryEvidenceTests
{
    [Fact]
    public void A_delivery_timeout_names_the_last_phase_and_chunk_progress()
    {
        var time = new FakeTimeProvider();
        var evidence = new AttachmentDeliveryEvidence(300_000, time);
        evidence.AttachmentStarted();
        time.Advance(TimeSpan.FromMilliseconds(5));
        evidence.ChunkReceived(100_000);
        time.Advance(TimeSpan.FromMilliseconds(6));
        evidence.ChunkReceived(200_000);
        evidence.ObserveUi("Sending…", Tone.Progress, null, DeliveryState.Sending);

        var failure = Wait.TimedOut("delivered", TimeSpan.FromMilliseconds(4464), evidence.Describe());

        Assert.Contains("timed out waiting for delivered", failure.Message);
        Assert.Contains("longest gap between looks 4464 ms", failure.Message);
        Assert.Contains("last phase=all attachment bytes received", failure.Message);
        Assert.Contains("chunks=2, bytes=300000/300000", failure.Message);
        Assert.Contains("first chunk=5 ms, last chunk=11 ms", failure.Message);
        Assert.Contains("reply acknowledgement=not observed", failure.Message);
        Assert.Contains("state=Sending, tone=Progress, status=Sending…", failure.Message);
    }

    [Fact]
    public void A_reply_acknowledgement_does_not_claim_that_the_UI_settled()
    {
        var evidence = new AttachmentDeliveryEvidence(300_000);
        evidence.ReplyReceived();
        evidence.ReplyAcknowledged("delivered");
        evidence.ObserveUi("Sending…", Tone.Progress, null, DeliveryState.Done);

        var detail = evidence.Describe();

        Assert.Contains("last phase=reply acknowledged", detail);
        Assert.Contains("outcome=delivered", detail);
        Assert.Contains("terminal UI=not observed", detail);
        Assert.Contains("state=Done, tone=Progress, status=Sending…", detail);
    }

    [Fact]
    public void A_late_transport_observer_does_not_hide_the_terminal_UI_phase()
    {
        var evidence = new AttachmentDeliveryEvidence(300_000);
        evidence.ObserveUi("The machine refused this reply.", Tone.Bad, "refused", DeliveryState.Done);
        evidence.AttachmentStarted();
        evidence.ChunkReceived(300_000);
        evidence.ReplyAcknowledged("refused");

        var failure = Wait.TimedOut("delivered", TimeSpan.Zero, evidence.Describe());

        Assert.Contains("last phase=terminal UI observed", failure.Message);
        Assert.Contains("outcome=refused", failure.Message);
        Assert.Contains("state=Done, tone=Bad, status=The machine refused this reply., error=refused", failure.Message);
    }
}
