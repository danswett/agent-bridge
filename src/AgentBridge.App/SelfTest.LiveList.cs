using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

static partial class SelfTest
{
    public static async Task RunLiveListAsync(MainWindow window, MainViewModel main, DemoHost demo, string report, Action<int> exit)
    {
        using var output = new StreamWriter(report) { AutoFlush = true };
        void Write(object value) => output.WriteLine(JsonSerializer.Serialize(value));
        var failures = 0;
        var snapshots = 0;
        try
        {
            if (!await Until(() => window.RootElement.XamlRoot is not null && main.AllCards.Any(c => c.Id == "demo-approval"), TimeSpan.FromSeconds(10)))
            { throw new InvalidOperationException("Synthetic window did not load."); }
            Resize(window, 1557, 1002);
            const string id = "render-live-reconnect";
            var fixture = new Session
            {
                Id = id, Agent = AgentKind.Copilot, Title = "Synthetic live list reconnect",
                Status = SessionStatus.Idle, StatusSince = StoredFrom, UpdatedAt = StoredFrom,
                Activity = new Activity { TurnCount = 21 },
            };
            await demo.PutAsync(fixture);
            if (!await Until(() => main.AllCards.Any(c => c.Id == id), TimeSpan.FromSeconds(5)))
            { throw new InvalidOperationException("Synthetic session did not arrive."); }
            var card = main.AllCards.Single(c => c.Id == id);
            var stored = card.Stored ?? throw new InvalidOperationException("No synthetic history.");
            for (var i = 1; i <= 20; i++) { stored.AddLive(StoredTurn(i)); }
            var connection = main.MachineOf(null)!.Host;
            SessionView? view = null;
            var longText = string.Join("\n\n", Enumerable.Range(0, 40).Select(i =>
                $"Synthetic paragraph {i}: **long response A** with `inline code`, a [link](https://example.com), and {new string('x', 128)}."))
                + "\n\n| Lane | Head | Tree | Parent | Result |\n|---|---|---|---|---|\n"
                + string.Concat(Enumerable.Range(0, 14).Select(i => $"| {i} | {new string('a', 128)} | {new string('b', 64)} | {new string('c', 64)} | ready |\n"))
                + "\n```text\n" + new string('d', 1800) + "\n```\n\nA_END_SENTINEL";
            string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            void Sample(string phase, bool requireVisible = true)
            {
                var list = ById<ListView>(view!, "Conversation") ?? throw new InvalidOperationException("No conversation list.");
                var scroll = ConversationScroller(view!) ?? throw new InvalidOperationException("No conversation scroller.");
                var drawn = Find<MarkdownView>(list);
                var visible = drawn.Count(v =>
                {
                    if (!v.IsLoaded || v.Visibility != Visibility.Visible || v.ActualHeight <= 0 || v.Children.Count == 0) { return false; }
                    var top = v.TransformToVisual(scroll).TransformPoint(default).Y;
                    return top < scroll.ViewportHeight && top + v.ActualHeight > 0;
                });
                var expected = string.Join("\n", MarkdownModel.Parse(card.HasVoice ? card.Response : "").Select(ConversationBlocks.TextOf));
                var bound = string.Join("\n", list.Items.OfType<ConversationBlock>().Select(b => b.Text));
                var matching = Hash(expected) == Hash(bound);
                var latest = list.Items.Count == 0 ? null : list.ContainerFromIndex(list.Items.Count - 1) as FrameworkElement;
                var latestTop = latest?.TransformToVisual(scroll).TransformPoint(default).Y;
                var latestVisible = latest is not null && latestTop < scroll.ViewportHeight && latestTop + latest.ActualHeight > 0;
                if (!matching || requireVisible && visible == 0 || card.HasVoice && view!.LiveListPinned && !latestVisible) { failures++; }
                snapshots++;
                Write(new
                {
                    phase, matching, visible, blocks = list.Items.Count,
                    pinned = view!.LiveListPinned, latestVisible, latestTop,
                    layout = view.LiveListLayout,
                    realised = Enumerable.Range(0, list.Items.Count).Count(i => list.ContainerFromIndex(i) is not null),
                    source = RuntimeHelpers.GetHashCode(list.ItemsSource),
                    historyRows = ById<ItemsRepeater>(view!, "History")?.ItemsSourceView?.Count,
                    held = stored.Turns.Count, state = stored.State.ToString(), hasVoice = card.HasVoice,
                    responseBytes = Encoding.UTF8.GetByteCount(card.Response), expectedHash = Hash(expected), boundHash = Hash(bound),
                    offset = scroll.VerticalOffset, extent = scroll.ScrollableHeight, viewport = scroll.ViewportHeight,
                    scale = view!.XamlRoot.RasterizationScale, connections = connection.ConnectionNumber,
                });
            }

            async Task WheelRun()
            {
                var list = ById<ListView>(view!, "Conversation")!;
                var scroll = ConversationScroller(view!)!;
                window.Activate();
                list.Focus(FocusState.Programmatic);
                AcquireNativeForeground(window.Handle);
                await Task.Delay(250);
                var heard = 0;
                var notch = -1;
                var routed = new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
                {
                    heard++;
                    Write(new { phase = "wheel-routed", notch, delta = e.GetCurrentPoint(scroll).Properties.MouseWheelDelta,
                        source = e.OriginalSource.GetType().Name, offset = scroll.VerticalOffset, extent = scroll.ScrollableHeight });
                });
                var changed = new EventHandler<ScrollViewerViewChangedEventArgs>((_, e) =>
                    Write(new { phase = "wheel-view-changed", notch, intermediate = e.IsIntermediate,
                        offset = scroll.VerticalOffset, extent = scroll.ScrollableHeight }));
                scroll.AddHandler(UIElement.PointerWheelChangedEvent, routed, true);
                scroll.ViewChanged += changed;
                try
                {
                    for (notch = 0; notch < 136; notch++)
                    {
                        var delta = notch < 4 || notch >= 68 ? -120 : 120;
                        var markdown = Find<MarkdownView>(list).Where(v => v.IsLoaded && v.ActualHeight > 0)
                            .Select(v => (View: v, Top: v.TransformToVisual(scroll).TransformPoint(default).Y))
                            .Where(v => v.Top < scroll.ViewportHeight && v.Top + v.View.ActualHeight > 0)
                            .OrderByDescending(v => v.View.ActualHeight).FirstOrDefault();
                        var y = markdown.View is null ? scroll.ViewportHeight / 2
                            : (Math.Max(0, markdown.Top) + Math.Min(scroll.ViewportHeight, markdown.Top + markdown.View.ActualHeight)) / 2;
                        var point = scroll.TransformToVisual(window.RootElement).TransformPoint(new Windows.Foundation.Point(scroll.ViewportWidth / 2, y));
                        var scale = scroll.XamlRoot.RasterizationScale;
                        var mouse = new WheelPoint { X = (int)(point.X * scale), Y = (int)(point.Y * scale) };
                        var (target, inWindow, _) = PostedInputTarget(window);
                        if (!inWindow || GetForegroundWindow() != window.Handle)
                        { throw new InvalidOperationException("Wheel target is not the foreground owned window."); }
                        if (ClientToScreen(window.Handle, ref mouse) == 0) { throw new InvalidOperationException("Wheel coordinate conversion failed."); }
                        Sample($"wheel-before-{notch}");
                        Write(new { phase = "wheel-target", notch, delta, markdownHeight = markdown.View?.ActualHeight });
                        var before = heard;
                        if (!PostMessageW(target, 0x020A, (nint)(delta << 16), (mouse.Y << 16) | (mouse.X & 0xffff)))
                        { throw new InvalidOperationException("Owned native wheel message failed."); }
                        await Task.Delay(120);
                        Sample($"wheel-after-{notch}");
                        if (heard != before + 1) { failures++; Write(new { phase = "wheel-delivery-failed", notch, before, heard }); }
                        if (scroll.VerticalOffset < 0 || scroll.VerticalOffset > scroll.ScrollableHeight + 1) { failures++; }
                    }
                    await Task.Delay(1000);
                    Sample("wheel-settled");
                    Write(new { phase = "wheel-finished", notches = notch, heard });
                }
                finally
                {
                    scroll.RemoveHandler(UIElement.PointerWheelChangedEvent, routed);
                    scroll.ViewChanged -= changed;
                }
            }

            for (var cycle = 0; cycle < 6; cycle++)
            {
                var a = longText + $"\n\nCycle {cycle}.";
                fixture = fixture with { UpdatedAt = DateTimeOffset.UtcNow, Activity = new Activity
                {
                    Turn = $"a-{cycle}", ResponseTurn = $"a-{cycle}", UserMessage = $"Synthetic A {cycle}",
                    UserMessageFrom = UserMessageFrom.Agent, Response = a, TurnCount = 21 + cycle * 2,
                } };
                await demo.PutAsync(fixture);
                if (!await Until(() => card.Response == a, TimeSpan.FromSeconds(5))) { throw new InvalidOperationException("Long A did not arrive intact."); }
                view = await Choose(window, main, id, "Conversation") ?? throw new InvalidOperationException("Synthetic view not selected.");
                await Task.Delay(500);
                Sample($"A-{cycle}");
                var number = connection.ConnectionNumber;
                connection.Reconnect();
                if (!await Until(() => connection.ConnectionNumber > number && connection.State == LinkState.Connected, TimeSpan.FromSeconds(10)))
                { throw new InvalidOperationException("Synthetic reconnect did not complete."); }
                await Task.Delay(500);
                Sample($"reconnected-{cycle}");
                fixture = fixture with { UpdatedAt = DateTimeOffset.UtcNow, Activity = new Activity
                {
                    Turn = $"b-{cycle}", ResponseTurn = $"a-{cycle}", UserMessage = $"Synthetic B {cycle}",
                    UserMessageFrom = UserMessageFrom.Agent, Response = a, TurnCount = 22 + cycle * 2,
                } };
                await demo.PutAsync(fixture);
                if (!await Until(() => card.Session.Activity.Turn == $"b-{cycle}" && !card.HasVoice, TimeSpan.FromSeconds(5)))
                { throw new InvalidOperationException("Queued B did not move A into history."); }
                await Task.Delay(500);
                Sample($"queued-B-{cycle}");
                var b = $"New synthetic B {cycle}.\n\nA separate reply with **different blocks**.\n\nB_END_SENTINEL";
                fixture = fixture with { UpdatedAt = DateTimeOffset.UtcNow, Activity = new Activity
                {
                    Turn = $"b-{cycle}", ResponseTurn = $"b-{cycle}", UserMessage = $"Synthetic B {cycle}",
                    UserMessageFrom = UserMessageFrom.Agent, Response = b, TurnCount = 22 + cycle * 2,
                } };
                await demo.PutAsync(fixture);
                if (!await Until(() => card.Response == b, TimeSpan.FromSeconds(5))) { throw new InvalidOperationException("New B did not arrive intact."); }
                await Task.Delay(500);
                Sample($"B-{cycle}");
                var retained = stored.Turns.Single(t => t.Id == $"a-{cycle}").Parts.Last(p => p.Kind == HistoryPartKind.Response).Text
                    ?? throw new InvalidOperationException("Retained A has no response.");
                var intact = retained == a;
                if (!intact) { failures++; }
                Write(new { phase = $"retained-A-{cycle}", intact, bytes = Encoding.UTF8.GetByteCount(retained), hash = Hash(retained) });
                if (await Choose(window, main, "demo-choice", "Conversation") is null) { throw new InvalidOperationException("Could not switch away."); }
                await Task.Delay(200);
                view = await Choose(window, main, id, "Conversation") ?? throw new InvalidOperationException("Could not switch back.");
                await Task.Delay(500);
                Sample($"back-{cycle}");
                if (cycle == 0) { await WheelRun(); }
            }
        }
        catch (Exception e) { failures++; Write(new { phase = "exception", type = e.GetType().FullName, hresult = e.HResult, detail = e.Message }); }
        Write(new { phase = "finished", snapshots, failures });
        exit(failures == 0 ? 0 : 1);
    }
}

public sealed partial class SessionView
{
    internal bool LiveListPinned => pinEnd;
    internal object LiveListLayout => new
    {
        historyHeight = HistoryColumn.ActualHeight,
        historyDesired = HistoryColumn.DesiredSize.Height,
        historyRealised = Enumerable.Range(0, historyShown.Count).Count(i => HistoryRows.TryGetElement(i) is not null),
        firstVisible = (ConversationList.ItemsPanelRoot as ItemsStackPanel)?.FirstVisibleIndex,
        lastVisible = (ConversationList.ItemsPanelRoot as ItemsStackPanel)?.LastVisibleIndex,
        firstCache = (ConversationList.ItemsPanelRoot as ItemsStackPanel)?.FirstCacheIndex,
        lastCache = (ConversationList.ItemsPanelRoot as ItemsStackPanel)?.LastCacheIndex,
    };
}
