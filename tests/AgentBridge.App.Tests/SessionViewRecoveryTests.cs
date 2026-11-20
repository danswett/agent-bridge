using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.App.Tests
{
    public sealed class SessionViewRecoveryTests
    {
        [Theory]
        [InlineData("turn-before-crash", "turn-before-crash", false, 472)]
        [InlineData("turn-before-crash", "replacement", false, 420)]
        [InlineData("turn-before-crash", null, false, 420)]
        [InlineData(null, null, false, 420)]
        [InlineData("turn-before-crash", "replacement", true, 2000)]
        public void A_live_anchor_only_restores_to_its_proven_reply(string? before, string? after, bool atEnd, double expected) => new SessionView().A_live_anchor_only_restores_to_its_proven_reply(before, after, atEnd, expected);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_unavailable_block_stays_pending_until_it_can_be_restored(bool unrealized) => new SessionView().An_unavailable_block_stays_pending_until_it_can_be_restored(unrealized);

        [Theory]
        [InlineData(true, false, false, 0)]
        [InlineData(false, true, false, 1)]
        [InlineData(false, true, false, 4)]
        [InlineData(false, false, true, 4)]
        public void Changed_or_truncated_live_context_cancels_geometry_instead_of_waiting_or_using_shifted_indices(bool advanced, bool truncated, bool wasTruncated, int count) => new SessionView().Changed_or_truncated_live_context_cancels_geometry_instead_of_waiting_or_using_shifted_indices(advanced, truncated, wasTruncated, count);

        [Fact]
        public void A_block_restore_through_scroll_into_view_is_not_taken_as_the_persons_navigation() => new SessionView().A_block_restore_through_scroll_into_view_is_not_taken_as_the_persons_navigation();
    }
}

// Offline geometry only: the production recovery partial runs on the shared pin harness without WinUI.
namespace AgentBridge.App
{
    public sealed partial class SessionView
    {
        double Landed => Scroller.Targets is [.., var to] ? to : Scroller.VerticalOffset;

        void Recover()
        {
            Recovery = new(null, message => throw new InvalidOperationException(message));
            Recovery.Remember(new("local\nsession", null));
            Card = new("session", null) { Row = new("local\nsession"), Session = Make.Session("session") with { Activity = new() { Turn = "turn-before-crash", ResponseTurn = "turn-before-crash" } } };
            (shownHistory, pinEnd, anchor) = ("local\nsession", false, new(3, -12));
            blocks.AddRange([new(), new(), new(), new()]);
            (Scroller.VerticalOffset, Scroller.ScrollableHeight) = (420, 2000);
        }

        internal void A_live_anchor_only_restores_to_its_proven_reply(string? before, string? after, bool atEnd, double expected)
        {
            Recover();
            Card!.Session = Card.Session with { Activity = Card.Session.Activity with { ResponseTurn = before } };
            pinEnd = atEnd;
            SaveReading();
            Card.Session = Card.Session with { Activity = Card.Session.Activity with { ResponseTurn = after } };
            RecoverReading();
            RestoreReading();
            Assert.Null(restoreReading);
            Assert.Equal(expected, Landed);
        }
        internal void An_unavailable_block_stays_pending_until_it_can_be_restored(bool unrealized)
        {
            Recover();
            SaveReading();
            if (unrealized) { ConversationList.Available = false; } else { blocks.Clear(); }
            Scroller.VerticalOffset = Scroller.ScrollableHeight = 0;
            RecoverReading();
            Assert.True(RestoreReading());
            Assert.NotNull(restoreReading);
            if (unrealized) { ConversationList.Available = true; } else { blocks.AddRange([new(), new(), new(), new()]); }
            Scroller.ScrollableHeight = 2000;
            Assert.True(RestoreReading());
            Assert.Null(restoreReading);
            Assert.Equal(52, Landed);
            Assert.False(pinEnd);
        }

        internal void Changed_or_truncated_live_context_cancels_geometry_instead_of_waiting_or_using_shifted_indices(bool advanced, bool truncated, bool wasTruncated, int count)
        {
            Recover();
            Card!.Session = Card.Session with { Activity = Card.Session.Activity with { ResponseTruncated = wasTruncated } };
            SaveReading();
            Card.Session = Card.Session with { Activity = Card.Session.Activity with { Turn = advanced ? "new-prompt" : Card.LiveTurn, ResponseTruncated = truncated } };
            blocks.Clear();
            blocks.AddRange(Enumerable.Range(0, count).Select(_ => new object()));
            RecoverReading();
            RestoreReading();
            Assert.Null(restoreReading);
            Assert.Null(anchor);
            Assert.True(pinEnd);
            Assert.Equal(420, Landed);
            SaveReading();
            Assert.Null(Recovery!.Current!.Position!.Block);
        }

        internal void A_block_restore_through_scroll_into_view_is_not_taken_as_the_persons_navigation()
        {
            Recover();
            SaveReading();
            RecoverReading();
            ConversationList.Scrolled = () => Scroller.Bring(new Microsoft.UI.Xaml.UIElement { Top = -50, Height = 200 }, 0);
            Assert.True(RestoreReading());
            Assert.Equal(0, pinReleases);
            Assert.Null(navigationPending);
            Assert.Equal(472, Landed);
        }
    }
}