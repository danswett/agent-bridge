using AgentBridge.App.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckReplyReturn(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        var choices = ById<RadioButtons>(window.SettingsPage, "ReplyReturn");
        if (main.Settings is not { } settings || choices is null)
        {
            check(false, "offers the Return key choice in Settings");
            return;
        }
        var original = settings.ReplyReturnIndex;
        var selected = main.Selected;
        try
        {
            check(choices.Items.Count == 2 && choices.SelectedIndex == original
                && choices.Items.OfType<RadioButton>().Select(AutomationProperties.GetName).SequenceEqual(new[] { "New line", "Send" }),
                "shows both Return choices and marks the saved choice");
            var id = main.AllCards.FirstOrDefault(c => c.Reply is { Held: false })?.Id;
            var view = id is null ? null : await Choose(window, main, id, "SendReply");
            var send = view is null ? null : ById<Button>(view, "SendReply");
            foreach (var index in new[] { 1, 0 })
            {
                choices.SelectedIndex = index;
                var expected = index == 1 ? "Send (Enter)" : "Send (Ctrl+Enter)";
                check(await Until(() => settings.ReplyReturnIndex == index && send is not null
                    && ToolTipService.GetToolTip(send) as string == expected
                    && AutomationProperties.GetHelpText(send) == expected
                    && AutomationProperties.GetName(send) == "Send", TimeSpan.FromSeconds(5)),
                    $"updates the existing reply button to {expected} immediately, keeping its name Send");
            }
        }
        finally
        {
            settings.ReplyReturnIndex = original;
            main.Selected = selected;
            window.ShowPage("settings");
        }
    }
}
