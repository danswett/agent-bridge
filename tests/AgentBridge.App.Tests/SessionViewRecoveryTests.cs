using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Xunit;

// Offline geometry only: the production recovery partial is source-linked without WinUI.
namespace AgentBridge.App
{
    public sealed partial class SessionView
    {
        readonly bool attached = true;
        bool pinEnd;
        string shownHistory = "local\nsession";
        ScrollAnchor? anchor = new(3, -12);
        readonly List<object> blocks = [new(), new(), new(), new()];
        readonly List<HistoryRow> historyShown = [];
        readonly HistoryFixture history = new();
        readonly Geometry ConversationScroll = new(), ConversationList = new(), HistoryRows = new(), HistoryColumn = new();
        readonly CardFixture Card = new();
        (string Id, double Top)? ReadingHistory() => null;
        static double Top(FrameworkElement element, Geometry scroll) => element.Y;
        static void Anchoring(Geometry scroll, double offset) => scroll.VerticalOffset = offset;
        static void DrawEarlier() => throw new InvalidOperationException("Unexpected history IO.");

        public SessionView()
        {
            Recovery = new(null, message => throw new InvalidOperationException(message));
            Recovery.Remember(new("local\nsession", null));
        }

        [Theory]
        [InlineData("turn-before-crash", "turn-before-crash", false, 472)]
        [InlineData("turn-before-crash", "replacement", false, 420)]
        [InlineData("turn-before-crash", null, false, 420)]
        [InlineData(null, null, false, 420)]
        [InlineData("turn-before-crash", "replacement", true, 2000)]
        public void A_live_anchor_only_restores_to_its_proven_reply(string? before, string? after, bool atEnd, double expected)
        {
            Card.Session = Card.Session with { Activity = Card.Session.Activity with { ResponseTurn = before } };
            pinEnd = atEnd;
            SaveReading();
            Card.Session = Card.Session with { Activity = Card.Session.Activity with { ResponseTurn = after } };
            RecoverReading();
            RestoreReading();
            Assert.Null(restoreReading);
            Assert.Equal(expected, ConversationScroll.VerticalOffset);
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void An_unavailable_block_stays_pending_until_it_can_be_restored(bool unrealized)
        {
            SaveReading();
            if (unrealized) { ConversationList.Available = false; } else { blocks.Clear(); }
            ConversationScroll.VerticalOffset = ConversationScroll.ScrollableHeight = 0;
            RecoverReading();
            Assert.True(RestoreReading());
            Assert.NotNull(restoreReading);
            if (unrealized) { ConversationList.Available = true; } else { blocks.AddRange([new(), new(), new(), new()]); }
            ConversationScroll.ScrollableHeight = 2000;
            Assert.True(RestoreReading());
            Assert.Null(restoreReading);
            Assert.Equal(52, ConversationScroll.VerticalOffset);
            Assert.False(pinEnd);
        }

        sealed class CardFixture
        {
            public RowFixture Row { get; } = new("local\nsession");
            public Session Session = Make.Session("session") with { Activity = new() { Turn = "turn-before-crash", ResponseTurn = "turn-before-crash" } };
            public string? LiveTurn => Session.Activity.Turn;
        }
        sealed record RowFixture(string Key);
        sealed class HistoryFixture
        {
            public HistoryLoad State => HistoryLoad.StartReached;
            public bool HasOlder => false;
            public object Start { get; } = new();
            public IReadOnlyList<HistoryTurn> Turns => [];
            public Task LoadOlderAsync() => throw new InvalidOperationException("Unexpected history IO.");
        }
        sealed class Geometry : FrameworkElement
        {
            public double VerticalOffset = 420, ScrollableHeight = 2000;
            public double ViewportHeight => 600;
            public bool Available = true;
            public object? ContainerFromIndex(int index) => Available ? new FrameworkElement() : null;
            public object GetOrCreateElement(int index) => throw new InvalidOperationException("Unexpected history realization.");
            public void ScrollIntoView(object item) { }
        }
    }
}
namespace Microsoft.UI.Xaml
{
    public class FrameworkElement
    {
        public bool IsLoaded => true;
        public double Y => 40;
        public void UpdateLayout() { }
    }
}
