using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// Settings › Agents on this machine (#678): each row's buttons are named for its agent and have ids
// of their own, a row shows one verb for its hooks' state (the demo's Copilot CLI to set up, Claude
// Code to repair, Codex working, with none), and a setup's result is said on its own row, as a
// polite live region, a failure in Danger. Narrator's names for the rows' buttons are kept in the
// result as the list it reads.
static partial class SelfTest
{
    static async Task CheckAgentRows(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile, Dictionary<string, object?> result)
    {
        window.ShowPage("settings");
        var view = window.SettingsPage;
        var settings = main.Settings;
        var rows = view.AgentRows;
        if (settings is null || !await Until(() => settings.Agents.Count == 3 && Buttons(rows).Count(b => b.Id.StartsWith("RemoveAgent_", StringComparison.Ordinal)) == 3, TimeSpan.FromSeconds(10)))
        {
            check(false, $"lists the demo's three agents under Settings › Agents on this machine ({settings?.Agents.Count ?? 0}: {settings?.AgentsState})");
            return;
        }
        rows.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        await Task.Delay(400);

        // What UI Automation says of each button shown, as Narrator reads it.
        var shown = Buttons(rows).Where(b => b.Button.Visibility == Visibility.Visible).ToList();
        var names = shown.Select(b => $"{b.Name} ({b.Id})").ToList();
        result["agentRowNames"] = names;
        var ids = Buttons(rows).Select(b => b.Id).ToList();
        string[] wanted = ["Set up Copilot CLI", "Remove Copilot CLI's connection", "Repair Claude Code", "Remove Claude Code's connection", "Remove Codex's connection"];
        check(shown.Select(b => b.Name).SequenceEqual(wanted) && ids.Distinct().Count() == ids.Count,
            $"names each agent row's buttons for its agent, with ids of their own ({string.Join(" | ", names)})");
        var verbs = shown.Where(b => b.Id.StartsWith("SetUpAgent_", StringComparison.Ordinal)).Select(b => $"{b.Id}: {b.Button.Content}").ToList();
        var codexVerb = Buttons(rows).FirstOrDefault(b => b.Id == "SetUpAgent_codex").Button;
        check(verbs.SequenceEqual(["SetUpAgent_copilot: Set up", "SetUpAgent_claude: Repair"]) && codexVerb is null or { Visibility: Visibility.Collapsed },
            $"shows one verb for each agent's hooks, and none for working ones ({string.Join(", ", verbs)}; Codex's {(codexVerb is null ? "absent" : codexVerb.Visibility.ToString())})");

        // A setup that works and a repair that fails, as a person makes them: the row's button, then
        // the confirmation, named for the agent with Cancel the default. Each is said on its own row
        // and nowhere else, and raised for Narrator as it starts and again as it ends (#705 review).
        view.Announced.Clear();
        var asked = new List<string>();
        foreach (var (agent, title) in new[] { ("copilot", "Set up Copilot CLI?"), ("claude", "Repair Claude Code?") })
        {
            var asks = await Confirm(window, Buttons(rows).FirstOrDefault(b => b.Id == $"SetUpAgent_{agent}").Button);
            asked.Add(asks ?? $"no dialog for {agent}");
            if (asks != $"{title} (Cancel the default)") { check(false, $"asks to confirm {agent}'s setup, named for it, Cancel the default ({asks})"); return; }
            await Until(() => !settings.Busy && view.Announced.Count(a => a.Id == agent) >= 2, TimeSpan.FromSeconds(10));
        }
        await Task.Delay(400);
        var heard = view.Announced.Select(a => $"{a.Id}: {a.Text}").ToList();
        result["agentRowAnnouncements"] = heard;
        check(heard.SequenceEqual(["copilot: Setting up…", $"copilot: {settings.Agents.Single(a => a.Id == "copilot").Status}", "claude: Repairing…", $"claude: {settings.Agents.Single(a => a.Id == "claude").Status}"]),
            $"confirms each setup by name and raises its row's progress and result for Narrator ({string.Join(" | ", asked)}; {string.Join(" | ", heard)})");
        TextBlock? Status(string id) => view.AgentStatusOf(id);
        bool OnItsRow(string id) => Status(id) is { } status && Buttons(rows).FirstOrDefault(b => b.Id == $"RemoveAgent_{id}").Button is { } remove
            && VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(remove)) == VisualTreeHelper.GetParent(status);
        var done = Status("copilot");
        var failed = Status("claude");
        var quiet = Status("codex");
        check(done is { Visibility: Visibility.Visible, Text.Length: > 0 } && AutomationProperties.GetLiveSetting(done) == AutomationLiveSetting.Polite && OnItsRow("copilot")
            && failed is { Visibility: Visibility.Visible } && AutomationProperties.GetLiveSetting(failed) == AutomationLiveSetting.Polite && OnItsRow("claude")
            && Colour(failed) == (Ui.Brush("AbDangerText") as SolidColorBrush)?.Color && Colour(done) != Colour(failed)
            && quiet is null or { Visibility: Visibility.Collapsed },
            $"says each setup's result on its own row as a polite live region, a failure in Danger (Copilot CLI: \"{done?.Text}\"; Claude Code: \"{failed?.Text}\"; Codex: {(quiet is null ? "none" : quiet.Visibility.ToString())})");
        rows.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        var themed = new List<string>();
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            window.RootElement.RequestedTheme = theme;
            await Task.Delay(400);
            var danger = (Ui.ThemedBrush("AbDangerText", theme) as SolidColorBrush)?.Color;
            if (Status("claude") is not { } shownFailure || Colour(shownFailure) != danger) { themed.Add($"{theme}: {(Status("claude") is { } f ? Colour(f) : null)}, not {danger}"); }
            await Picture(window.RootElement, Path.ChangeExtension(outFile, $".settings-agents.{theme.ToString().ToLowerInvariant()}.png"));
        }
        window.RootElement.RequestedTheme = ElementTheme.Default;
        check(themed.Count == 0, $"draws a failed setup in the Danger text of the theme its row is drawn in{(themed.Count > 0 ? $" ({string.Join("; ", themed)})" : "")}");
        // A fresh check takes the rows' results and the failed repair's page-wide error, as it would
        // for a person, so the pages drawn after are as before.
        await settings.CheckAgentsAsync();
        check(!settings.HasError && settings.Agents.All(a => a.Status.Length == 0), $"clears each row's result with a fresh check ({settings.Error})");
        await Task.Delay(300);
    }

    static Windows.UI.Color? Colour(TextBlock text) => (text.Foreground as SolidColorBrush)?.Color;

    // Presses a row's button as UI Automation would, then the confirmation's primary button; says
    // what the confirmation asked, or null when none opened.
    static async Task<string?> Confirm(MainWindow window, Button? button)
    {
        if (button is null) { return null; }
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        ContentDialog? dialog = null;
        await Until(() => (dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(window.RootElement.XamlRoot)
            .SelectMany(p => p.Child is ContentDialog c ? new[] { c } : p.Child is { } child ? Find<ContentDialog>(child).ToArray() : []).FirstOrDefault()) is not null, TimeSpan.FromSeconds(5));
        if (dialog is null) { return null; }
        await Task.Delay(300);
        var asks = $"{dialog.Title} ({(dialog.DefaultButton == ContentDialogButton.Close ? $"{dialog.CloseButtonText} the default" : $"{dialog.DefaultButton} the default")})";
        if (Find<Button>(dialog).FirstOrDefault(b => b.Name == "PrimaryButton") is { } primary)
        {
            ((IInvokeProvider)new ButtonAutomationPeer(primary).GetPattern(PatternInterface.Invoke)).Invoke();
        }
        else { dialog.Hide(); }
        return asks;
    }

    // The high-contrast picture of the same rows, results and all, taken in the high-contrast pass.
    static async Task PictureAgentRows(MainWindow window, MainViewModel main, string outFile, string theme)
    {
        window.ShowPage("settings");
        if (main.Settings is { } settings)
        {
            await settings.SetUpAgentAsync("copilot");
            await settings.SetUpAgentAsync("claude");
        }
        await Task.Delay(300);
        window.SettingsPage.AgentRows.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        await Task.Delay(400);
        await Picture(window.RootElement, Path.ChangeExtension(outFile, $".settings-agents.{theme}.png"));
        if (main.Settings is { } after) { await after.CheckAgentsAsync(); }
    }

    static IEnumerable<(string Id, string Name, Button Button)> Buttons(ItemsControl rows) => Find<Button>(rows)
        .Select(b => (Id: AutomationProperties.GetAutomationId(b), Name: FrameworkElementAutomationPeer.CreatePeerForElement(b)?.GetName() ?? "", Button: b))
        .Where(b => b.Id.StartsWith("SetUpAgent_", StringComparison.Ordinal) || b.Id.StartsWith("RemoveAgent_", StringComparison.Ordinal));
}
