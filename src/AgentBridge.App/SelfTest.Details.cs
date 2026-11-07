using System.Text.Json.Nodes;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// A session's Details drawn (ui.md §6.2, #1087): the rows the view model holds, each drawn once and
// in its order, then End session, for a session with every row and for one with only what a
// session always has, each in light and dark, and in high contrast in its own pass. Which rows,
// in which order and words, is docs/design/session-details.json's, which DetailsTableTests checks.
static partial class SelfTest
{
    const string DetailsId = "demo-choice";

    // The demo's session as it would be with every row: nothing asked of the person, an agent driving.
    static Session DetailsFull(Session s, DateTimeOffset now) => s with
    {
        Repository = "danswett/agent-bridge",
        Branch = "main",
        Model = "gpt-5.6-sol",
        Cwd = @"C:\Users\dan\repos\agent-bridge",
        Status = SessionStatus.Working,
        StatusSince = now.AddMinutes(-12),
        StartedAt = now.AddMinutes(-47),
        UpdatedAt = now.AddMinutes(-2),
        ProcessId = 4312,
        Driver = DriverKind.Agent,
        DriverPrincipal = "homeassistant:demo",
        Question = null,
        WaitingOn = null,
        Activity = new Activity { Response = s.Activity.Response, TurnCount = 12, Compactions = 3, Background = new Background { Agents = 2, Shells = 1 } },
    };

    static Session DetailsSparse(Session s, DateTimeOffset now) => s with
    {
        Repository = null,
        Branch = null,
        Model = null,
        Cwd = null,
        StartedAt = null,
        ProcessId = null,
        Driver = null,
        DriverPrincipal = null,
        Question = null,
        WaitingOn = null,
        Status = SessionStatus.Idle,
        StatusSince = now.AddMinutes(-30),
        UpdatedAt = now.AddMinutes(-2),
        Activity = new Activity(),
    };

    // Both Message and Not responding since, which no real session has together: every row at once.
    static JsonObject DetailsAttention(DateTimeOffset now, AgentKind agent) => new()
    {
        ["kind"] = AuthState.NotResponding,
        ["since"] = now.AddMinutes(-12).ToString("O"),
        ["agent"] = AuthState.WireName(agent),
        ["lastEventAt"] = now.AddMinutes(-12).ToString("O"),
        ["message"] = "The agent has not answered for 12 minutes.",
    };

    // The words that wrap: a Status of several facts and a Message of a sentence or two, which a row
    // never trims (§6.2). Needs sign-in is the Status with the most to say.
    static JsonObject DetailsLongAttention(DateTimeOffset now, AgentKind agent) => new()
    {
        ["kind"] = AuthState.SignInNeeded,
        ["since"] = now.AddMinutes(-12).ToString("O"),
        ["agent"] = AuthState.WireName(agent),
        ["account"] = "a-collaborator-with-a-long-account-name",
        ["message"] = "The agent could not reach its provider because the sign-in it was using has expired, so it stopped before it could finish the change you asked it to make. Sign it in again and it carries on from where it was.",
    };

