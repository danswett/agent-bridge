using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// Your messages in the stored conversation (ui.md §6.4, #1735): an earlier turn's prompt you typed
// is a bubble at the right edge of the history column, at most 85% of it, as the live prompt is and
// as the PWA's history is; its time stays under it. Before the fix it was plain text at the column's
// start, beside the agent's replies, so the transcript did not read as a chat. A prompt you didn't
// type (a schedule's) stays the collapsed line at the start, never a bubble.
static partial class SelfTest
{
    static async Task CheckHistoryYourMessages(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        window.ShowPage("sessions");
        Resize(window, 1200, 900);
        var view = await Choose(window, main, "demo-approval", "Response");
        var stored = view?.Card?.Stored;
        if (view is null || stored is null) { check(false, "has a session whose stored turns can be drawn to check your messages in them"); return; }
        var at = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
        var longText = string.Join(' ', Enumerable.Repeat("A long message of the person's that has to wrap inside its bubble, `code` and a_very_long_identifier_with_no_break_points_at_all included.", 6));
        HistoryTurn Turn(string id, int minute, HistoryPrompt prompt) => new()
        {
            Id = id,
            At = at.AddMinutes(minute),
            Prompt = prompt,
            Parts = [new HistoryPart { Id = $"{id}p", Kind = HistoryPartKind.Response, At = at.AddMinutes(minute), Text = "A reply." }],
        };
        stored.AddLive(Turn("yours-short", 1, new HistoryPrompt { Text = "Yes" }));
        stored.AddLive(Turn("yours-long", 2, new HistoryPrompt { Text = longText }));
        stored.AddLive(Turn("yours-schedule", 3, new HistoryPrompt { Text = "Run the nightly check.", From = UserMessageFrom.Schedule, Label = "4" }));

        var wrong = new List<string>();
        foreach (var width in new[] { 1440.0, 400.0 })
        {
            Resize(window, width, 900);
            view = await Choose(window, main, "demo-approval", "Response");
            if (view is null) { wrong.Add($"{width}: not drawn"); continue; }
            var bubbles = new List<Border>();
            var column = ById<StackPanel>(view, "HistoryColumn");
            await Until(() =>
            {
                bubbles = Find<Border>(view).Where(b => b.Name == "HistoryYourMessage").ToList();
                return column is not null && bubbles.Count >= 2 && bubbles.All(b => b.ActualWidth > 0);
            }, TimeSpan.FromSeconds(10));
            if (column is null || bubbles.Count != 2) { wrong.Add($"{width}: {bubbles.Count} bubbles of 2"); continue; }
            var inner = column.ActualWidth - column.Padding.Left - column.Padding.Right;
            foreach (var bubble in bubbles)
            {
                var right = bubble.TransformToVisual(column).TransformPoint(new Windows.Foundation.Point(bubble.ActualWidth, 0)).X;
                if (Math.Abs(right - (column.ActualWidth - column.Padding.Right)) > 1 || bubble.ActualWidth > inner * 0.85 + 1)
                {
                    wrong.Add($"{width}: bubble {bubble.ActualWidth:0} wide ending at {right:0}, column {column.ActualWidth:0}");
                }
            }
            // The scheduled prompt is the collapsed line at the start, with no bubble of its own.
            var lines = Find<StackPanel>(view).Where(s => s.Name == "" && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(s) == "HistoryPrompt" && s.Visibility == Visibility.Visible).ToList();
            if (lines.Count < 1) { wrong.Add($"{width}: no scheduled prompt line"); }
        }
        check(wrong.Count == 0, $"draws a prompt you typed in the stored conversation as a bubble at the right of its column, at most 85% of it, short and long, at every width, and a scheduled prompt as its line{(wrong.Count > 0 ? $": not {string.Join("; ", wrong)}" : "")}");
        Resize(window, 1440, 1000);
    }
}
