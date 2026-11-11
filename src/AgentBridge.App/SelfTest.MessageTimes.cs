using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckMessageTimes(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        window.ShowPage("sessions");
        var view = await Choose(window, main, "demo-approval", "Response");
        if (view?.Card?.Stored is not { } stored) { check(false, "has stored history for the message-clock check"); return; }
        var sent = DateTimeOffset.UtcNow;
        stored.AddLive(new HistoryTurn
        {
            Id = "message-clock", At = sent, Prompt = new HistoryPrompt { Text = "A stored clock fixture" },
            Parts = [new HistoryPart { Id = "message-clock-reply", Kind = HistoryPartKind.Response, At = sent, Text = "A dated reply." }],
        });
        MessageTimeView? time = null;
        await Until(() => (time = Find<MessageTimeView>(view).FirstOrDefault(t => t.Stamp?.At == sent)) is not null, TimeSpan.FromSeconds(5));
        if (time is null) { check(false, "draws stored stamps as refreshable time buttons"); return; }
        var now = sent.AddSeconds(9);
        time.ReadClock = () => new MessageClock(now, TimeZoneInfo.Utc, System.Globalization.CultureInfo.InvariantCulture);
        time.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        time.Focus(FocusState.Keyboard);
        await Until(() => Shown(time, view) && FocusManager.GetFocusedElement(time.XamlRoot) == time, TimeSpan.FromSeconds(5));
        time.Refresh();
        window.RootElement.UpdateLayout();
        var scroll = ConversationScroller(view);
        var rows = ById<ItemsRepeater>(view, "History");
        var source = rows?.ItemsSource;
        var offset = scroll?.VerticalOffset;
        var height = time.ActualHeight;
        var width = time.ActualWidth;
        var exact = AutomationProperties.GetName(time);
        var stable = scroll is not null && rows is not null;
        foreach (var age in new[] { 10, 59, 60, 120, 3600 })
        {
            now = sent.AddSeconds(age);
            time.Refresh();
            window.RootElement.UpdateLayout();
            stable &= time.RefreshPending && (time.Content as TextBlock)?.Text == MessageTime.Label(sent, now, TimeZoneInfo.Utc, System.Globalization.CultureInfo.InvariantCulture)
                && time.ActualHeight == height && time.ActualWidth == width && scroll?.VerticalOffset == offset
                && ReferenceEquals(rows?.ItemsSource, source) && FocusManager.GetFocusedElement(time.XamlRoot) == time
                && AutomationProperties.GetName(time) == exact;
        }
        check(stable, "refreshes an existing stored label across second, minute and clock boundaries without changing its size, history source, focus or scroll offset");
        var before = (time.Content as TextBlock)?.Text;
        window.ShowPage("settings");
        now = sent.AddMinutes(10);
        window.RootElement.UpdateLayout();
        var stopped = await Until(() => !time.RefreshPending, TimeSpan.FromSeconds(5));
        time.Refresh();
        check(stopped && !time.RefreshPending && (time.Content as TextBlock)?.Text == before, "stops hidden message clocks without changing their text");
        window.ShowPage("sessions");
        check(await Until(() => (time.Content as TextBlock)?.Text == "10 min ago" && time.RefreshPending, TimeSpan.FromSeconds(5)),
            "recomputes a stored message time when its view returns, without another history event");
        time.ReadClock = () => MessageClock.Local;
        time.Refresh();
    }
}
