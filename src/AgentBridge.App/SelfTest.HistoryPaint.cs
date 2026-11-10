using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

static partial class SelfTest
{
    // Observe frames, not the eventual state after bindings and layout have settled (#1759).
    static async Task CheckHistoryPaint(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check)
    {
        window.ShowPage("sessions");
        Resize(window, 1200, 900);
        const string session = "demo-history-paint";
        await demo.PutAsync(DemoHost.Examples().First() with { Id = session, Title = "Transcript paint", Question = null });
        await Until(() => main.AllCards.Any(c => c.Id == session), TimeSpan.FromSeconds(10));
        var view = await Choose(window, main, session, "Response");
        if (view?.Card is not { Stored: { } stored } card || ById<ItemsRepeater>(view, "History") is not { } rows
            || ConversationScroller(view) is not { } viewport)
        {
            check(false, "finds stored turns for the transcript first-paint check");
            Away(window, main);
            await demo.RemoveSessionAsync(session);
            return;
        }

        HistoryTurn Turn(int n) => new()
        {
            Id = n % 3 == 0 ? $"paint{n}" : $"person{n}",
            At = StoredFrom.AddMinutes(n),
            Prompt = new HistoryPrompt
            {
                Text = $"Agent message {n}.\n\nThis whole message must stay behind Show prompt until opened.",
                From = n % 3 == 0 ? UserMessageFrom.Agent : null,
            },
            Parts = [new HistoryPart
            {
                Id = $"paint{n}p",
                Kind = HistoryPartKind.Response,
                At = StoredFrom.AddMinutes(n),
                Text = $"**Formatted reply {n}** with `code`.",
            }],
        };

        bool Shown(FrameworkElement element)
        {
            if (!element.IsLoaded) { return false; }
            for (DependencyObject? at = element; at is not null; at = VisualTreeHelper.GetParent(at))
            {
                if (at is UIElement { Visibility: Visibility.Collapsed }) { return false; }
            }
            return element.ActualWidth > 0 && element.ActualHeight > 0;
        }

        bool Drawn(FrameworkElement element)
        {
            if (!Shown(element)) { return false; }
            var bounds = element.TransformToVisual(viewport).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return bounds.Bottom > 0 && bounds.Top < viewport.ViewportHeight;
        }

        var mono = ((FontFamily)Application.Current.Resources["MonoFontFamily"]).Source;
        bool Formatted(MarkdownView markdown)
        {
            var runs = markdown.Children.OfType<RichTextBlock>().SelectMany(r => r.Blocks.OfType<Paragraph>())
                .SelectMany(p => p.Inlines.OfType<Run>()).ToList();
            return runs.Any(r => r.Text.StartsWith("Formatted reply ", StringComparison.Ordinal) && r.FontWeight.Weight == Microsoft.UI.Text.FontWeights.SemiBold.Weight)
                && runs.Any(r => r.Text == "code" && r.FontFamily?.Source == mono);
        }

        var phase = "first paint";
        var failures = new HashSet<string>();
        var seen = new HashSet<string>();
        var frames = 0;
        void Frame(object? sender, object e)
        {
            frames++;
            for (var i = 0; i < (rows.ItemsSourceView?.Count ?? 0); i++)
            {
                if (rows.ItemsSourceView!.GetAt(i) is not HistoryRow row || !row.Id.StartsWith("paint", StringComparison.Ordinal)
                    || rows.TryGetElement(i) is not FrameworkElement element || !Drawn(element)) { continue; }
                seen.Add(row.Id);
                var prompt = ById<StackPanel>(element, "HistoryPrompt");
                if (prompt is null || !Shown(prompt) || !Find<DisclosureButton>(prompt).Any(b => !b.IsExpanded))
                {
                    failures.Add($"{phase}, frame {frames}, {row.Id}: missing collapsed agent presentation");
                }
                var label = prompt is null ? null : Find<TextBlock>(prompt).FirstOrDefault(t => t.Text == row.Disclosure?.Origin.Label);
                if (label?.Foreground is not SolidColorBrush colour || Ui.ThemedBrush("AbAgentText", element.ActualTheme) is not SolidColorBrush expected || colour.Color != expected.Color)
                {
                    failures.Add($"{phase}, frame {frames}, {row.Id}: agent label not in its formatted colour");
                }
                if (Find<Border>(element).Any(b => b.Name == "HistoryYourMessage" && Drawn(b))
                    || Find<TextBlock>(element).Any(t => t.Text == row.Disclosure?.Origin.Text && Drawn(t)))
                {
                    failures.Add($"{phase}, frame {frames}, {row.Id}: unformatted full prompt visible");
                }
                var markdown = Find<MarkdownView>(element);
                if (markdown.Count != row.Blocks.Count || markdown.Any(m => !Formatted(m)))
                {
                    failures.Add($"{phase}, frame {frames}, {row.Id}: reply not formatted");
                }
            }
        }

        Away(window, main);
        await Task.Delay(300);
        for (var n = 1; n <= 300; n++) { stored.AddLive(Turn(n)); }
        CompositionTarget.Rendering += Frame;
        try
        {
            window.Sessions.Rows.SelectedItem = card;
            await Until(() => seen.Contains("paint300"), TimeSpan.FromSeconds(10));
            await Task.Delay(500);
            check(seen.Contains("paint300") && failures.Count == 0,
                $"first paint never shows an unformatted transcript ({frames} frames; {string.Join("; ", failures.Take(5))})");

            phase = "arriving reply";
            failures.Clear();
            frames = 0;
            for (var n = 301; n <= 309; n++)
            {
                stored.AddLive(Turn(n));
                if (n % 3 == 0) { await Until(() => seen.Contains($"paint{n}"), TimeSpan.FromSeconds(5)); }
                await Task.Delay(200);
            }
            check(new[] { 303, 306, 309 }.All(n => seen.Contains($"paint{n}")) && failures.Count == 0,
                $"each arriving reply never shows an unformatted transcript ({frames} frames; {string.Join("; ", failures.Take(5))})");

            if (ConversationScroller(view) is { } scroll)
            {
                phase = "offscreen arrival";
                failures.Clear();
                view.ReleasePin();
                scroll.ChangeView(null, 0, null, disableAnimation: true);
                await Task.Delay(300);
                stored.AddLive(Turn(312));
                await Task.Delay(300);
                scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
                await Until(() => seen.Contains("paint312"), TimeSpan.FromSeconds(10));
                check(seen.Contains("paint312") && failures.Count == 0,
                    $"a reply arriving offscreen is formatted on its first visible frame ({string.Join("; ", failures.Take(5))})");
            }
            else { check(false, "finds the scroller for an offscreen reply"); }

            CompositionTarget.Rendering -= Frame;
            Away(window, main);
            await Task.Delay(300);
            phase = "restored view";
            failures.Clear();
            seen.Clear();
            CompositionTarget.Rendering += Frame;
            window.Sessions.Rows.SelectedItem = card;
            await Until(() => seen.Contains("paint312"), TimeSpan.FromSeconds(10));
            check(seen.Contains("paint312") && failures.Count == 0,
                $"restoring the transcript never shows its unformatted form ({string.Join("; ", failures.Take(5))})");

            // Replace a person's row with its late agent mark through the same collection and template.
            if (rows.ItemsSource is IList<HistoryRow> shown)
            {
                CompositionTarget.Rendering -= Frame;
                var late = Turn(315);
                var before = late with { Prompt = late.Prompt! with { From = null } };
                HistoryRowSync.Apply(shown, HistoryDraw.Rows([before], null));
                await Task.Delay(300);
                phase = "late attribution";
                failures.Clear();
                seen.Clear();
                CompositionTarget.Rendering += Frame;
                HistoryRowSync.Apply(shown, HistoryDraw.Rows([late], null));
                await Until(() => seen.Contains("paint315"), TimeSpan.FromSeconds(10));
                await Task.Delay(300);
                check(seen.Contains("paint315") && failures.Count == 0,
                    $"late attribution publishes the formatted row without an unformatted frame ({string.Join("; ", failures.Take(5))})");
            }
            else { check(false, "finds the history collection for a late agent mark"); }
        }
        finally
        {
            CompositionTarget.Rendering -= Frame;
            Away(window, main);
            await demo.RemoveSessionAsync(session);
        }
    }
}
