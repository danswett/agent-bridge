using System.Collections.Specialized;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckHistoryScrollPreserve(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check)
    {
        await CheckHistoryScrollPreserveCase(window, main, demo, check, 40);
        await CheckHistoryScrollPreserveCase(window, main, demo, check, 1146);
    }

    static async Task CheckHistoryScrollPreserveCase(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> report, int turns)
    {
        var trace = new List<string>();
        Action? detachTrace = null;
        void Check(bool ok, string text) => report(ok, $"{text} [fixture requested turns {turns}]"
            + (ok ? "" : "\nObserved diagnostic sequence:\n" + (trace.Count == 0 ? "Observers not yet attached; cause UNKNOWN." : string.Join("\n", trace))));
        Action<bool, string> check = Check;
        var id = $"demo-history-scroll-preserve-{turns}";
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
            var seeded = 0;
            for (var n = 1; n <= turns; n++) { if (stored.AddLive(StoredTurn(n))) { seeded++; } }
            var supported = Math.Min(turns, HistoryLimits.Kept);
            var drawn = await Until(() => stored.State != HistoryLoad.Loading && scroll.ViewportHeight > 0 && rows.ItemsSourceView?.Count >= supported
                && scroll.ScrollableHeight > 3 * scroll.ViewportHeight, TimeSpan.FromSeconds(10));
            check(drawn && seeded == turns, $"scroll-preserve setup: draws a transcript taller than three viewports after seeding {seeded} of {turns} logical turns");
            if (!drawn || seeded != turns) { return; }

            ItemsSourceView? observedSource = null;
            var sourceEpoch = 0;
            var phase = "setup";
            string? lastLayout = null;
            UIElement? lastAnchor = null;
            long? sourceToken = null;
            var parent = ById<ListView>(view, "ConversationList");
            var identities = new List<object>();
            long? parentToken = null;
            int Identity(object? value)
            {
                if (value is null) { return 0; }
                var index = identities.FindIndex(old => ReferenceEquals(old, value));
                if (index < 0) { index = identities.Count; identities.Add(value); }
                return index + 1;
            }
            static string Element(UIElement? element) => element is FrameworkElement { DataContext: HistoryRow row } ? row.Id : element?.GetType().Name ?? "<none>";
            static string Ids(System.Collections.IList? items) => items is null ? "<unavailable>" :
                string.Join(",", items.Cast<object?>().Select(item => item is HistoryRow row ? row.Id : item?.GetType().Name ?? "<null>"));
            void Record(string reason) => trace.Add($"{trace.Count}: {phase}, actual offset {scroll.VerticalOffset:R}, extent {scroll.ExtentHeight:R}, "
                + $"viewport {scroll.ViewportHeight:R}, inner epoch {sourceEpoch}/ref {Identity(rows.ItemsSourceView)}, outer source ref {Identity(parent?.ItemsSource)}/count {parent?.Items.Count}, Card ref {Identity(view.Card)}/session {view.Card?.Id ?? "<null>"}, "
                + $"inner count {rows.ItemsSourceView?.Count.ToString() ?? "<no source>"}, loaded {rows.IsLoaded}, size {rows.ActualWidth:R}x{rows.ActualHeight:R}, anchor {Element(scroll.CurrentAnchor)}: {reason}");
            void Collection(object? sender, NotifyCollectionChangedEventArgs args) =>
                Record($"collection {args.Action}, old index {args.OldStartingIndex}, new index {args.NewStartingIndex}, old [{Ids(args.OldItems)}], new [{Ids(args.NewItems)}]\nObserved managed stack:\n{Environment.StackTrace}");
            void OuterCollection(object? sender, Windows.Foundation.Collections.IVectorChangedEventArgs args) =>
                Record($"OUTER vector {args.CollectionChange}, index {args.Index}, count {parent?.Items.Count}; old/new blocks not supplied by vector event\nObserved managed stack:\n{Environment.StackTrace}");
            void WatchSource()
            {
                if (ReferenceEquals(observedSource, rows.ItemsSourceView)) { return; }
                if (observedSource is not null) { observedSource.CollectionChanged -= Collection; }
                observedSource = rows.ItemsSourceView;
                sourceEpoch++;
                if (observedSource is not null) { observedSource.CollectionChanged += Collection; }
                Record($"ItemsSourceView {(sourceEpoch == 1 ? "initial attachment" : "reference replacement witnessed")}; null {observedSource is null}");
            }
            void Changing(object? sender, ScrollViewerViewChangingEventArgs args) =>
                Record($"ViewChanging proposed next {args.NextView.VerticalOffset:R}, final {args.FinalView.VerticalOffset:R}, inertial {args.IsInertial}; production ChangeView caller UNKNOWN unless present in observed stack\n{Environment.StackTrace}");
            void ObservedChanged(object? sender, ScrollViewerViewChangedEventArgs args) =>
                Record($"ViewChanged actual, intermediate {args.IsIntermediate}\nObserved managed stack:\n{Environment.StackTrace}");
            void AnchorRequested(object? sender, ScrollViewerAnchorRequestedEventArgs args) =>
                Record($"AnchorRequested proposed {Element(args.AnchorElement)}; not proof of chosen anchor or restore target\nObserved managed stack:\n{Environment.StackTrace}");
            void Clearing(object? sender, ItemsRepeaterElementClearingEventArgs args) =>
                Record($"container clearing, index {rows.GetElementIndex(args.Element)}, element {Element(args.Element)}\nObserved managed stack:\n{Environment.StackTrace}");
            void Layout(object? sender, object args)
            {
                WatchSource();
                var geometry = $"{scroll.VerticalOffset:R}/{scroll.ExtentHeight:R}/{scroll.ViewportHeight:R}/{rows.ActualHeight:R}/{rows.ActualWidth:R}/{Identity(parent?.ItemsSource)}/{Identity(view.Card)}";
                if (geometry == lastLayout && ReferenceEquals(lastAnchor, scroll.CurrentAnchor)) { return; }
                lastLayout = geometry;
                lastAnchor = scroll.CurrentAnchor;
                Record($"layout/render geometry or current anchor changed: {geometry}");
            }
            detachTrace = () =>
            {
                if (sourceToken is { } token) { rows.UnregisterPropertyChangedCallback(ItemsRepeater.ItemsSourceProperty, token); }
                if (parent is not null && parentToken is { } outerToken) { parent.UnregisterPropertyChangedCallback(ListView.ItemsSourceProperty, outerToken); }
                if (parent is not null) { parent.Items.VectorChanged -= OuterCollection; }
                if (observedSource is not null) { observedSource.CollectionChanged -= Collection; }
                scroll.ViewChanging -= Changing;
                scroll.ViewChanged -= ObservedChanged;
                scroll.AnchorRequested -= AnchorRequested;
                scroll.LayoutUpdated -= Layout;
                rows.ElementClearing -= Clearing;
            };
            sourceToken = rows.RegisterPropertyChangedCallback(ItemsRepeater.ItemsSourceProperty, (_, _) =>
            {
                Record($"ItemsSource property changed; source identity checked separately\nObserved managed stack:\n{Environment.StackTrace}");
                WatchSource();
            });
            scroll.ViewChanging += Changing;
            scroll.ViewChanged += ObservedChanged;
            scroll.AnchorRequested += AnchorRequested;
            scroll.LayoutUpdated += Layout;
            rows.ElementClearing += Clearing;
            if (parent is not null)
            {
                parentToken = parent.RegisterPropertyChangedCallback(ListView.ItemsSourceProperty, (_, _) => Record($"OUTER ItemsSource property notification; compare observed reference IDs\n{Environment.StackTrace}"));
                parent.Items.VectorChanged += OuterCollection;
            }
            Record($"Observers attached; earlier setup interval unobserved, cause UNKNOWN. Outer list available {parent is not null}; reference IDs are local ReferenceEquals ordinals, not hashes.");
            WatchSource();
            view.ReleasePin();
            var chosen = Math.Round(scroll.ScrollableHeight / 2);
            Record($"test-owned ChangeView request vertical {chosen:R}, disableAnimation true; request is not an observed offset");
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
            HistoryRow[] ReadRows()
            {
                var items = rows.ItemsSourceView;
                return items is null ? [] : Enumerable.Range(0, items.Count).Select(i => items.GetAt(i)).OfType<HistoryRow>().ToArray();
            }
            static (string? Prompt, string Response) Words(HistoryRow row) =>
                (row.Prompt, string.Join("\n\n", row.Blocks.Select(block => block.Text)));
            var originals = ReadRows();
            var floor = originals.Length;
            var required = originals.Select(row => (row.Id, Words: Words(row))).ToArray();
            bool OnScreen(FrameworkElement element)
            {
                if (!element.IsLoaded || element.ActualHeight <= 0 || element.ActualWidth <= 0
                    || Top(element) >= scroll.ViewportHeight || Top(element) + element.ActualHeight <= 0) { return false; }
                for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
                {
                    if (node is UIElement ui && (ui.Visibility != Visibility.Visible || ui.Opacity <= 0)) { return false; }
                }
                return true;
            }
            int Visible()
            {
                var items = rows.ItemsSourceView;
                var count = 0;
                for (var i = 0; items is not null && i < items.Count; i++)
                {
                    if (items.GetAt(i) is not HistoryRow row || rows.TryGetElement(i) is not FrameworkElement element || !OnScreen(element)) { continue; }
                    if (!required.Any(old => old.Id == row.Id && Words(row) == old.Words)) { continue; }
                    var prompt = Find<TextBlock>(element).Any(text => OnScreen(text) && !string.IsNullOrWhiteSpace(text.Text) && text.Text == row.Prompt);
                    var response = Find<RichTextBlock>(element).Any(text => OnScreen(text) && text.Blocks.OfType<Paragraph>().Any(p =>
                        row.Blocks.Any(block => !string.IsNullOrWhiteSpace(block.Text) && block.Text == string.Concat(p.Inlines.OfType<Run>().Select(run => run.Text)))));
                    if (prompt || response) { count++; }
                }
                return count;
            }
            string Metrics() => $"seeded {seeded}, retained {stored.Turns.Count}, items {rows.ItemsSourceView?.Count ?? 0}, visible {Visible()}, "
                + $"viewport {scroll.ViewportHeight:0.###}, offset {scroll.VerticalOffset:0.###}, extent {scroll.ExtentHeight:0.###}, scrollable {scroll.ScrollableHeight:0.###}";
            var ready = floor >= supported && floor == stored.Turns.Count && (turns != 40 || floor + 3 <= HistoryLimits.Kept)
                && originals.Select(row => row.Id).Distinct().Count() == floor
                && originals.All(row => !string.IsNullOrWhiteSpace(row.Prompt) && row.Blocks.Any(block => !string.IsNullOrWhiteSpace(block.Text))) && Visible() > 0;
            check(ready, $"scroll-preserve setup: captures original stable ids and actual prompt/response text with rendered content; {Metrics()}");
            if (!ready) { return; }
            var frames = 0;
            var minimum = before;
            var drift = 0.0;
            var rowsKept = true;
            var countKept = true;
            var contentKept = true;
            var renderedKept = true;
            var extentKept = true;
            var rangeKept = true;
            var minimumRows = floor;
            var minimumVisible = Visible();
            var minimumExtent = scroll.ExtentHeight;
            void Offset()
            {
                minimum = Math.Min(minimum, scroll.VerticalOffset);
                drift = Math.Max(drift, Math.Abs(scroll.VerticalOffset - before));
                var visible = Visible();
                minimumVisible = Math.Min(minimumVisible, visible);
                minimumExtent = Math.Min(minimumExtent, scroll.ExtentHeight);
                renderedKept &= visible > 0;
                extentKept &= double.IsFinite(scroll.ExtentHeight) && scroll.ExtentHeight > 2 * scroll.ViewportHeight;
                rangeKept &= double.IsFinite(scroll.VerticalOffset) && double.IsFinite(scroll.ScrollableHeight)
                    && scroll.ViewportHeight > 0 && scroll.VerticalOffset >= 0 && scroll.VerticalOffset <= scroll.ScrollableHeight;
            }
            void Changed(object? sender, ScrollViewerViewChangedEventArgs args) => Offset();
            void Frame(object? sender, object args)
            {
                frames++;
                Offset();
                var current = ReadRows();
                var byId = current.ToLookup(row => row.Id);
                minimumRows = Math.Min(minimumRows, current.Length);
                countKept &= current.Length >= floor;
                contentKept &= required.All(old => byId[old.Id].Any(row => Words(row) == old.Words));
                foreach (var kept in retained)
                {
                    var index = RowIndex(kept.Id);
                    rowsKept &= index >= 0 && ReferenceEquals(rows.TryGetElement(index), kept.Element)
                        && kept.Element.IsLoaded
                        && Math.Abs(Top(kept.Element) - kept.Top) <= 1.5;
                }
                Layout(sender, args);
                Record($"render {frames}, minimum offset {minimum:R}, drift {drift:R}, rows {rowsKept}, count {countKept}, content {contentKept}, visible {renderedKept}, extent {extentKept}, range {rangeKept}");
            }
            scroll.ViewChanged += Changed;
            CompositionTarget.Rendering += Frame;
            try
            {
                var settled = await Until(() => frames >= 2, TimeSpan.FromSeconds(5));
                var baseline = settled && drift <= 1.5 && rowsKept && countKept && contentKept && renderedKept && extentKept && rangeKept;
                check(baseline, $"scroll-preserve setup: chosen NONZERO middle and original content stay stable across two frames BEFORE received updates; {Metrics()}");
                if (!baseline) { return; }
                for (var update = 1; update <= 3; update++)
                {
                    var prior = ReadRows();
                    floor = Math.Max(prior.Length, Math.Min(HistoryLimits.Kept, originals.Length + update - 1));
                    // Only the supported long case may evict oldest turns when this new notice arrives.
                    var evicted = stored.Turns.Take(turns > HistoryLimits.Kept ? 1 : 0).Select(item => item.Id).ToHashSet();
                    // Never recapture corrupted original words as a new passing baseline.
                    required = required.Concat(prior.Select(row => (row.Id, Words: Words(row))))
                        .DistinctBy(row => row.Id).Where(row => !evicted.Contains(row.Id)).ToArray();
                    var turn = StoredTurn(turns + update);
                    var response = $"Received message update {update}.";
                    // The notice appends history through the received session update, not a direct UI mutation.
                    fixture = fixture with
                    {
                        UpdatedAt = StoredFrom.AddHours(update),
                        Activity = new Activity { UserMessage = "The live exchange", Response = response, TurnCount = turns + update, Notice = turn },
                    };
                    phase = $"received update {update}";
                    Record($"test-owned received-session request, notice {turn.Id}, test baseline {before:R}, original visible [{string.Join(",", retained.Select(kept => $"{kept.Id}@{kept.Top:R}:index{RowIndex(kept.Id)}"))}]; "
                        + "production saved anchor/offset, layout-call boundaries, restore targets, OnCard invocations, pinReleases and skipped-restore reasons UNKNOWN unless directly exposed by observed stacks");
                    await demo.PutAsync(fixture);
                    var delivered = await Until(() => card.Response == response && RowIndex(turn.Id) >= 0, TimeSpan.FromSeconds(5));
                    check(delivered, $"scroll-preserve driver: receives message update {update} and its appended history turn");
                    if (!delivered) { return; }
                    var firstFrame = frames;
                    var rendered = await Until(() => frames >= firstFrame + 2, TimeSpan.FromSeconds(5));
                    check(rendered, $"scroll-preserve driver: renders frames after message update {update}");
                    if (!rendered) { return; }
                    if (turns == 40)
                    {
                        check(minimum > 1.5 && drift <= 1.5,
                            $"scroll-preserve behavior: update {update} preserves pre-update offset {before:0.###}, never resets to zero (now {scroll.VerticalOffset:0.###}, minimum {minimum:0.###}, maximum drift {drift:0.###} px)");
                        check(rowsKept, $"scroll-preserve behavior: update {update} retains unchanged visible rows and their positions; added history and edited live reply are excluded");
                    }
                    else { check(minimum > 1.5, $"scroll-preserve offset-reset: long-history update {update} never resets to zero (minimum {minimum:0.###} px); oldest eviction may shift absolute offset"); }
                    check(countKept, $"scroll-preserve model-count: update {update} never drops below the pre-update row count in rendered frames (minimum items {minimumRows}); {Metrics()}");
                    check(contentKept, $"scroll-preserve model-content: update {update} preserves every required original id/prompt/response; intended additions, live edits and permitted oldest eviction are separate; {Metrics()}");
                    check(renderedKept, $"scroll-preserve rendered-empty: update {update} never loses actual visible prompt/response content (minimum visible rows {minimumVisible}); {Metrics()}");
                    check(extentKept, $"scroll-preserve extent-collapse: update {update} never collapses toward zero (minimum extent {minimumExtent:0.###} px); {Metrics()}");
                    check(rangeKept, $"scroll-preserve offset-range: update {update} stays inside the current scrollable extent; {Metrics()}");
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
            detachTrace?.Invoke();
            Away(window, main);
            await demo.RemoveSessionAsync(id);
        }
    }
}
