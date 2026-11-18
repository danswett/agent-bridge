using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class LatestReturnTests
{
    static string ViewSource(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentBridge.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, "src", "AgentBridge.App", file));
    }

    [Fact]
    public void Question_navigation_releases_the_pin_before_requesting_bring_into_view()
    {
        var source = ViewSource("SessionView.xaml.cs");
        var start = source.IndexOf("void OnQuestionBar(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var handler = source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];
        var release = handler.IndexOf("ReleasePin();", StringComparison.Ordinal);
        var navigate = handler.IndexOf("QuestionCard.StartBringIntoView(", StringComparison.Ordinal);
        Assert.True(release >= 0 && navigate > release, "Question navigation must release the Jump pin before moving the viewport.");
    }

    [Fact]
    public void History_restore_recaptures_the_anchor_after_correcting_the_viewport()
    {
        var source = ViewSource("SessionView.History.cs");
        var start = source.IndexOf("void Restore()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var restore = source[start..source.IndexOf("\n        }", start, StringComparison.Ordinal)];
        var correction = restore.IndexOf("Anchoring(scroll, target);", StringComparison.Ordinal);
        var capture = restore.IndexOf("TakeAnchor();", StringComparison.Ordinal);
        Assert.True(correction >= 0 && capture > correction, "Restore must capture the corrected position, including when no correction was needed.");
        Assert.Equal("}", restore[(correction + "Anchoring(scroll, target);".Length)..capture].Trim());
    }

    [Fact]
    public void Jump_pins_until_released_and_can_be_explicitly_rearmed()
    {
        var hatch = new LatestHatch();
        hatch.Jump();
        Assert.True(hatch.AtEnd(false));
        hatch.Release();
        Assert.False(hatch.Pinned);
        Assert.False(hatch.AtEnd(false));
        hatch.Jump();
        Assert.True(hatch.AtEnd(false));
    }

    [Fact]
    public void Released_hatch_still_follows_when_back_at_bottom()
    {
        var h = new LatestHatch();
        h.Jump();
        h.Release();
        Assert.True(h.AtEnd(true));
    }

    [Fact]
    public void Released_hatch_compensates_prepend()
    {
        var h = new LatestHatch();
        h.Jump();
        h.Release();
        Assert.Equal(700, h.RestoreOffset(600, 900, 800));
    }

    [Theory]
    [InlineData(600d, 1200d, 900d, 900d)]
    [InlineData(600d, 800d, null, 600d)]
    [InlineData(600d, 400d, null, 400d)]
    public void A_released_hatch_keeps_its_offset_or_compensates_a_prepend_without_a_visible_row(
        double offset, double extent, double? previousExtent, double expected)
    {
        var hatch = new LatestHatch();
        hatch.Jump();
        hatch.Release();
        Assert.Equal(expected, hatch.RestoreOffset(offset, extent, previousExtent));
    }

    [Fact]
    public void Before_jump_the_existing_automatic_follow_and_prepend_compensation_are_unchanged()
    {
        var hatch = new LatestHatch();
        hatch.Release();
        Assert.True(hatch.AtEnd(true));
        Assert.False(hatch.AtEnd(false));
        Assert.Equal(900, hatch.RestoreOffset(600, 900, 600));
    }

    [Fact]
    public void The_latest_input_receipt_for_the_exact_view_is_accepted()
    {
        var gate = new LatestReceiptGate(Guid.NewGuid());
        var input = gate.Issue();
        Assert.True(gate.Accepts(input.Arrived(59451.46, 59920)));
    }

    [Fact]
    public void Reaching_the_old_end_cannot_satisfy_a_later_input()
    {
        var gate = new LatestReceiptGate(Guid.NewGuid());
        var oldEnd = gate.Issue().Arrived(59398, 59398);
        var latest = gate.Issue();

        Assert.False(gate.Accepts(oldEnd));
        Assert.True(gate.Accepts(latest.Arrived(59451.46, 59920)));
        Assert.False(gate.Accepts(oldEnd));
    }

    [Fact]
    public void An_intermediate_input_cannot_replace_the_latest_issued_input()
    {
        var gate = new LatestReceiptGate(Guid.NewGuid());
        var first = gate.Issue();
        var intermediate = gate.Issue();
        var latest = gate.Issue();

        Assert.True(first.Sequence < intermediate.Sequence);
        Assert.True(intermediate.Sequence < latest.Sequence);
        Assert.False(gate.Accepts(intermediate.Arrived(59398, 59398)));
        Assert.False(gate.Accepts(first.Arrived(59398, 59398)));
        Assert.True(gate.Accepts(latest.Arrived(62878, 62878)));
    }

    [Fact]
    public void Another_views_receipt_is_rejected_even_with_the_same_sequence_and_offsets()
    {
        var gate = new LatestReceiptGate(Guid.NewGuid());
        var other = new LatestReceiptGate(Guid.NewGuid());
        var input = gate.Issue();
        var foreign = other.Issue();

        Assert.Equal(input.Sequence, foreign.Sequence);
        Assert.False(gate.Accepts(foreign.Arrived(59398, 59398)));
        Assert.True(gate.Accepts(input.Arrived(59398, 59398)));
    }

    [Fact]
    public void Issuing_another_input_invalidates_a_previously_accepted_receipt()
    {
        var gate = new LatestReceiptGate(Guid.NewGuid());
        var arrived = gate.Issue().Arrived(59398, 59398);
        Assert.True(gate.Accepts(arrived));

        gate.Issue();

        Assert.False(gate.Accepts(arrived));
    }

    [Fact]
    public void A_receipt_cannot_be_accepted_before_any_input_was_issued()
    {
        var view = Guid.NewGuid();
        var gate = new LatestReceiptGate(view);
        Assert.False(gate.Accepts(new LatestInputReceipt(view, 0, 59398, 59398)));
        Assert.False(gate.Accepts(new LatestInputReceipt(view, 1, 59398, 59398)));
    }

    [Fact]
    public void An_unissued_future_sequence_cannot_satisfy_the_latest_input()
    {
        var view = Guid.NewGuid();
        var gate = new LatestReceiptGate(view);
        var input = gate.Issue();
        Assert.False(gate.Accepts(new LatestInputReceipt(view, input.Sequence + 1, 59398, 59398)));
    }

    [Fact]
    public void A_delayed_arrival_is_not_restamped_with_the_newest_sequence()
    {
        var gate = new LatestReceiptGate(Guid.NewGuid());
        var input = gate.Issue();
        gate.Issue();
        var receipt = input.Arrived(59451.46, 59920);

        Assert.False(gate.Accepts(receipt));
    }
}
