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
// Code to repair, Codex set up but not tested yet, as a healthy agent reads on a real host, with none), and a setup's result is said on its own row, as a
// polite live region, a failure in Danger. Narrator's names for the rows' buttons are kept in the
// result as the list it reads.
static partial class SelfTest
{
    static async Task CheckAgentRows(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check, string outFile, Dictionary<string, object?> result)
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
        // Copilot CLI has no owned record yet, so nothing to remove: no Remove button (#784's ruling).
        string[] wanted = ["Set up Copilot CLI", "Repair Claude Code", "Remove Claude Code's connection", "Remove Codex's connection"];
        check(shown.Select(b => b.Name).SequenceEqual(wanted) && ids.Distinct().Count() == ids.Count,
            $"names each agent row's buttons for its agent, with ids of their own ({string.Join(" | ", names)})");
        var verbs = shown.Where(b => b.Id.StartsWith("SetUpAgent_", StringComparison.Ordinal)).Select(b => $"{b.Id}: {b.Button.Content}").ToList();
        var codexVerb = Buttons(rows).FirstOrDefault(b => b.Id == "SetUpAgent_codex").Button;
        // Codex is set up and the host can't test it yet (#705 review): nothing to do, so no verb, and
        // "Not tested yet" where a result would be.
        var untested = view.AgentStatusOf("codex");
        check(verbs.SequenceEqual(["SetUpAgent_copilot: Set up", "SetUpAgent_claude: Repair"]) && codexVerb is null or { Visibility: Visibility.Collapsed }
            && untested is { Text: "Not tested yet", Visibility: Visibility.Visible },
            $"shows one verb for each agent's hooks, and none for one set up and not tested yet ({string.Join(", ", verbs)}; Codex's {(codexVerb is null ? "absent" : codexVerb.Visibility.ToString())}, \"{untested?.Text}\")");

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
        check(heard.SequenceEqual(["copilot: Copilot CLI: Setting up…", $"copilot: {settings.Agents.Single(a => a.Id == "copilot").StatusName}", "claude: Claude Code: Repairing…", $"claude: {settings.Agents.Single(a => a.Id == "claude").StatusName}"]),
            $"confirms each setup by name and raises its row's progress and result for Narrator, named for its agent ({string.Join(" | ", asked)}; {string.Join(" | ", heard)})");
        // A raise queued for a status the row has since moved on from, or one already said, is not
        // heard: a setup that ends before its progress is drawn is heard once (#705 review).
        view.Announced.Clear();
        view.Announce("copilot", "Setting up…");
        view.Announce("copilot", settings.Agents.Single(a => a.Id == "copilot").Status);
        await Task.Delay(400);
        check(view.Announced.Count == 0, $"skips a status raise that was superseded or already said ({string.Join(" | ", view.Announced.Select(a => $"{a.Id}: {a.Text}"))})");
        TextBlock? Status(string id) => view.AgentStatusOf(id);
        bool OnItsRow(string id) => Status(id) is { } status && Buttons(rows).FirstOrDefault(b => b.Id == $"RemoveAgent_{id}").Button is { } remove
            && VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(remove)) == VisualTreeHelper.GetParent(status);
        var done = Status("copilot");
        var failed = Status("claude");
        var quiet = Status("codex");
        check(done is { Visibility: Visibility.Visible, Text.Length: > 0 } && AutomationProperties.GetLiveSetting(done) == AutomationLiveSetting.Polite && OnItsRow("copilot")
            && failed is { Visibility: Visibility.Visible } && AutomationProperties.GetLiveSetting(failed) == AutomationLiveSetting.Polite && OnItsRow("claude")
            && Colour(failed) == (Ui.Brush("AbDangerText") as SolidColorBrush)?.Color && Colour(done) != Colour(failed)
            && quiet is { Text: "Not tested yet" }
            && Named(done) == $"Copilot CLI: {done?.Text}" && Named(failed) == $"Claude Code: {failed?.Text}" && Named(quiet) == "Codex: Not tested yet",
            $"says each setup's result on its own row as a polite live region named for its agent, a failure in Danger (\"{Named(done)}\"; \"{Named(failed)}\"; \"{Named(quiet)}\")");
        static string? Named(TextBlock? status) => status is null ? null : FrameworkElementAutomationPeer.CreatePeerForElement(status)?.GetName();
        // A completed setup moves the row on as a real host's evidence does (#705 review): Copilot CLI,
        // set up, offers no Set up beside "Hooks registered…", and its Hooks line no longer says
        // intake is off.
        var setUp = settings.Agents.Single(a => a.Id == "copilot");
        var copilotVerb = Buttons(rows).FirstOrDefault(b => b.Id == "SetUpAgent_copilot").Button;
        check(copilotVerb is null or { Visibility: Visibility.Collapsed } && setUp.SetUpVerb is null && !setUp.Checks.Contains("intake is off", StringComparison.Ordinal),
            $"offers no Set up once Copilot CLI's setup completed, its Hooks line moved on ({(copilotVerb is null ? "absent" : $"{copilotVerb.Visibility} {copilotVerb.Content}")}; {setUp.Checks.Split('\n')[1]})");
        // The rows' foot at the window's: the last row's "Not tested yet" in the pictures too.
        rows.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 1 });
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
        // The rows the app can't act on, for Creative's acceptance (#705): each says why in its own
        // words, never the host's raw text, and offers nothing that can't work.
        await PictureRowsTheAppCantActOn(window, main, demo, outFile, [ElementTheme.Light, ElementTheme.Dark], check, result);
        // A fresh check takes the rows' results and the failed repair's page-wide error, as it would
        // for a person, so the pages drawn after are as before; Copilot CLI is set up and not tested yet.
        await settings.CheckAgentsAsync();
        string[] after = [.. settings.Agents.Select(a => $"{a.Id}: {a.Status}")];
        check(!settings.HasError && after.SequenceEqual(["copilot: Not tested yet", "claude: ", "codex: Not tested yet"]), $"clears each row's result with a fresh check ({string.Join(" | ", after)}{settings.Error})");
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

    // A failed repair drawn on Settings before high contrast comes on, with its Danger colour then
    // (#705 review: turning high contrast on can leave ActualTheme as it was).
    static async Task<(TextBlock? Status, Windows.UI.Color? Was)> ShowFailedAgentRow(MainWindow window, MainViewModel main)
    {
        window.ShowPage("settings");
        if (main.Settings is not { } settings) { return (null, null); }
        await settings.SetUpAgentAsync("claude");
        await Task.Delay(400);
        var status = window.SettingsPage.AgentStatusOf("claude");
        return (status, status is null ? null : Colour(status));
    }

    // The same line, not drawn again, now in high contrast's Danger text; then a fresh check takes the
    // result and the page-wide error, so the pages drawn next are as before.
    static async Task CheckAgentRowRepainted(MainWindow window, MainViewModel main, (TextBlock? Status, Windows.UI.Color? Was) before, Action<bool, string> check)
    {
        var now = window.SettingsPage.AgentStatusOf("claude");
        var danger = (Ui.ThemedBrush("AbDangerText", now?.ActualTheme ?? ElementTheme.Default) as SolidColorBrush)?.Color;
        check(before.Status is not null && ReferenceEquals(now, before.Status) && Colour(before.Status) == danger && danger != before.Was,
            $"repaints a failed setup's status already drawn in high contrast's Danger text ({before.Was} then {(before.Status is null ? null : Colour(before.Status))}, wanted {danger}{(ReferenceEquals(now, before.Status) ? "" : "; the row was drawn again")})");
        if (main.Settings is { } settings) { await settings.CheckAgentsAsync(); }
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
        window.SettingsPage.AgentRows.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 1 });
        await Task.Delay(400);
        await Picture(window.RootElement, Path.ChangeExtension(outFile, $".settings-agents.{theme}.png"));
        if (main.Settings is { } after) { await after.CheckAgentsAsync(); }
    }

    // The demo's rows the app can't act on (DemoHost.ShowAgentsTheAppCantActOn), pictured in each
    // theme given: Copilot CLI read-only and not installed, with no Install; Claude Code's failed
    // installation and unreadable hook ownership, each by its reason, with Install CLI offered again;
    // and Codex on an older host. Checked once: their words, no raw text, no verb or Remove.
    static async Task PictureRowsTheAppCantActOn(MainWindow window, MainViewModel main, DemoHost demo, string outFile, ElementTheme[] themes,
        Action<bool, string>? check = null, Dictionary<string, object?>? result = null)
    {
        window.ShowPage("settings");
        if (main.Settings is not { } settings) { check?.Invoke(false, "pictures the rows the app can't act on (no Settings)"); return; }
        demo.ShowAgentsTheAppCantActOn(true);
        try
        {
            await settings.CheckAgentsAsync();
            var rows = window.SettingsPage.AgentRows;
            await Until(() => settings.Agents.Any(a => a.Id == "codex" && a.Status.StartsWith("Can't check", StringComparison.Ordinal)), TimeSpan.FromSeconds(10));
            rows.UpdateLayout();
            await Task.Delay(400);
            if (check is not null)
            {
                string[] said = [.. settings.Agents.Select(a => $"{a.Id}: {a.Installation} | {a.Status}")];
                result?.Add("agentRowsTheAppCantActOn", said);
                var words = Find<TextBlock>(rows).Select(t => t.Text).ToList();
                var shown = Buttons(rows).Where(b => b.Button.Visibility == Visibility.Visible).Select(b => b.Id).ToList();
                // Install CLI is a plain button: a toggle would be read as "on" or "pressed", a state the
                // person didn't choose (Creative, 6675018272).
                var install = Buttons(rows).FirstOrDefault(b => b.Id == "InstallAgent_claude");
                var installPeer = install.Button is null ? null : FrameworkElementAutomationPeer.CreatePeerForElement(install.Button);
                check(installPeer is not null && install.Name == "Install CLI for Claude Code" && installPeer.GetPattern(PatternInterface.Toggle) is null
                    && installPeer.GetPattern(PatternInterface.Invoke) is not null && AutomationProperties.GetItemStatus(install.Button!) == "",
                    $"offers Install CLI again after a failed installation, a button named for the product with no toggle state ({install.Name}; toggle: {installPeer?.GetPattern(PatternInterface.Toggle) is not null})");
                check(said.SequenceEqual([
                        "copilot: Not installed · install it on this machine | Read-only · this machine's bridge is older",
                        "claude: Install failed · Windows Package Manager couldn't install it | Hook ownership couldn't be checked · access denied",
                        "codex: Installed, version 0.48.0 | Can't check · this machine's bridge is older"])
                    && !words.Any(w => w.Contains("Access to the path", StringComparison.Ordinal) || w.Contains("0x8A15000F", StringComparison.Ordinal) || w.Contains("agent-ha-bridge", StringComparison.Ordinal))
                    // Each reason once, on the status line: the checks beneath say only the fact (Creative, 6675472997).
                    && words.Count(w => w.Contains("Read-only ·", StringComparison.Ordinal)) == 1 && words.Count(w => w.Contains("· access denied", StringComparison.Ordinal)) == 1
                    && shown.SequenceEqual(["InstallAgent_claude"])
                    && Status("copilot") is { } readOnly && ReferenceEquals(readOnly.Style, window.SettingsPage.Resources["AgentStatusAbText2"]),
                    $"says why of each row the app can't act on, in its words and the neutral tone, with nothing on it that can't work ({string.Join(" / ", said)}; shown: {string.Join(", ", shown)}; {(Status("copilot") is { } s ? Colour(s)?.ToString() : "no status")})");
            }
            rows.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 1 });
            foreach (var theme in themes)
            {
                window.RootElement.RequestedTheme = theme;
                await Task.Delay(400);
                var name = theme == ElementTheme.Default ? "highcontrast" : theme.ToString().ToLowerInvariant();
                await Picture(window.RootElement, Path.ChangeExtension(outFile, $".settings-agents-cant-act.{name}.png"));
            }
            window.RootElement.RequestedTheme = ElementTheme.Default;
        }
        finally
        {
            demo.ShowAgentsTheAppCantActOn(false);
            await settings.CheckAgentsAsync();
            await Task.Delay(300);
        }
        TextBlock? Status(string id) => window.SettingsPage.AgentStatusOf(id);
    }

    static IEnumerable<(string Id, string Name, Button Button)> Buttons(ItemsControl rows) => Find<Button>(rows)
        .Select(b => (Id: AutomationProperties.GetAutomationId(b), Name: FrameworkElementAutomationPeer.CreatePeerForElement(b)?.GetName() ?? "", Button: b))
        .Where(b => b.Id.StartsWith("SetUpAgent_", StringComparison.Ordinal) || b.Id.StartsWith("RemoveAgent_", StringComparison.Ordinal) || b.Id.StartsWith("InstallAgent_", StringComparison.Ordinal));
}