    // The shared table: docs/design/session-details.json, found from the repo the app runs from.
    // Its rows in order, those of the scenario with every row, and of the one with only what a
    // session always has.
    static (List<string> All, List<string> Full, List<string> Sparse)? DetailsTable()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var file = Path.Combine(dir, "docs", "design", "session-details.json");
            if (!File.Exists(file)) { continue; }
            var table = JsonNode.Parse(File.ReadAllText(file))!;
            List<string> Labels(JsonNode? rows) => [.. rows!.AsArray().Select(r => r!["row"]!.GetValue<string>())];
            var scenarios = table["scenarios"]!.AsArray();
            return (Labels(table["rows"]), Labels(scenarios[0]!["rows"]), Labels(scenarios[1]!["rows"]));
        }
        return null;
    }

    static bool Visible(FrameworkElement? e)
    {
        for (DependencyObject? at = e; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement { Visibility: Visibility.Collapsed }) { return false; }
        }
        return e is { ActualHeight: > 0, ActualWidth: > 0 };
    }

    static Windows.UI.Color ShellColor(string theme) => Application.Current.Resources.MergedDictionaries
        .Select(d => d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary rd && rd.TryGetValue("AbShell", out var b) && b is SolidColorBrush s ? s.Color : (Windows.UI.Color?)null)
        .FirstOrDefault(c => c is not null) ?? default;

    // The session as a host would send it next, chosen, and its Details drawn in the inspector.
    // kind: full (every row), long (a long Status and Message) or sparse (what a session always has).
    static async Task<(SessionCardViewModel Card, FrameworkElement Pane)?> ShowDetails(MainWindow window, MainViewModel main, DemoHost demo, Session original, string kind)
    {
        var now = DateTimeOffset.UtcNow;
        var sparse = kind == "sparse";
        await demo.Upsert(sparse ? DetailsSparse(original, now) : DetailsFull(original, now), sparse ? null : kind == "long" ? DetailsLongAttention(now, original.Agent) : DetailsAttention(now, original.Agent));
        if (await Choose(window, main, DetailsId, "ToggleInspector") is not { Card: { } card }) { return null; }
        var shown = await Until(() => card.Details.Any(d => d.Label == "Message") == !sparse && card.Details.Any(d => d.Label == "Model") == !sparse
            && card.Details.FirstOrDefault(d => d.Label == "Status")?.Value.StartsWith(kind == "long" ? "Needs sign-in" : "", StringComparison.Ordinal) == true
            && ById<FrameworkElement>(window.Sessions, "InspectorDetails") is { } pane && Visible(pane) && ById<TextBlock>(pane, "Detail:ID") is { } id && Visible(id), TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        return shown ? (card, ById<FrameworkElement>(window.Sessions, "InspectorDetails")!) : null;
    }

    // What is drawn: each row's value by its label, from the top down, and End session after them.
    static string DrawnRows(FrameworkElement pane, SessionCardViewModel card, string kind, out List<string> labels)
    {
        var drawn = Find<TextBlock>(pane)
            .Where(t => AutomationProperties.GetAutomationId(t).StartsWith("Detail:", StringComparison.Ordinal) && Visible(t))
            .Select(t => (Label: AutomationProperties.GetAutomationId(t)["Detail:".Length..], Text: t.Text, Top: t.TransformToVisual(pane).TransformPoint(default).Y, Block: t))
            .OrderBy(t => t.Top).ToList();
        labels = [.. drawn.Select(d => d.Label)];
        var problems = new List<string>();
        var want = card.Details.ToList();
        if (!labels.SequenceEqual(want.Select(d => d.Label))) { problems.Add($"rows [{string.Join(", ", labels)}], not [{string.Join(", ", want.Select(d => d.Label))}]"); }
        else
        {
            foreach (var (row, got) in want.Zip(drawn))
            {
                if (!row.Path && got.Text != row.Value) { problems.Add($"{row.Label} says \"{got.Text}\", not \"{row.Value}\""); }
                // Only a path is shortened, by its middle; every other value wraps to as many lines as it needs.
                if (!row.Path && (got.Block.IsTextTrimmed || got.Block.TextWrapping != TextWrapping.Wrap)) { problems.Add($"{row.Label} is cut off, not wrapped"); }
            }
        }
        if (drawn.Zip(drawn.Skip(1)).Any(p => p.Second.Top <= p.First.Top)) { problems.Add("rows not one under another"); }
        if (kind == "long" && drawn.FirstOrDefault(d => d.Label == "Agent").Block is { } line)
        {
            foreach (var label in new[] { "Status", "Message" })
            {
                if (drawn.FirstOrDefault(d => d.Label == label).Block is not { } block || block.ActualHeight < line.ActualHeight * 2) { problems.Add($"{label} not drawn over more than one line"); }
            }
        }
        var end = ById<Button>(pane, "EndSession");
        if (end is null || !Visible(end) || drawn.Count == 0 || end.TransformToVisual(pane).TransformPoint(default).Y <= drawn[^1].Top) { problems.Add("End session not drawn after the last row"); }
        return string.Join("; ", problems);
    }

    static async Task CheckSessionDetails(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check, string outFile)
    {
        var page = window.Sessions;
        var layout = page.Layout!;
        if (main.AllCards.FirstOrDefault(c => c.Id == DetailsId)?.Session is not { } original) { check(false, "finds the demo's session for Details"); return; }
        var originalAttention = demo.AttentionOf(DetailsId);
        try
        {
            Resize(window, 1440, 1000);
            await Until(() => layout.Mode == PaneMode.Three, TimeSpan.FromSeconds(10));
            if (DetailsTable() is not var (all, full, sparse))
            {
                check(false, "finds docs/design/session-details.json from the app, for the Details check");
                return;
            }
            // What the drawn pane must hold, from the table: every row for the full session, all but
            // the silence for a Needs sign-in, and the always-present rows (and the machine's) for the sparse.
            var expected = new Dictionary<string, List<string>>
            {
                ["full"] = full,
                ["long"] = [.. full.Where(l => l != "Not responding since")],
                ["sparse"] = [.. all.Where(l => sparse.Contains(l) || l == "Machine")],
            };
            foreach (var name in new[] { "full", "long", "sparse" })
            {
                if (await ShowDetails(window, main, demo, original, name) is not var (card, pane)) { check(false, $"draws the {name} session's Details"); continue; }
                var problems = DrawnRows(pane, card, name, out var labels);
                if (!labels.SequenceEqual(expected[name])) { problems += $"{(problems.Length > 0 ? "; " : "")}drawn [{string.Join(", ", labels)}], not the table's [{string.Join(", ", expected[name])}]"; }
                check(problems.Length == 0, $"draws the {name} session's Details as the table's rows, each once and in its order, one under another, none cut off, then End session{(problems.Length > 0 ? $": {problems}" : "")}");
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    window.RootElement.RequestedTheme = theme;
                    await Task.Delay(300);
                    await Picture(pane, Path.ChangeExtension(outFile, $".details.{name}.{theme.ToString().ToLowerInvariant()}.png"), ShellColor(theme.ToString()));
                }
                window.RootElement.RequestedTheme = ElementTheme.Default;
            }
        }
        finally
        {
            window.RootElement.RequestedTheme = ElementTheme.Default;
            await demo.Upsert(original, originalAttention);
            Resize(window, 1440, 1000);
        }
    }

    // The same two, in Windows' high contrast, taken in its own pass. The pane has no fill of its own,
    // so it is drawn on the window's colour, as the pages and the delegate fold are (#1079).
    static async Task PictureDetailsInHighContrast(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check, string outFile)
    {
        var taken = new List<string>();
        Resize(window, 1440, 1000);
        if (window.Sessions.Layout is { } layout) { await Until(() => layout.Mode == PaneMode.Three, TimeSpan.FromSeconds(10)); }
        if (main.AllCards.FirstOrDefault(c => c.Id == DetailsId)?.Session is { } original)
        {
            var originalAttention = demo.AttentionOf(DetailsId);
            try
            {
                foreach (var name in new[] { "full", "long", "sparse" })
                {
                    if (await ShowDetails(window, main, demo, original, name) is not var (_, pane)) { continue; }
                    await Picture(pane, Path.ChangeExtension(outFile, $".details.{name}.highcontrast.png"), ShellColor("HighContrast"));
                    taken.Add(name);
                }
            }
            finally { await demo.Upsert(original, originalAttention); }
        }
        check(taken.Count == 3, $"pictures Details in high contrast, with every row, with a long Status and Message, and with only the rows a session always has ({(taken.Count > 0 ? string.Join(", ", taken) : "none")})");
    }
}
