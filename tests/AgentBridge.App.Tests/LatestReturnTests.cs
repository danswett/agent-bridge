using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class LatestReturnTests
{
    [Fact]
    public void Jump_pins_until_released_and_only_another_jump_rearms_it()
    {
        var hatch = new LatestHatch();
        hatch.Jump();
        Assert.True(hatch.AtEnd(false));
        hatch.Release();
        Assert.False(hatch.AtEnd(true));
        hatch.Jump();
        Assert.True(hatch.AtEnd(false));
    }

    [Theory]
    [InlineData(600, 900, 500, 600)]
    [InlineData(600, 800, null, 600)]
    [InlineData(600, 400, null, 400)]
    public void A_released_hatch_without_a_visible_row_keeps_its_numeric_offset(
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
