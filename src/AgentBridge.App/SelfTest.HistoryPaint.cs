using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

static partial class SelfTest
{
    // Adapted from the retained #1768 observer: subscribe before selection or host changes, never after Choose settles.
    static async Task CheckHistoryFormattedFromFirstFrame(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        await using var host = new HistoryPaintHost();
        await host.AppendAsync(0, agent: false);
        await host.AppendAsync(1);
        await host.AppendAsync(2, agent: false);
        var ui = new QueueDispatcher(DispatcherQueue.GetForCurrentThread());
        await using var connection = new HostConnection(ct => LocalClient.ConnectAsync(host.Endpoint, ct: ct), ui, AppLog.Write);
        const string key = "history-paint-host";
        var cards = new CardContext
        {
            Host = connection, Ui = ui, Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null),
            Capabilities = () => connection.Capabilities,
            FetchDetail = async id => await connection.ReadAsync<Session>(Ops.SessionGet, new { id }),
        };
        main.Add(new Machine(key, connection, cards));
        var view = window.Sessions.SessionPaneView;
        var phase = "host load";
        var frames = 0;
        var failures = new HashSet<string>();
        var seen = new HashSet<string>();
        var mono = ((FontFamily)Application.Current.Resources["MonoFontFamily"]).Source;
        bool Shown(FrameworkElement? element)
        {
            if (element is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 }) { return false; }
            for (DependencyObject? at = element; at is not null; at = VisualTreeHelper.GetParent(at))
            {
                if (at is UIElement { Visibility: Visibility.Collapsed }) { return false; }
            }
            return true;
        }
        bool Drawn(FrameworkElement? element, ScrollViewer viewport)
        {
            if (!Shown(element)) { return false; }
            var bounds = element!.TransformToVisual(viewport).TransformBounds(
                new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return bounds.Bottom > 0 && bounds.Top < viewport.ViewportHeight
                && bounds.Right > 0 && bounds.Left < viewport.ViewportWidth;
        }
        bool Formatted(MarkdownView markdown, int n = -1)
        {
            var runs = markdown.Children.OfType<RichTextBlock>().SelectMany(r => r.Blocks.OfType<Paragraph>())
                .SelectMany(p => p.Inlines.OfType<Run>()).ToList();
            return runs.Any(r => (n < 0 ? r.Text.StartsWith("Formatted reply ", StringComparison.Ordinal) : r.Text == $"Formatted reply {n}")
                    && r.FontWeight.Weight == Microsoft.UI.Text.FontWeights.SemiBold.Weight)
                && runs.Any(r => r.Text == "code" && r.FontFamily?.Source == mono);
        }
        bool AgentPresentation(FrameworkElement element, FrameworkElement? prompt, TextBlock? label,
            DisclosureButton? toggle, FrameworkElement? bubble, string text, ScrollViewer viewport)
        {
            return Shown(prompt) && toggle is { IsExpanded: false }
                && label?.Foreground is SolidColorBrush colour
                && Ui.ThemedBrush("AbAgentText", element.ActualTheme) is SolidColorBrush expected && colour.Color == expected.Color
                && !Drawn(bubble, viewport)
                && !Find<TextBlock>(element).Any(t => t.Text == text && Drawn(t, viewport));
        }
        void Frame(object? sender, object e)
        {
            if (view.Card?.Id != HistoryPaintHost.Id || ConversationScroller(view) is not { ViewportHeight: > 0 } viewport) { return; }
            frames++;
            var wrong = new List<string>();
            var visible = 0;
            if (ById<ItemsRepeater>(view, "History") is { } rows)
            {
                for (var i = 0; i < (rows.ItemsSourceView?.Count ?? 0); i++)
                {
                    if (rows.ItemsSourceView!.GetAt(i) is not HistoryRow row
                        || rows.TryGetElement(i) is not FrameworkElement element || !Drawn(element, viewport)) { continue; }
                    var text = row.Disclosure?.Origin.Text ?? row.Prompt;
                    var n = Enumerable.Range(0, 5).FirstOrDefault(n => HistoryPaintHost.Prompt(n) == text, -1);
                    if (n < 0) { continue; }
                    visible++;
                    seen.Add($"history{n}");
                    if (n is 1 or 3 or 4)
                    {
                        var prompt = ById<StackPanel>(element, "HistoryPrompt");
                        var label = prompt is null ? null : Find<TextBlock>(prompt).FirstOrDefault(t => t.Text == row.Disclosure?.Origin.Label);
                        var bubble = Find<Border>(element).FirstOrDefault(b => b.Name == "HistoryYourMessage");
                        if (!AgentPresentation(element, prompt, label, prompt is null ? null : Find<DisclosureButton>(prompt).FirstOrDefault(),
                            bubble, HistoryPaintHost.Prompt(n), viewport)) { wrong.Add($"history{n}: agent presentation"); }
                    }
                    foreach (var markdown in Find<MarkdownView>(element).Where(m => Drawn(m, viewport)))
                    {
                        if (!Formatted(markdown)) { wrong.Add($"history{n}: reply formatting"); }
                        if (Formatted(markdown, n)) { seen.Add($"reply{n}"); }
                    }
                }
            }
            var live = Enumerable.Range(0, 5).FirstOrDefault(n => HistoryPaintHost.Prompt(n) == view.Card.Session.Activity.UserMessage, -1);
            if (live is 3 or 4 && (Drawn(ById<StackPanel>(view, "Prompt"), viewport) || Drawn(ById<Border>(view, "YourMessage"), viewport)))
            {
                seen.Add($"live{live}");
                if (!AgentPresentation(view, ById<StackPanel>(view, "Prompt"), ById<TextBlock>(view, "PromptLabelText"),
                    ById<DisclosureButton>(view, "ShowPrompt"), ById<Border>(view, "YourMessage"), HistoryPaintHost.Prompt(live), viewport))
                { wrong.Add($"live{live}: agent presentation"); }
            }
            foreach (var markdown in Find<MarkdownView>(view).Where(m =>
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(m) == "ResponseBlock" && Drawn(m, viewport)))
            {
                if (!Formatted(markdown)) { wrong.Add("live reply formatting"); }
                if (live >= 0 && Formatted(markdown, live)) { seen.Add($"reply{live}"); }
            }
            if (Find<TextBlock>(view).Any(t => t.Text.Contains("**Formatted reply ", StringComparison.Ordinal) && Drawn(t, viewport))
                || Find<RichTextBlock>(view).Any(t => Drawn(t, viewport) && t.Blocks.OfType<Paragraph>()
                    .SelectMany(p => p.Inlines.OfType<Run>()).Any(r => r.Text.Contains("**Formatted reply ", StringComparison.Ordinal))))
            { wrong.Add("raw reply markup visible"); }
            foreach (var problem in wrong) { failures.Add($"{phase}, frame {frames}: {problem}"); }
            AppLog.Write($"self-test: history-paint {phase}, frame {frames}, visible history {visible}, unformatted={wrong.Count > 0}: {string.Join("; ", wrong)}");
        }
        async Task Observe(string target, string reply)
        {
            await Until(() => seen.Contains(target) && seen.Contains(reply), TimeSpan.FromSeconds(10));
            await Task.Delay(300);
            check(frames > 0 && seen.Contains(target) && seen.Contains(reply) && failures.Count == 0 && host.HistoryRequests > 0,
                $"{phase}: never unformatted on the host-metadata path ({frames} frames; observed {string.Join(", ", seen.Order())}; {string.Join("; ", failures.Take(5))})");
        }
        try
        {
            connection.Start();
            if (!await Until(() => main.AllCards.Any(c => c.Id == HistoryPaintHost.Id), TimeSpan.FromSeconds(10)))
            { check(false, "discovers the isolated host-metadata paint session"); return; }
            window.ShowPage("sessions");
            Resize(window, 1200, 1000);
            Away(window, main);
            CompositionTarget.Rendering += Frame;
            await Choose(window, main, HistoryPaintHost.Id, "Response");
            await Observe("history1", "reply1");
            for (var n = 3; n <= 4; n++)
            {
                phase = $"host arrival {n - 2}";
                frames = 0;
                seen.Clear();
                await host.AppendAsync(n);
                await Observe($"live{n}", $"reply{n}");
            }
            check(failures.Count == 0,
                "Diagnostic only: a passing host-path observation does not fix or disprove the owner compositor flash; app version/recording remain missing.");
        }
        finally
        {
            CompositionTarget.Rendering -= Frame;
            Away(window, main);
            main.Remove(key);
        }
    }
}
