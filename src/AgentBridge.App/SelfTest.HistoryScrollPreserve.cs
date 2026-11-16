using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckHistoryScrollPreserve(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check)
    {
        const string id = "demo-history-scroll-preserve";
        const int turns = 40;
        var fixture = new Session
        {
            Id = id, Agent = AgentKind.Copilot, Title = "Preserve the transcript reading position",
            Status = SessionStatus.Idle, StatusSince = StoredFrom, UpdatedAt = StoredFrom,
            Activity = new Activity { UserMessage = "The live exchange", Response = "Initial latest reply.", TurnCount = turns },
        };
        await demo.PutAsync(fixture);
        try
        {
            var received = await Until(() => main.AllCards.Any(c => c.Id == id), TimeSpan.FromSeconds(10));
            check(received, "scroll-preserve setup: receives the dedicated demo session");
            if (!received) { return; }
            window.ShowPage("sessions");
            Resize(window, 1000, 700);
            var view = await Choose(window, main, id, "Response");
            if (view is null || view.Card is not { Stored: { } stored } card || ConversationScroller(view) is not { } scroll
                || ById<ItemsRepeater>(view, "History") is not { } rows)
            {
                check(false, "scroll-preserve setup: opens the conversation, history and scroller");
                return;
            }
            for (var n = 1; n <= turns; n++) { stored.AddLive(StoredTurn(n)); }
            var drawn = await Until(() => stored.State != HistoryLoad.Loading && scroll.ViewportHeight > 0 && rows.ItemsSourceView?.Count >= turns
                && scroll.ScrollableHeight > 3 * scroll.ViewportHeight, TimeSpan.FromSeconds(10));
            check(drawn, "scroll-preserve setup: draws a transcript taller than three viewports");
            if (!drawn) { return; }

            view.ReleasePin();
            var chosen = Math.Round(scroll.ScrollableHeight / 2);
            scroll.ChangeView(null, chosen, null, disableAnimation: true);
            var reached = await Until(() => Math.Abs(scroll.VerticalOffset - chosen) <= 1.5, TimeSpan.FromSeconds(5));
            var before = scroll.VerticalOffset;
            check(reached && before > scroll.ViewportHeight && before < scroll.ScrollableHeight - scroll.ViewportHeight,
                $"scroll-preserve setup: reaches the chosen NONZERO middle offset before updates ({before:0.###}, chosen {chosen:0.###} px)");
            if (!reached || before <= scroll.ViewportHeight || before >= scroll.ScrollableHeight - scroll.ViewportHeight) { return; }

            double Top(FrameworkElement element) => element.TransformToVisual(scroll).TransformPoint(default).Y;
            var retained = new List<(string Id, FrameworkElement Element, double Top)>();
            var initialRows = rows.ItemsSourceView;
            for (var i = 0; initialRows is not null && i < initialRows.Count; i++)
            {
                if (initialRows.GetAt(i) is HistoryRow row && rows.TryGetElement(i) is FrameworkElement element
                    && Top(element) + element.ActualHeight > 0 && Top(element) < scroll.ViewportHeight)
                {
                    retained.Add((row.Id, element, Top(element)));
                }
            }
            check(retained.Count > 0, "scroll-preserve setup: captures unchanged visible stored rows before updates");
            if (retained.Count == 0) { return; }

            int RowIndex(string rowId)
            {
                var items = rows.ItemsSourceView;
                for (var i = 0; items is not null && i < items.Count; i++)
                {
                    if (items.GetAt(i) is HistoryRow row && row.Id == rowId) { return i; }
                }
                return -1;
            }
            var frames = 0;
            var minimum = before;
            var drift = 0.0;
            var rowsKept = true;
            void Offset()
            {
                minimum = Math.Min(minimum, scroll.VerticalOffset);
                drift = Math.Max(drift, Math.Abs(scroll.VerticalOffset - before));
            }
            void Changed(object? sender, ScrollViewerViewChangedEventArgs args) => Offset();
            void Frame(object? sender, object args)
            {
                frames++;
                Offset();
                foreach (var kept in retained)
                {
                    var index = RowIndex(kept.Id);
                    rowsKept &= index >= 0 && ReferenceEquals(rows.TryGetElement(index), kept.Element)
                        && kept.Element.IsLoaded
                        && Math.Abs(Top(kept.Element) - kept.Top) <= 1.5;
                }
            }
            scroll.ViewChanged += Changed;
            CompositionTarget.Rendering += Frame;
            try
            {
                for (var update = 1; update <= 3; update++)
                {
                    var turn = StoredTurn(turns + update);
                    var response = $"Received message update {update}.";
                    // The notice appends history through the received session update, not a direct UI mutation.
                    fixture = fixture with
                    {
                        UpdatedAt = StoredFrom.AddHours(update),
                        Activity = new Activity { UserMessage = "The live exchange", Response = response, TurnCount = turns + update, Notice = turn },
                    };
                    await demo.PutAsync(fixture);
                    var delivered = await Until(() => card.Response == response && RowIndex(turn.Id) >= 0, TimeSpan.FromSeconds(5));
                    check(delivered, $"scroll-preserve driver: receives message update {update} and its appended history turn");
                    if (!delivered) { return; }
                    var firstFrame = frames;
                    var rendered = await Until(() => frames >= firstFrame + 2, TimeSpan.FromSeconds(5));
                    check(rendered, $"scroll-preserve driver: renders frames after message update {update}");
                    if (!rendered) { return; }
                    check(minimum > 1.5 && drift <= 1.5,
                        $"scroll-preserve behavior: update {update} preserves pre-update offset {before:0.###}, never resets to zero (now {scroll.VerticalOffset:0.###}, minimum {minimum:0.###}, maximum drift {drift:0.###} px)");
                    check(rowsKept, $"scroll-preserve behavior: update {update} retains unchanged visible rows and their positions; added history and edited live reply are excluded");
                }
            }
            finally
            {
                CompositionTarget.Rendering -= Frame;
                scroll.ViewChanged -= Changed;
            }
        }
        finally
        {
            Away(window, main);
            await demo.RemoveSessionAsync(id);
        }
    }
}
