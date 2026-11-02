using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AgentBridge.App;

// --self-test <file>: CI's check that the published app starts, draws the demo's sessions and
// answers through them. It finds the controls the way UI Automation does (by automation id),
// clicks through their automation peers, waits for the demo host's answer to come back to the
// session, saves a picture of each page beside the result file, writes what it saw and exits.
static partial class SelfTest
{
    public static async Task RunAsync(MainWindow window, MainViewModel main, LaunchViewModel launch, string outFile, DemoHost demo, DemoHost remote, Action<int> exit)
    {
        var result = new Dictionary<string, object?>();
        var checks = new List<string>();
        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            (ok ? checks : failures).Add(what);
            if (!ok) { AppLog.Write($"self-test: failed: {what}"); }
        }
        // A ThemeResource or StaticResource that resolves to nothing, in any theme: logged, and a
        // failure, since an installed app may die of it with nothing but 0xC000027B.
        // Off by default: without it, the event is never raised and a failed reference passes.
        Application.Current.DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        Application.Current.DebugSettings.XamlResourceReferenceFailed += (_, e) =>
        {
            AppLog.Write($"self-test: a XAML resource reference failed: {e.Message}");
            failures.Add($"a XAML resource reference failed: {e.Message}");
        };
        DisplayPass? display = null;
        try
        {
            display = await DisplayPass.BeginAsync(1600, 1000);
            result["display"] = display.How;
            AppLog.Write($"self-test: the screen {display.How}");

            // This machine's sessions and the other machine's, through the demo's stand-in for the relay.
            var expected = demo.Count + remote.Count;
            result["expectedCards"] = expected;
            var drawn = await Until(() => Rows(window).Count == expected, TimeSpan.FromSeconds(60));
            var rows = Rows(window);
            result["drawnCards"] = rows.Count;
            Check(drawn, $"draws all {expected} demo sessions of both machines as rows");
            Check(main.Waiting == 4, "counts four sessions waiting on you across both machines");
            Check(main.ShowMachines && main.Machines.Any(m => m.Label.StartsWith(remote.MachineName, StringComparison.Ordinal)), "offers the other machine in the machine picker");
            var tabs = ById<SelectorBar>(window.RootElement, "MachinePicker");
            Check(tabs is { Items.Count: var n } && n == main.Machines.Count && tabs.Items.Any(i => i.Text == remote.MachineName) && tabs.Items[0].Text == "All",
                $"offers the machines as tabs over the list ({string.Join(", ", tabs?.Items.Select(i => i.Text) ?? [])})");
            var remoteFacts = rows.FirstOrDefault(r => r.Card?.Id == "demo-remote-codex") is { } rc ? ById<TextBlock>(rc, "RowFacts") : null;
            Check(remoteFacts is not null && Text(remoteFacts).EndsWith(" · " + remote.MachineName, StringComparison.Ordinal), $"names the machine on another machine's row ({(remoteFacts is null ? "none" : Text(remoteFacts))})");
            await CheckRowAnatomy(rows, Check);

            // A session chosen in the list is the session pane's, with its question's controls.
            var approval = await Choose(window, main, "demo-approval", "Option:approve");
            Check(approval is not null && ById<Button>(approval, "Option:approve") is { IsEnabled: true } && ById<Button>(approval, "Option:deny") is not null,
                "shows Approve once and Deny on a Copilot permission prompt");
            var form = await Choose(window, main, "02d9e5d5-6b35-49bd-b083-39572fbff648", "SendAnswer");
            Check(form is not null && ById<Button>(form, "SendAnswer") is not null && ById<TextBox>(form, "Field:notes") is not null,
                "draws a form question with its fields and Send");
            var withResponse = main.AllCards.FirstOrDefault(c => c.HasResponse);
            var responding = withResponse is null ? null : await Choose(window, main, withResponse.Id, "Response");
            Check(responding is not null && await Until(() => ById<FrameworkElement>(responding, "Response") is MarkdownView { Children.Count: > 0 }, TimeSpan.FromSeconds(5)), "renders a response's Markdown");
            // Status is a glyph and words (docs/design/ui.md §2.2), the glyph drawn from the generated
            // styles and named as its row reads (§9).
            var glyphs = rows.Select(r => (r.Card, Glyph: ById<StatusGlyphView>(r, "StatusGlyph"))).ToList();
            var unnamed = glyphs.Where(g => g.Glyph is null || g.Card is null || AutomationProperties.GetName(g.Glyph) != g.Card.GlyphLabel || Find<Microsoft.UI.Xaml.Shapes.Path>(g.Glyph).Count == 0).Select(g => g.Card?.Id ?? "?").ToList();
            Check(glyphs.Count > 0 && unnamed.Count == 0, $"draws every row's status glyph, named with its state{(unnamed.Count > 0 ? $": not {string.Join(", ", unnamed)}" : "")}");
            result["glyphs"] = glyphs.Select(g => $"{g.Card?.Glyph}{(g.Card?.Driven == true ? "+driven" : "")}: {g.Card?.StatusText}").ToList();
            var navIcons = Find<ContentControl>(window.RootElement).Where(c => AutomationProperties.GetAutomationId(c) == "NavIcon").ToList();
            Check(navIcons.Count >= 3 && navIcons.All(i => Find<Microsoft.UI.Xaml.Shapes.Path>(i).FirstOrDefault()?.Stroke is SolidColorBrush { Color.A: > 0 }),
                $"draws the navigation's design icons in a visible colour ({navIcons.Count} found)");
            CheckDesign(window, Check);
            await CheckInlineMono(window, Check);
            await CheckShell(window, main, Check);
            await CheckStatusLines(window, main, Check);
            await CheckCurrentTools(window, main, Check);

            // Needs you (#165): every question on both machines, its count the one the tray shows,
            // and Approve once and Deny right there for the permission prompt.
            var lane = ById<FrameworkElement>(window.RootElement, "NeedsYou");
            // Waited for: the lane's entries are drawn a layout pass after its list changes, which
            // can come after the cards are.
            List<Grid> Entries() => lane is null ? [] : Find<Grid>(lane).Where(g => AutomationProperties.GetAutomationId(g) == "NeedsYouEntry").ToList();
            await Until(() => Entries().Count == 4, TimeSpan.FromSeconds(10));
            var entries = Entries();
            result["needsYou"] = main.NeedsYou.Select(e => $"{e.Card.Id}: {e.Mode}").ToList();
            Check(lane is { Visibility: Visibility.Visible } && entries.Count == 4 && main.NeedsYou.Count == main.Waiting, "lists all four questions in Needs you, as many as the tray counts");
            Check(entries.Count == 4 && entries.All(g => ById<StatusGlyphView>(g, "NeedsYouGlyph") is { } v && Find<ContentControl>(v).Any()), "marks every Needs you entry with the Needs you glyph");
            // Every entry's template has the buttons, collapsed and disabled where it offers none:
            // exactly one of each is live, the permission prompt's.
            int Live(string id) => lane is null ? 0 : Find<Button>(lane).Count(b => AutomationProperties.GetAutomationId(b) == id && b.IsEnabled);
            var offered = await Until(() => Live("NeedsYouApprove") == 1 && Live("NeedsYouDeny") == 1, TimeSpan.FromSeconds(10));
            result["needsYouButtons"] = lane is null ? null : Find<Button>(lane).Select(b => $"{AutomationProperties.GetAutomationId(b)}: {(b.IsEnabled ? "enabled" : "disabled")}, {b.Visibility}").ToList();
            Check(offered && main.NeedsYou.SingleOrDefault(e => e.Card.Id == "demo-approval") is { Approvable: true }, "offers Approve once and Deny in Needs you for the permission prompt");
            if (lane is not null) { await Picture(lane, Path.ChangeExtension(outFile, ".needs-you.png")); }
            // ui.md §6.1: the list's first section, above Pinned and Sessions, inside the list pane;
            // an entry opens its session in the session pane.
            double Top(FrameworkElement e) => e.TransformToVisual(window.RootElement).TransformPoint(default).Y;
            var listPane = ById<FrameworkElement>(window.Sessions, "ListPane");
            var firstRow = Rows(window).OrderBy(Top).FirstOrDefault();
            var inPane = lane is not null && listPane is not null && Above<FrameworkElement>(lane, e => e == listPane) is not null
                && lane.TransformToVisual(listPane).TransformPoint(new Windows.Foundation.Point(lane.ActualWidth, 0)).X <= listPane.ActualWidth + 0.5;
            Check(inPane && firstRow is not null && Top(lane!) < Top(firstRow), "puts Needs you at the top of the list pane, above the rows");
            var formEntry = Entries().FirstOrDefault(g => main.NeedsYou.ElementAtOrDefault(Entries().IndexOf(g))?.Card.Id == "02d9e5d5-6b35-49bd-b083-39572fbff648");
            if (formEntry is not null && ById<HyperlinkButton>(formEntry, "NeedsYouOpen") is { } open)
            {
                main.Selected = null;
                ((IInvokeProvider)new HyperlinkButtonAutomationPeer(open).GetPattern(PatternInterface.Invoke)).Invoke();
                var opened = await Until(() => main.Selected?.Id == "02d9e5d5-6b35-49bd-b083-39572fbff648" && window.Sessions.SessionPaneView.Card == main.Selected && window.Sessions.Rows.SelectedItem == main.Selected, TimeSpan.FromSeconds(5));
                Check(opened, "opens a Needs you entry's session in the session pane, its row selected");
                // Keyboard focus goes with it, into the question, so the next Tab is in the session.
                bool InSession() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(window.RootElement.XamlRoot) is DependencyObject f && Above<FrameworkElement>(f, e => e == window.Sessions.SessionPaneView) is not null;
                var focused = await Until(() => main.Selected is { WantsFocus: false } && InSession(), TimeSpan.FromSeconds(5));
                Check(focused, "takes keyboard focus into the session a Needs you entry opens");
            }
            else { Check(false, "finds the form question's Needs you entry"); }
            // Alt+Down and Alt+Up on a row (§9, #164): the row moves, perhaps drawn by another
            // control, and its list item has focus again, so the next move is the same row's. A row
            // other than the open one, as from its menu: the session open stays open.
            var movable = Rows(window).Where(r => r.Card is { Row.CanMoveDown: true } c && c != main.Selected).OrderBy(Top).FirstOrDefault();
            if (movable?.Card is { } moving)
            {
                bool RowFocused() => Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(window.RootElement.XamlRoot) is ListViewItem item && ReferenceEquals(item.Content, moving);
                var openBefore = main.Selected;
                movable.MoveBy(1);
                var down = await Until(() => !moving.WantsRowFocus && RowFocused(), TimeSpan.FromSeconds(5));
                Rows(window).FirstOrDefault(r => r.Card == moving)?.MoveBy(-1);
                var up = await Until(() => !moving.WantsRowFocus && RowFocused(), TimeSpan.FromSeconds(5));
                Check(down && up, "keeps keyboard focus on a row moved down and back up, so the next Alt+Up/Down moves the same row");
                await Task.Delay(300);
                Check(main.Selected == openBefore && openBefore != moving, $"leaves the open session open while another row moves and takes focus ({main.Selected?.Id ?? "none"})");
            }
            else { Check(false, "finds a row that can move down"); }
            // Keyboard focus put on a row by itself opens nothing (SingleSelectionFollowsFocus is
            // off); once an arrow key has moved it there, the row with focus is opened.
            var ordered = Rows(window).OrderBy(Top).ToList();
            var target = ordered.FirstOrDefault(r => r.Card != main.Selected);
            if (target?.Card is { } reached && Above<ListViewItem>(target, _ => true) is { } targetItem)
            {
                var openNow = main.Selected;
                targetItem.Focus(FocusState.Keyboard);
                await Task.Delay(300);
                var stayed = main.Selected == openNow;
                window.Sessions.FollowFocus();
                var opened = await Until(() => main.Selected == reached, TimeSpan.FromSeconds(5));
                Check(stayed && opened, $"opens nothing for a row only given focus, and the row an arrow key reached (focus alone: {(stayed ? "opened nothing" : "opened its row")}; after the key: {main.Selected?.Id ?? "none"})");
            }
            else { Check(false, "finds a row other than the open one"); }            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".window.png"));
            await Picture(window.Sessions, Path.ChangeExtension(outFile, ".sessions.png"));

            // Answer the single-choice question with one click, as a person would.
            var choice = await Choose(window, main, "demo-choice", "Option:pg");
            if (choice?.Card is { } choiceCard && ById<Button>(choice, "Option:pg") is { } pg)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(pg).GetPattern(PatternInterface.Invoke)).Invoke();
                var answered = await Until(() => choiceCard.ReceiptText.StartsWith("Your answer: Delivered", StringComparison.Ordinal), TimeSpan.FromSeconds(20));
                result["receipt"] = choiceCard.ReceiptText;
                Check(answered, "answers a question from its session and shows it was delivered");
                // The receipt is laid out a pass after its text changes (#247): waited for, then the
                // same condition checked, so a session that closes or a receipt that never shows
                // still fails.
                TextBlock? Receipt() => ById<TextBlock>(window.Sessions.SessionPaneView, "Receipt");
                bool ReceiptShown() => main.Selected == choiceCard && Receipt() is { Visibility: Visibility.Visible, ActualHeight: > 0 } r && r.Text == choiceCard.ReceiptText;
                await Until(ReceiptShown, TimeSpan.FromSeconds(5));
                var receipt = Receipt();
                Check(ReceiptShown(), $"keeps the answered session open, its receipt shown{(main.Selected == choiceCard ? "" : $" (open: {main.Selected?.Id ?? "none"})")}{(receipt is null ? " (no receipt)" : $" (receipt: {receipt.Visibility}, {receipt.ActualHeight:0.#} px)")}");
            }
            else { Check(false, "finds the single-choice question's option button"); }

            // Answer a question on the other machine the same way: it goes to that machine's host.
            var remoteChoice = await Choose(window, main, "demo-remote-claude", "Option:device");
            if (remoteChoice?.Card is { } remoteCard && ById<Button>(remoteChoice, "Option:device") is { } device)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(device).GetPattern(PatternInterface.Invoke)).Invoke();
                var answered = await Until(() => remoteCard.ReceiptText.StartsWith("Your answer: Delivered", StringComparison.Ordinal), TimeSpan.FromSeconds(20));
                result["remoteReceipt"] = remoteCard.ReceiptText;
                Check(answered, "answers a question on another machine from its session and shows it was delivered");
            }
            else { Check(false, "finds the other machine's question's option button"); }

            // The machine tabs narrow the list to one machine; the session open on another machine
            // closes with its row.
            var watched = main.AllCards.First(c => !c.Id.StartsWith("demo-remote", StringComparison.Ordinal) && c.Id != "demo-approval");
            main.Selected = main.AllCards.First(c => c.Id == "demo-approval");
            main.SelectedMachine = main.Machines.First(m => m.Key == DemoHost.RemoteId);
            var narrowed = await Until(() => Rows(window).Count == remote.Count, TimeSpan.FromSeconds(10));
            Check(narrowed && tabs?.SelectedItem?.Text == remote.MachineName, "shows only the chosen machine's rows, its tab selected");
            Check(main.Selected is null, "closes a session whose row the machine tabs hide");
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".remote.png"));
            await Task.Delay(500);
            var hidden = Listeners(watched);
            var hiddenBy = ListenersBy(watched);
            main.SelectedMachine = main.Machines[0];
            await Until(() => Rows(window).Count == expected, TimeSpan.FromSeconds(10));
            await Task.Delay(500);
            var shown = Listeners(watched);
            var shownBy = ListenersBy(watched);

            // Back and forth: each time the rows get new controls, and the old ones let go.
            for (var i = 0; i < 5; i++)
            {
                main.SelectedMachine = main.Machines.First(m => m.Key == DemoHost.RemoteId);
                await Until(() => Rows(window).Count == remote.Count, TimeSpan.FromSeconds(10));
                main.SelectedMachine = main.Machines[0];
                await Until(() => Rows(window).Count == expected, TimeSpan.FromSeconds(10));
            }
            // A leak grows with each change; the count read as shown can also include a recycled
            // control not yet unloaded (4 shown, 2 settled, seen once in four runs), so it is a ceiling.
            var settled = await Until(() => Listeners(watched) is var n && n <= shown && n > hidden, TimeSpan.FromSeconds(10));
            var again = await Choose(window, main, "demo-approval", "Option:approve");
            Check(again is not null && ById<Button>(again, "Option:approve") is { IsEnabled: true }, "still shows a question's choices on a session chosen again after the tabs change");
            Check(settled && shown > hidden && hidden >= 0, $"a row's control listens while shown and lets go when hidden, however often the tabs change ({hidden} listeners hidden: {hiddenBy}; {shown} shown: {shownBy}; {Listeners(watched)} after five more changes: {ListenersBy(watched)})");

            await CheckPanes(window, main, Check, outFile);
            await CheckDelegates(window, main, Check, outFile);

            // Launch a session from the Launch page: it starts, its row appears, and Open goes to it.
            window.ShowPage("launch");
            var ready = await Until(() => launch.ShowForm && launch.Machines.Count == 2 && ById<Button>(window.RootElement, "LaunchButton") is { IsEnabled: true }, TimeSpan.FromSeconds(10));
            Check(ready, "offers both machines on the Launch page");
            // Wider than a page's column, which is where they once spilled off the right edge.
            Resize(window, 1000, 900);
            await Task.Delay(700);
            Check(Fits(window, "LaunchPane"), "fits the Launch page's column in a wide window");
            var before = main.AllCards.Count();
            launch.Prompt = "Summarize the open pull requests.";
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".launch.png"));
            if (ById<Button>(window.RootElement, "LaunchButton") is { } go)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(go).GetPattern(PatternInterface.Invoke)).Invoke();
                var started = await Until(() => launch.State == "started" && main.AllCards.Count() == before + 1, TimeSpan.FromSeconds(20));
                result["launchStatus"] = launch.StatusText;
                Check(started, "launches a session from the Launch page and shows its row");
            }
            else { Check(false, "finds the Launch button"); }
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".launched.png"));

            // Codex on the other machine starts only on a first message: nothing is sent without one.
            // The new session opened on the Sessions page: back to Launch, which fetches its choices.
            window.ShowPage("launch");
            launch.SelectedMachine = launch.Machines.First(m => m.Value == DemoHost.RemoteId);
            await Until(() => launch.Title.EndsWith(remote.MachineName, StringComparison.Ordinal) && launch.Agents.Any(a => a.Value == "codex"), TimeSpan.FromSeconds(10));
            launch.Agent = launch.Agents.FirstOrDefault(a => a.Value == "codex");
            launch.Prompt = "";
            launch.Launch();
            Check(launch.Problem.StartsWith("Not launched: Codex starts its session only on a first message", StringComparison.Ordinal), "refuses to launch Codex on another machine without a first message");

            window.ShowPage("usage");
            await Task.Delay(500);
            Check(main.Usage.Rows.Count == 3, "shows usage for three agents across both machines");
            Check(Fits(window, "UsagePane"), "fits the Usage page's column in a wide window");
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".usage.png"));
            window.ShowPage("settings");
            await Task.Delay(500);
            Check(Fits(window, "SettingsPane"), "fits the Settings page's column in a wide window");
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".settings.png"));

            // Approve once from Needs you, as a person would: the card's own answer goes, and the
            // entry leaves with its question.
            window.ShowPage("sessions");
            await Task.Delay(500);
            var waiting = main.Waiting;
            var approveButton = ById<FrameworkElement>(window.RootElement, "NeedsYou") is { } laneNow
                ? Find<Button>(laneNow).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "NeedsYouApprove" && b.IsEnabled) : null;
            if (approveButton is not null)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(approveButton).GetPattern(PatternInterface.Invoke)).Invoke();
                var approvalCard = main.AllCards.First(c => c.Id == "demo-approval");
                var gone = await Until(() => approvalCard.ReceiptText.StartsWith("Your answer: Delivered", StringComparison.Ordinal) && main.NeedsYou.All(e => e.Card.Id != "demo-approval") && main.Waiting == waiting - 1, TimeSpan.FromSeconds(20));
                result["laneReceipt"] = approvalCard.ReceiptText;
                Check(gone, "approves a permission prompt from Needs you: the card shows it delivered, and the entry and the tray count go down");
            }
            else { Check(false, "finds Approve once in Needs you"); }
            // The search box finds a session on any machine and opens it, from any page.
            var sought = main.AllCards.First(c => c.Id == "demo-remote-claude");
            window.SearchFor(sought.Title);
            var hits = window.Search.ItemsSource as IReadOnlyList<SearchHit> ?? [];
            Check(hits.Any(h => h.SessionId == sought.Id && h.Machine == DemoHost.RemoteId), $"finds another machine's session by its title in the search box ({hits.Count} found)");
            main.Selected = null;
            if (hits.FirstOrDefault(h => h.SessionId == sought.Id) is { } hit) { window.OpenHit(hit); }
            await Task.Delay(500);
            Check(window.CurrentPage == "sessions" && main.Selected == sought && window.Sessions.Rows.SelectedItem == sought && window.Search.Text.Length == 0,
                "opens a session chosen in the search box on the Sessions page, its row selected");

            // Every page, and every pane, in every theme: three panes, with a delegate in the strip and
            // the inspector, and the session's Details and Tools.
            Resize(window, 1440, 1000);
            main.Selected = main.AllCards.First(c => c.Id == "demo-approval");
            await Task.Delay(700);
            await CheckMachinesPage(window, main, Check, outFile);
            await CheckNarrowest(window, Check, outFile);
            // The theme passes draw the Sessions page with a delegate in its strip and inspector.
            await ShowADelegate(window, main, Check);
            await CheckPagesInEveryTheme(window, Check, outFile);
            // What was seen so far, written down first: a failure in high contrast that ends the
            // process (a XAML failure shows only as 0xC000027B) leaves the rest on record.
            await Write(outFile, result, checks, failures, "high contrast");
            await CheckHighContrast(window, Check, outFile, result);
        }
        catch (Exception e)
        {
            failures.Add($"crashed: {e}");
        }
        if (display is not null) { await display.EndAsync(); }
        // Written again if a failure arrived while it was written (a resource reference failing as
        // a theme settles), so the file always says why the run failed.
        int written;
        do
        {
            written = failures.Count;
            await Write(outFile, result, checks, failures, null);
        }
        while (failures.Count != written);
        var code = failures.Count == 0 ? 0 : 1;
        AppLog.Write($"self-test: finished, exit code {code}");
        exit(code);
    }

    // The result file. unfinished: the check under way, when written part-way through.
    static async Task Write(string outFile, Dictionary<string, object?> result, List<string> checks, List<string> failures, string? unfinished)
    {
        result["passed"] = checks;
        result["failed"] = failures;
        result["unfinished"] = unfinished;
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    // The design's generated resources (Design/DesignResources.xaml, tools/design-gen): every brush
    // in every theme, and every glyph and icon drawn. A key missing from one theme would fail only
    // on a machine using that theme, and a resource that fails to load shows only as 0xC000027B.
    // The list's rows, one per listed session: a recycled row control off screen is not one.
    static List<SessionRow> Rows(MainWindow window) =>
        [.. Find<SessionRow>(window.Sessions.Rows).Where(r => r.Card is { } c && window.ViewModel.Sessions.Contains(c) && r.ActualHeight > 0).DistinctBy(r => r.Card)];

    // A TextBlock's words, its runs' included.
    static string Text(TextBlock block) => block.Inlines.Count == 0 ? block.Text : string.Concat(block.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Select(r => r.Text));

    // A session chosen, as a click on its row chooses it, and the session pane once it shows it
    // with the part asked for.
    static async Task<SessionView?> Choose(MainWindow window, MainViewModel main, string id, string part)
    {
        if (main.AllCards.FirstOrDefault(c => c.Id == id) is not { } card) { return null; }
        window.ShowPage("sessions");
        if (!main.Sessions.Contains(card)) { main.SelectedMachine = main.Machines[0]; }
        window.Sessions.Rows.SelectedItem = card;
        var view = window.Sessions.SessionPaneView;
        var shown = await Until(() => main.Selected == card && view.Card == card && ById<FrameworkElement>(view, part) is not null, TimeSpan.FromSeconds(10));
        return shown ? view : null;
    }

    // A row is ui.md §6.1's two lines (#214): the glyph on line 1, never centred on the row; the
    // title, semibold only for unread activity, then the dot or the age; and line 2, at most two
    // facts on one line, so no separator starts a line, and never the model, counts or the pid.
    static async Task CheckRowAnatomy(List<SessionRow> rows, Action<bool, string> check)
    {
        await Task.Delay(200);
        var crooked = new List<string>();
        var facts = new List<string>();
        foreach (var row in rows)
        {
            if (row.Card is not { } card) { continue; }
            double Top(FrameworkElement? e) => e is null ? -100 : e.TransformToVisual(row).TransformPoint(default).Y;
            var glyph = ById<StatusGlyphView>(row, "StatusGlyph");
            var title = ById<TextBlock>(row, "RowTitle");
            var line2 = ById<TextBlock>(row, "RowFacts");
            var text = line2 is null ? "" : Text(line2);
            facts.Add(text);
            var onLine1 = glyph is not null && title is not null && Math.Abs(Top(glyph) + glyph.ActualHeight / 2 - (Top(title) + title.ActualHeight / 2)) <= 1;
            var oneLine = line2 is not null && line2.ActualHeight <= line2.FontSize * 2 && Top(line2) > Top(title);
            var weight = title?.FontWeight.Weight == (card.Unread ? Microsoft.UI.Text.FontWeights.SemiBold.Weight : Microsoft.UI.Text.FontWeights.Normal.Weight);
            var never = card.Session.Model is { Length: > 0 } model && text.Contains(model, StringComparison.Ordinal)
                || card.Session.ProcessId is { } pid && text.Contains(pid.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (!onLine1 || !oneLine || !weight || never || text.Split(" · ").Length > 2 || text.StartsWith('·') || text.Length == 0) { crooked.Add(card.Id); }
        }
        check(rows.Count > 0 && crooked.Count == 0, $"draws every row as two lines: the glyph on line 1, then at most two facts on one line, never the model or the pid{(crooked.Count > 0 ? $": not {string.Join(", ", crooked)}" : "")} ({string.Join(" | ", facts)})");
    }

    // The status words in their tone's text colour on every row and in the session's header, and
    // inside the row. Then the window's theme flipped: the words and the glyphs must take the other
    // theme's colours without a session update, as the rows behind them do; a brush looked up once
    // kept the old theme's, dark on dark.
    static async Task CheckStatusLines(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        static string Colour(object? b) => b is SolidColorBrush s ? s.Color.ToString() : "none";
        var view = window.Sessions.SessionPaneView;
        var lines = Rows(window).Where(r => r.Card is not null)
            .Select(r => (Control: (UserControl)r, Card: r.Card!, Words: r.FindName("StatusWords") as Microsoft.UI.Xaml.Documents.Run, Line: ById<TextBlock>(r, "RowFacts"), Glyph: ById<StatusGlyphView>(r, "StatusGlyph")))
            .ToList();
        if (view.Card is { } open)
        {
            lines.Add((view, open, view.FindName("StatusWords") as Microsoft.UI.Xaml.Documents.Run, ById<TextBlock>(view, "HeaderMeta"), ById<StatusGlyphView>(view, "StatusGlyph")));
        }
        List<string> Wrong(Func<StatusTone, object?> expected) => [.. lines.Where(l => l.Words is null || Colour(l.Words.Foreground) != Colour(expected(l.Card.StatusTone))).Select(l => l.Card.Id)];
        var wrong = Wrong(Ui.WordsBrush);
        check(lines.Count > 1 && wrong.Count == 0, $"sets every status's words in its tone's text colour, on rows and in the header{(wrong.Count > 0 ? $": not {string.Join(", ", wrong)}" : "")}");

        var outside = lines.Where(l => l.Line is not { } w || w.TransformToVisual(l.Control).TransformPoint(new Windows.Foundation.Point(w.ActualWidth, 0)).X > l.Control.ActualWidth + 0.5).Select(l => l.Card.Id).ToList();
        check(outside.Count == 0, $"keeps every status's words inside its row{(outside.Count > 0 ? $": not {string.Join(", ", outside)}" : "")}");

        // High contrast keeps its own dictionary whatever the window asks for: nothing to flip.
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) { return; }
        var design = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.ToString().EndsWith(Design.DesignKeys.Source["ms-appx:///".Length..], StringComparison.OrdinalIgnoreCase) == true);
        var root = window.RootElement;
        var flip = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        if (design?.ThemeDictionaries.TryGetValue(flip.ToString(), out var d) != true || d is not ResourceDictionary themed) { check(false, $"finds the {flip} theme's brushes"); return; }
        object? Themed(string key) => themed.TryGetValue(key, out var b) ? b : null;
        root.RequestedTheme = flip;
        try
        {
            await Until(() => lines.All(l => l.Control.ActualTheme == flip), TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            var stale = Wrong(t => Themed(Ui.WordsKey(t)));
            check(stale.Count == 0, $"turns every status's words to the {flip} theme's colour without an update{(stale.Count > 0 ? $": not {string.Join(", ", stale)}" : "")}");
            var staleGlyphs = lines.Where(l => l.Glyph is null || Find<ContentControl>(l.Glyph).FirstOrDefault() is not { } shape || Colour(shape.Foreground) != Colour(Themed($"Ab{StatusGlyphView.RoleOf(l.Card.Glyph, l.Card.Driven)}"))).Select(l => l.Card.Id).ToList();
            check(staleGlyphs.Count == 0, $"turns every status glyph to the {flip} theme's colour without an update{(staleGlyphs.Count > 0 ? $": not {string.Join(", ", staleGlyphs)}" : "")}");
        }
        finally
        {
            root.RequestedTheme = ElementTheme.Default;
            await Until(() => lines.All(l => l.Control.ActualTheme != flip), TimeSpan.FromSeconds(10));
            await Task.Delay(300);
        }
    }

    // What a session is doing now, in its conversation: the tool's glyph named for its state
    // (ui.md §9), the call in mono beside it.
    static async Task CheckCurrentTools(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        var tools = main.AllCards.Where(c => c.HasTool).ToList();
        var unnamed = new List<string>();
        foreach (var card in tools)
        {
            var view = await Choose(window, main, card.Id, "ToolGlyph");
            var glyph = view is null ? null : ById<StatusGlyphView>(view, "ToolGlyph");
            var call = view is null ? null : ById<TextBlock>(view, "CurrentTool");
            if (glyph is null || AutomationProperties.GetName(glyph) != card.ToolState || AutomationProperties.GetAccessibilityView(glyph) != AccessibilityView.Content
                || call?.FontFamily.Source != ((Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["MonoFontFamily"]).Source)
            {
                unnamed.Add(card.Id);
            }
        }
        check(tools.Count > 0 && unnamed.Count == 0, $"names every current tool's glyph with its state, the call in mono{(unnamed.Count > 0 ? $": not {string.Join(", ", unnamed)}" : "")}");
    }

    // The panes by window width (ui.md §5.2, #180), resized live across the breakpoints: three from
    // 1280, two with tabs from 760, one column below; a 400×900 window shows one column; the session
    // open stays open through every resize.
    static async Task CheckPanes(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile)
    {
        window.ShowPage("sessions");
        var page = window.Sessions;
        var layout = page.Layout!;
        var card = main.AllCards.First(c => c.Id == "demo-approval");
        if (!main.Sessions.Contains(card)) { main.SelectedMachine = main.Machines[0]; }
        main.Selected = card;
        FrameworkElement? Pane(string id) => ById<FrameworkElement>(page, id);
        static bool Shows(FrameworkElement? e) => e is { Visibility: Visibility.Visible, ActualWidth: > 0 };
        double Width(string id) => Pane(id)?.ActualWidth ?? 0;

        async Task<bool> At(double width, double height, PaneMode mode)
        {
            Resize(window, width, height);
            return await Until(() => layout.Mode == mode && Math.Abs(window.RootElement.XamlRoot.Size.Width - window.RootElement.ActualWidth) < 1, TimeSpan.FromSeconds(10)) && await Settle();
        }
        static async Task<bool> Settle() { await Task.Delay(500); return true; }

        var list = (double)Application.Current.Resources["AbLayoutList"];
        var listMin = (double)Application.Current.Resources["AbLayoutListMin"];
        var inspector = (double)Application.Current.Resources["AbLayoutInspector"];

        var three = await At(1440, 900, PaneMode.Three);
        check(three && Shows(Pane("ListPane")) && Math.Abs(Width("ListPane") - list) < 1 && Shows(Pane("SessionPane")) && Shows(Pane("Inspector")) && Math.Abs(Width("Inspector") - inspector) < 1
            && Shows(Pane("InspectorDetails")) && Shows(Pane("InspectorTools")) && !Shows(Pane("SessionTabs")),
            $"shows the list ({Width("ListPane"):0}), the session and the inspector ({Width("Inspector"):0}) with Details and Tools at 1440 px");
        var detail = ById<TextBlock>(page, "Detail:ID");
        check(detail is not null && detail.FontFamily.Source == ((Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["MonoFontFamily"]).Source && ById<Button>(page, "EndSession") is not null,
            "lists the session's details in the inspector, its ID in mono, with End session");
        // A path cut in the middle to the width it has, its end kept (#214).
        var folder = ById<TextBlock>(page, "Detail:Folder");
        var whole = card.Session.Cwd ?? "";
        var cutWell = folder is not null && (folder.Text == whole || folder.Text.Split('\u2026') is [var head, var tail] && whole.StartsWith(head, StringComparison.Ordinal) && whole.EndsWith(tail, StringComparison.Ordinal) && tail.Length > 0)
            && folder.ActualWidth > 0 && !folder.IsTextTrimmed;
        check(whole.Length == 0 || cutWell, $"cuts a session's folder in Details in the middle, to its width ({folder?.Text ?? "none"})");
        await Picture(window.RootElement, Path.ChangeExtension(outFile, ".panes3.png"));
        if (ById<Button>(page.SessionPaneView, "ToggleInspector") is { } toggle)
        {
            ((IInvokeProvider)new ButtonAutomationPeer(toggle).GetPattern(PatternInterface.Invoke)).Invoke();
            var gone = await Until(() => !Shows(Pane("Inspector")), TimeSpan.FromSeconds(5));
            ((IInvokeProvider)new ButtonAutomationPeer(toggle).GetPattern(PatternInterface.Invoke)).Invoke();
            var back = await Until(() => Shows(Pane("Inspector")), TimeSpan.FromSeconds(5));
            check(gone && back, "hides the inspector and shows it again from the session's header");
        }
        else { check(false, "finds the inspector's toggle in the session's header"); }

        var two = await At(1000, 900, PaneMode.Two);
        check(two && Shows(Pane("ListPane")) && Width("ListPane") >= listMin - 1 && Width("ListPane") <= list + 1 && Shows(Pane("SessionPane")) && !Shows(Pane("Inspector")) && Shows(Pane("SessionTabs")),
            $"shows the list ({Width("ListPane"):0}) and the session with its tabs, and no inspector, at 1000 px");
        if (ById<SelectorBar>(page, "SessionTabs") is { } bar && bar.Items.FirstOrDefault(i => (string)i.Tag == "Tools") is { } toolsTab)
        {
            bar.SelectedItem = toolsTab;
            var tools = await Until(() => Shows(Pane("ToolsPanel")) && !Shows(Pane("Conversation")) && card.HistoryOpen, TimeSpan.FromSeconds(5));
            check(tools, "shows the session's tools in its Tools tab, loading them as it opens");
            bar.SelectedItem = bar.Items[0];
            await Until(() => Shows(Pane("Conversation")), TimeSpan.FromSeconds(5));
        }
        else { check(false, "finds the session's Tools tab"); }
        await Picture(window.RootElement, Path.ChangeExtension(outFile, ".panes2.png"));

        var one = await At(400, 900, PaneMode.One);
        check(one && !Shows(Pane("ListPane")) && Shows(Pane("SessionPane")) && Shows(ById<FrameworkElement>(page.SessionPaneView, "Back")) && main.Selected == card,
            "shows one column at 400×900: the session open, with a way back to the list");
        await Picture(window.RootElement, Path.ChangeExtension(outFile, ".panes1.png"));
        if (ById<Button>(page.SessionPaneView, "Back") is { } backButton)
        {
            ((IInvokeProvider)new ButtonAutomationPeer(backButton).GetPattern(PatternInterface.Invoke)).Invoke();
            var listed = await Until(() => Shows(Pane("ListPane")) && !Shows(Pane("SessionPane")) && main.Selected is null, TimeSpan.FromSeconds(5));
            check(listed && Math.Abs(Width("ListPane") - window.RootElement.XamlRoot.Size.Width) < 80, $"goes back to the list, the window's width ({Width("ListPane"):0} px)");
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".panes1list.png"));
        }
        else { check(false, "finds Back in one column"); }

        // The session open survives every breakpoint, both ways.
        main.Selected = card;
        var kept = true;
        foreach (var (w, mode) in new[] { (1000.0, PaneMode.Two), (1440.0, PaneMode.Three), (700.0, PaneMode.One), (1300.0, PaneMode.Three), (800.0, PaneMode.Two) })
        {
            kept &= await At(w, 900, mode) && main.Selected == card && page.Rows.SelectedItem == card && page.SessionPaneView.Card == card && Shows(Pane("SessionPane"));
        }
        check(kept, "keeps the session open, and its row selected, through every breakpoint and back");
    }
    // The Windows shell (ui.md §7, #180): the 48 px title bar with the mark, the name and the search
    // box; the compact rail with every page and its 3×16 selection pill in Bridge; the pages on a
    // layer with an 8 px top-left radius; WinUI's accent controls in Bridge in both themes; and the
    // window no smaller than 360×600.
    static async Task CheckShell(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        static string Colour(object? b) => b is SolidColorBrush s ? s.Color.ToString() : b?.GetType().Name ?? "none";
        static string Hex(string h) => $"#FF{h.TrimStart('#')}";
        var accent = Design.DesignKeys.AccentColors.ToDictionary(a => a.Key, a => a.Color);
        var bar = window.Bar;
        check(bar.Title == "Agent Bridge" && Math.Abs(bar.ActualHeight - (double)Application.Current.Resources["AbLayoutTitleBar"]) < 0.5,
            $"draws the 48 px title bar with the app's name ({bar.ActualHeight:0.#} px)");
        var mark = ById<ContentControl>(window.RootElement, "Mark");
        check(mark is not null && Find<Microsoft.UI.Xaml.Shapes.Path>(mark).Count > 0, "draws the mark in the title bar");
        check(window.Search.XamlRoot is not null && window.Search.ActualWidth > 100 && FindAncestor<TitleBar>(window.Search) == bar, $"puts the search box in the title bar ({window.Search.ActualWidth:0} px wide)");
        check(window.RootElement is Grid { KeyboardAccelerators: var keys } && keys.Any(k => k.Key == Windows.System.VirtualKey.K && k.Modifiers == Windows.System.VirtualKeyModifiers.Control), "focuses search with Ctrl+K");
        // #240: with any other theme, Windows App SDK repaints the caption buttons from a thread-pool
        // thread on every colour change, and a high-contrast switch crashed the app in dcomp.dll.
        check(window.AppWindow.TitleBar.PreferredTheme == Microsoft.UI.Windowing.TitleBarTheme.Legacy,
            $"paints the caption buttons itself, on the UI thread, not through the title bar's PreferredTheme ({window.AppWindow.TitleBar.PreferredTheme})");

        var rail = window.Rail;
        var tags = rail.MenuItems.Concat(rail.FooterMenuItems).OfType<NavigationViewItem>().Select(i => i.Tag as string).ToList();
        check(rail.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftCompact && tags.SequenceEqual(["sessions", "machines", "launch", "usage", "settings"])
            && rail.FooterMenuItems.OfType<NavigationViewItem>().Single().Tag as string == "settings",
            $"has a compact rail of Sessions, Machines, Launch and Usage, with Settings at its foot ({string.Join(", ", tags)})");
        // NavigationView draws the pages' layer with the radius in its left modes, not in its top mode.
        // Found from the page's side: each rail item's template has a ContentGrid of its own, first.
        var layer = ById<Grid>(rail, "Content") is { } content ? Above<Grid>(content, g => g.Name == "ContentGrid") : null;
        var radius = ((CornerRadius)Application.Current.Resources["AbRadiusCard"]).TopLeft;
        check(layer is { CornerRadius: var r } && Math.Abs(r.TopLeft - radius) < 0.1 && r.TopRight == 0 && r.BottomLeft == 0, $"draws the pages on a layer with an {radius} px top-left radius ({(layer is null ? "none" : layer.CornerRadius.ToString())})");

        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) { break; }
            window.RootElement.RequestedTheme = theme;
            await Task.Delay(300);
            var want = Hex(theme == ElementTheme.Dark ? accent["SystemAccentColorLight2"] : accent["SystemAccentColorDark1"]);
            var pill = Find<Microsoft.UI.Xaml.Shapes.Rectangle>(rail).FirstOrDefault(x => x.Name == "SelectionIndicator" && x.Opacity > 0);
            check(pill is { ActualWidth: 3, ActualHeight: 16 } && Colour(pill.Fill) == want, $"marks the page shown with a 3×16 Bridge pill in {theme} ({(pill is null ? "none" : $"{pill.ActualWidth}×{pill.ActualHeight} {Colour(pill.Fill)}")}, wanted {want})");
            var caption = window.AppWindow.TitleBar;
            var wanted = MainWindow.CaptionColours(theme);
            check(wanted is { } c && caption.ButtonForegroundColor == c.Text && caption.ButtonInactiveForegroundColor == c.Inactive && caption.ButtonHoverBackgroundColor == c.Hover && caption.ButtonPressedBackgroundColor == c.Pressed,
                $"draws the caption buttons in the design's {theme} text and fills ({caption.ButtonForegroundColor?.ToString() ?? "system"}, wanted {wanted?.Text.ToString() ?? "none"})");
            // A control that follows WinUI's accent: an accent button.
            var button = new Button { Content = "Bridge", Style = (Style)Application.Current.Resources["AccentButtonStyle"], Opacity = 0, IsHitTestVisible = false };
            var root = (Panel)window.RootElement;
            root.Children.Add(button);
            try
            {
                button.UpdateLayout();
                check(Colour(button.Background) == want, $"draws WinUI's accent button in Bridge in {theme}, not the Windows accent ({Colour(button.Background)}, wanted {want})");
            }
            finally { root.Children.Remove(button); }
        }
        window.RootElement.RequestedTheme = ElementTheme.Default;
        // Back in the app's own theme before anything else reads a colour: the status words are
        // compared with the app's brushes next.
        var own = Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        await Until(() => window.RootElement.ActualTheme == own, TimeSpan.FromSeconds(10));
        await Task.Delay(300);

        var scale = window.RootElement.XamlRoot.RasterizationScale;
        // Capped to the work area as the window's own minimum is, on a screen too short for 600 px.
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        var (w, h) = WindowPlacement.Smallest(new Display(new PixelRect(area.X, area.Y, area.Width, area.Height), scale));
        check(window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { PreferredMinimumWidth: var mw, PreferredMinimumHeight: var mh } && mw == w && mh == h,
            $"lets the window be resized down to 360×600 and no smaller ({w}×{h} px at {scale:0.##}×)");
        check(main.Machines.Count > 0, "keeps the machine picker's choices");
    }

    // The Machines page (#180, #213): a row per machine, its icon and status on line 1 with its
    // name, never centred on a taller row; no sentence or id on a row; one button style.
    static async Task CheckMachinesPage(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile)
    {
        window.ShowPage("machines");
        await Task.Delay(500);
        var rows = Find<Grid>(window.RootElement).Where(g => AutomationProperties.GetAutomationId(g) == "MachineRow" && g.ActualHeight > 0).ToList();
        check(rows.Count == main.MachineRows.Count && rows.Count >= 2, $"lists every machine on the Machines page ({rows.Count} of {main.MachineRows.Count})");
        var crooked = new List<string>();
        foreach (var row in rows)
        {
            var name = ById<TextBlock>(row, "MachineName");
            var top = name?.TransformToVisual(row).TransformPoint(default).Y ?? -1;
            double Top(string id) => ById<FrameworkElement>(row, id) is { } e ? e.TransformToVisual(row).TransformPoint(default).Y : -100;
            var status = ById<TextBlock>(row, "MachineStatus");
            var right = status is null ? double.MaxValue : status.TransformToVisual(row).TransformPoint(new Windows.Foundation.Point(status.ActualWidth, 0)).X;
            if (Math.Abs(Top("MachineIcon") - top) > 1 || Math.Abs(Top("MachineStatus") - top) > 1 || right > row.ActualWidth + 0.5) { crooked.Add(name?.Text ?? "?"); }
        }
        check(crooked.Count == 0, $"aligns each machine's icon and status with its name's line, inside the row{(crooked.Count > 0 ? $": not {string.Join(", ", crooked)}" : "")}");
        var facts = rows.Select(r => ById<TextBlock>(r, "MachineFacts")?.Text ?? "").ToList();
        check(facts.All(f => f.Split(" · ").Length <= 2 && !f.Contains("read-only", StringComparison.OrdinalIgnoreCase) && !f.EndsWith('.')), $"keeps every machine row to two facts, with no sentence ({string.Join(" | ", facts)})");
        var buttons = rows.SelectMany(r => Find<Button>(r)).Where(b => b.Visibility == Visibility.Visible && b.ActualHeight > 0).ToList();
        var heights = buttons.Select(b => Math.Round(b.ActualHeight)).Distinct().ToList();
        check(buttons.Count > 0 && buttons.All(b => b.Style == buttons[0].Style) && heights.Count == 1, $"gives every button on the Machines page one style ({buttons.Count} buttons, heights {string.Join(", ", heights)})");
        await Picture(window.RootElement, Path.ChangeExtension(outFile, ".machines.png"));
    }

    // At the smallest window (360×600, ui.md §5.2), every control on every page, the Sessions page's
    // one column included, is within the window: none cut off at the right, where nothing scrolls to
    // reach it.
    static async Task CheckNarrowest(MainWindow window, Action<bool, string> check, string outFile)
    {
        var root = window.RootElement;
        var before = window.AppWindow.Size;
        Resize(window, DesignLayout.MinWidth, DesignLayout.MinHeight);
        try
        {
            await Until(() => root.ActualWidth <= DesignLayout.MinWidth + 1, TimeSpan.FromSeconds(10));
            var cut = new List<string>();
            foreach (var (tag, pane) in Pages)
            {
                window.ShowPage(tag);
                await Task.Delay(500);
                if (ById<FrameworkElement>(root, pane) is not { } page) { cut.Add($"{tag}: no page"); continue; }
                // Every action a machine's row can offer, at once: the demo offers no updates, so
                // Check and Install never show beside Show sessions on their own. Shown here, the
                // row must wrap them, not run them off its edge.
                var hidden = tag != "machines" ? [] : Find<Panel>(page).Where(p => AutomationProperties.GetAutomationId(p) == "MachineButtons")
                    .SelectMany(p => p.Children.OfType<Button>()).Where(b => b.Visibility == Visibility.Collapsed).ToList();
                foreach (var b in hidden) { b.Visibility = Visibility.Visible; }
                try
                {
                    if (hidden.Count > 0) { page.UpdateLayout(); }
                    if (tag == "usage")
                    {
                        // #225: whose account it is, whole, however narrow: wrapped, never cut.
                        var accounts = Find<TextBlock>(page).Where(t => AutomationProperties.GetAutomationId(t) is "UsageAccount" or "UsageSummary" && Shown(t, page)).ToList();
                        var cutShort = accounts.Where(t => t.IsTextTrimmed || t.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(t.ActualWidth, 0)).X > root.ActualWidth + 1).Select(t => t.Text).ToList();
                        check(accounts.Count >= 2 && cutShort.Count == 0, $"shows every usage account and the summary whole in a {DesignLayout.MinWidth:0} px window ({accounts.Count}{(cutShort.Count > 0 ? $"; cut: {string.Join(", ", cutShort)}" : "")})");
                    }
                    if (tag == "machines")
                    {
                        var most = Find<Panel>(page).Where(p => AutomationProperties.GetAutomationId(p) == "MachineButtons").Select(p => p.Children.OfType<Button>().Count(b => Shown(b, page))).DefaultIfEmpty().Max();
                        check(most >= 4, $"draws a machine row with all its actions at once in a {DesignLayout.MinWidth:0} px window ({most} buttons)");
                    }
                    foreach (var control in Find<Control>(page).Where(c => c is Button or ComboBox or TextBox or ToggleSwitch && Shown(c, page)))
                    {
                        var right = control.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(control.ActualWidth, 0)).X;
                        if (right > root.ActualWidth + 1) { cut.Add($"{tag}: {(AutomationProperties.GetAutomationId(control) is { Length: > 0 } id ? id : control.GetType().Name)} to {right:0}"); }
                    }
                    await Picture(root, Path.ChangeExtension(outFile, $".{tag}.narrow.png"));
                }
                finally
                {
                    foreach (var b in hidden) { b.Visibility = Visibility.Collapsed; }
                }
            }
            check(cut.Count == 0, $"keeps every control on every page inside a {DesignLayout.MinWidth:0} px window{(cut.Count > 0 ? $": not {string.Join(", ", cut)}" : "")}");
        }
        finally
        {
            window.AppWindow.Resize(before);
            await Task.Delay(500);
        }
    }

    // On screen: itself and everything up to the page visible, and given room.
    static bool Shown(FrameworkElement element, FrameworkElement page)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) { return false; }
        for (DependencyObject? up = element; up is not null && up != page; up = VisualTreeHelper.GetParent(up))
        {
            if (up is UIElement { Visibility: Visibility.Collapsed }) { return false; }
        }
        return true;
    }

    static void Resize(MainWindow window, double width, double height)
    {
        var scale = window.RootElement.XamlRoot.RasterizationScale;
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale)));
    }
    // Every page loads and draws in Light and Dark, and in high contrast with system colours for
    // every role. A resource missing from one theme fails only where that theme is used, and a XAML
    // failure shows only as 0xC000027B, so each page is drawn in each. High contrast is a Windows
    // setting, not a window's: on CI the self-test turns it on for the pass and back off after.
    static readonly (string Tag, string Pane)[] Pages = [("sessions", "SessionsPage"), ("machines", "MachinesPane"), ("launch", "LaunchPane"), ("usage", "UsagePane"), ("settings", "SettingsPane")];

    static async Task<List<string>> DrawPages(MainWindow window, string theme, string outFile)
    {
        var root = window.RootElement;
        var broken = new List<string>();
        foreach (var (tag, pane) in Pages)
        {
            AppLog.Write($"self-test: the {tag} page in {theme}");
            window.ShowPage(tag);
            await Task.Delay(400);
            var page = ById<FrameworkElement>(root, pane);
            var title = page is null ? null : Find<TextBlock>(page).FirstOrDefault(t => AutomationProperties.GetHeadingLevel(t) == AutomationHeadingLevel.Level1);
            if (page is not { IsLoaded: true, Visibility: Visibility.Visible } || page.ActualHeight <= 0 || title is not { FontSize: 20, ActualHeight: > 0 }) { broken.Add(tag); }
            // The Sessions page's panes too: the session and, in three panes, its inspector.
            if (tag == "sessions" && window.Sessions.Layout is { } layout && page is not null)
            {
                var session = ById<TextBlock>(page, "SessionTitle");
                var inspector = ById<FrameworkElement>(page, "Inspector");
                if (layout.ShowSession && session is not { ActualHeight: > 0 }) { broken.Add("the session pane"); }
                if (layout.ShowInspector && inspector is not { Visibility: Visibility.Visible, ActualWidth: > 0 }) { broken.Add("the inspector"); }
                if (!layout.ShowInspector && layout.Mode == PaneMode.Three) { broken.Add("three panes without the inspector"); }
                await DrawDelegates(window, page, layout, broken, theme, outFile);
            }
            await Picture(root, Path.ChangeExtension(outFile, $".{tag}.{theme.ToLowerInvariant()}.png"));
        }
        return broken;
    }

    static async Task CheckPagesInEveryTheme(MainWindow window, Action<bool, string> check, string outFile)
    {
        var root = window.RootElement;
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                await Task.Delay(300);
                var broken = await DrawPages(window, theme.ToString(), outFile);
                check(broken.Count == 0 && root.ActualTheme == theme, $"draws every page with its title in {theme}{(broken.Count > 0 ? $": not {string.Join(", ", broken)}" : "")}");
            }
            root.RequestedTheme = ElementTheme.Default;
        }
    }

    // High contrast: every page drawn again, and every role and surface a system colour. Windows'
    // setting is changed off the UI thread: SystemParametersInfo sends WM_SETTINGCHANGE to every
    // window, this app's included, and from the UI thread that message would re-enter XAML in the
    // middle of this method.
    static async Task CheckHighContrast(MainWindow window, Action<bool, string> check, string outFile, Dictionary<string, object?> result)
    {
        var contrast = await HighContrastPass.BeginAsync();
        result["highContrast"] = contrast.How;
        AppLog.Write($"self-test: high contrast {contrast.How}");
        if (!contrast.On)
        {
            // Skipped only off CI, where a developer's own setting is never changed.
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") { check(false, $"turns Windows' high contrast on for the pass ({contrast.How})"); }
            return;
        }
        try
        {
            var on = await Until(() => new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast, TimeSpan.FromSeconds(15));
            await Task.Delay(1500);
            check(on, "sees Windows' high contrast turned on");
            var hcBroken = await DrawPages(window, "HighContrast", outFile);
            check(hcBroken.Count == 0, $"draws every page with its title in high contrast{(hcBroken.Count > 0 ? $": not {string.Join(", ", hcBroken)}" : "")}");
            var system = new[] { "SystemColorHighlightColor", "SystemColorHighlightTextColor", "SystemColorWindowTextColor", "SystemColorWindowColor", "SystemColorGrayTextColor", "SystemColorButtonFaceColor", "SystemColorButtonTextColor", "SystemColorHotlightColor" }
                .Select(k => Application.Current.Resources.TryGetValue(k, out var c) && c is Windows.UI.Color colour ? colour.ToString() : null).OfType<string>().ToHashSet();
            var own = Design.DesignKeys.Brushes.Where(k => !(Application.Current.Resources.TryGetValue(k, out var b) && b is SolidColorBrush s && system.Contains(s.Color.ToString()))).ToList();
            check(system.Count > 0 && own.Count == 0, $"uses a system colour for every role and surface in high contrast{(own.Count > 0 ? $": not {string.Join(", ", own)}" : "")}");
            var systemCaptions = await Until(() => window.AppWindow.TitleBar.ButtonForegroundColor is null, TimeSpan.FromSeconds(10));
            check(systemCaptions, $"leaves the caption buttons in the system's colours in high contrast ({window.AppWindow.TitleBar.ButtonForegroundColor?.ToString() ?? "system"})");
            window.ShowPage("sessions");
        }
        finally
        {
            await contrast.EndAsync();
            AppLog.Write("self-test: high contrast put back");
        }
        // Back in the window's theme, repainted on the UI thread; and the app still running a moment
        // after the switch, which is when the title bar's own repaint once crashed it (#240).
        if (!contrast.TurnedOn) { return; }
        var theme = window.RootElement.ActualTheme;
        var back = await Until(() => !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
            && MainWindow.CaptionColours(theme) is { } c && window.AppWindow.TitleBar.ButtonForegroundColor == c.Text, TimeSpan.FromSeconds(15));
        check(back, $"draws the caption buttons in the {theme} theme again once high contrast is off ({window.AppWindow.TitleBar.ButtonForegroundColor?.ToString() ?? "system"})");
    }

    // Windows' high contrast for the self-test's pass, on CI only: a developer's own setting is
    // never changed. Turned back as it was when the pass ends, however it ends.
    sealed partial class HighContrastPass
    {
        const uint GetHighContrast = 0x42, SetHighContrast = 0x43, On_ = 0x1, SendChange = 0x2;
        uint before;
        bool changed;

        public bool On { get; private set; }
        // On for the pass, and so off again after it.
        public bool TurnedOn { get; private set; }
        public string How { get; private set; } = "";

        public static async Task<HighContrastPass> BeginAsync()
        {
            var pass = new HighContrastPass();
            if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) { pass.On = true; pass.How = "on already"; return pass; }
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") { pass.How = "not checked: high contrast is changed only on CI"; return pass; }
            var (read, flags, readError) = await Task.Run(() =>
            {
                var hc = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>() };
                var ok = SystemParametersInfo(GetHighContrast, hc.cbSize, ref hc, 0);
                return (ok, hc.dwFlags, ok ? 0 : Marshal.GetLastPInvokeError());
            });
            if (!read) { pass.How = $"not checked: reading the setting failed ({readError})"; return pass; }
            pass.before = flags;
            var (set, setError) = await Task.Run(() => Set(flags | On_));
            pass.changed = set;
            pass.On = set;
            pass.TurnedOn = set;
            pass.How = set ? "turned on for the pass" : $"not checked: turning it on failed ({setError})";
            return pass;
        }

        public async Task EndAsync()
        {
            if (!changed) { return; }
            changed = false;
            var flags = before;
            await Task.Run(() => Set(flags));
        }

        static (bool Ok, int Error) Set(uint flags)
        {
            var hc = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>(), dwFlags = flags };
            var ok = SystemParametersInfo(SetHighContrast, hc.cbSize, ref hc, SendChange);
            return (ok, ok ? 0 : Marshal.GetLastPInvokeError());
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIGHCONTRAST
        {
            public uint cbSize;
            public uint dwFlags;
            public nint lpszDefaultScheme;
        }

        [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SystemParametersInfo(uint action, uint param, ref HIGHCONTRAST value, uint winIni);
    }
    static T? Above<T>(DependencyObject node, Func<T, bool> match) where T : DependencyObject
    {
        for (var up = VisualTreeHelper.GetParent(node); up is not null; up = VisualTreeHelper.GetParent(up))
        {
            if (up is T t && match(t)) { return t; }
        }
        return null;
    }
    static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T) { node = VisualTreeHelper.GetParent(node); }
        return node as T;
    }

    static void CheckDesign(MainWindow window, Action<bool, string> check)
    {
        var design = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.ToString().EndsWith(Design.DesignKeys.Source["ms-appx:///".Length..], StringComparison.OrdinalIgnoreCase) == true);
        check(design is not null, "merges the design's generated resources");
        if (design is null) { return; }
        foreach (var theme in new[] { "Light", "Dark", "HighContrast" })
        {
            List<string> missing = design.ThemeDictionaries.TryGetValue(theme, out var d) && d is ResourceDictionary brushes
                ? [.. Design.DesignKeys.Brushes.Where(k => !brushes.TryGetValue(k, out var b) || b is not SolidColorBrush)]
                : [.. Design.DesignKeys.Brushes];
            check(missing.Count == 0, $"defines every design brush in {theme}{(missing.Count > 0 ? $": missing {string.Join(", ", missing)}" : "")}");
        }
        check(Application.Current.Resources.TryGetValue("MonoFontFamily", out var mono) && mono is FontFamily, "has the design's monospace stack");
        List<string> wrongAccent = [.. Design.DesignKeys.AccentColors.Where(a => !(Application.Current.Resources.TryGetValue(a.Key, out var c) && c is Windows.UI.Color colour && colour.ToString() == $"#FF{a.Color.TrimStart('#')}")).Select(a => a.Key)];
        check(wrongAccent.Count == 0, $"sets WinUI's accent palette to Bridge{(wrongAccent.Count > 0 ? $": not {string.Join(", ", wrongAccent)}" : "")}");
        List<string> missingType = [.. Design.DesignKeys.TypeStyles.Where(k => !(Application.Current.Resources.TryGetValue(k, out var s) && s is Style { TargetType.Name: "TextBlock" }))];
        List<string> missingSizes = [.. Design.DesignKeys.LayoutSizes.Where(k => !(Application.Current.Resources.TryGetValue(k, out var d) && d is double))];
        check(missingType.Count + missingSizes.Count == 0, $"has the design's type ramp and layout sizes{(missingType.Count + missingSizes.Count > 0 ? $": missing {string.Join(", ", missingType.Concat(missingSizes))}" : "")}");
        List<string> missingFonts = [.. Design.DesignKeys.FontFiles.Where(f => !File.Exists(Path.Combine(AppContext.BaseDirectory, f)))];
        check(missingFonts.Count == 0, $"bundles Agent Bridge Mono and its licence{(missingFonts.Count > 0 ? $": missing {string.Join(", ", missingFonts)}" : "")}");

        // The bundled font itself, with no fallback after it: in a monospace ten i are as wide as
        // ten M. Were the file not found, the name would fall through to Segoe UI, where they aren't.
        var bundled = (mono as FontFamily)?.Source.Split(',')[0].Trim() ?? "";
        TextBlock Sample(string text) => new() { Text = text, FontFamily = new FontFamily(bundled), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Left };
        var narrow = Sample("iiiiiiiiii");
        var wide = Sample("MMMMMMMMMM");

        // Drawn for a moment, unseen, so each template is applied and its path markup read.
        var host = new StackPanel { Opacity = 0, IsHitTestVisible = false };
        var controls = Design.DesignKeys.Glyphs.Concat(Design.DesignKeys.Icons)
            .Select(k => (Key: k, Control: new ContentControl { Style = Application.Current.Resources.TryGetValue(k, out var s) ? s as Style : null }))
            .ToList();
        foreach (var (_, c) in controls) { host.Children.Add(c); }
        host.Children.Add(narrow);
        host.Children.Add(wide);
        var root = (Panel)window.RootElement;
        root.Children.Add(host);
        try
        {
            host.UpdateLayout();
            var undrawn = controls.Where(x => x.Control.Style is null || Find<Microsoft.UI.Xaml.Shapes.Path>(x.Control).Count == 0).Select(x => x.Key).ToList();
            check(undrawn.Count == 0, $"draws every design glyph and icon{(undrawn.Count > 0 ? $": not {string.Join(", ", undrawn)}" : "")}");
            var (n, w) = (narrow.ActualWidth, wide.ActualWidth);
            check(n > 0 && Math.Abs(n - w) < 1, $"sets code in the bundled Agent Bridge Mono ({bundled}: ten i {n:0.#} px, ten M {w:0.#} px)");
        }
        finally { root.Children.Remove(host); }
    }

    // How many are listening to a card: its controls (on screen, once each) and the app's own.
    // -1 when the event cannot be found at all, so the check cannot pass by not looking.
    // Who listens, by the listener's type, for a failure to say.
    static string ListenersBy(AgentBridge.App.Core.Observable o) =>
        typeof(AgentBridge.App.Core.Observable).GetField(nameof(o.PropertyChanged), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(o) is Delegate d
            ? string.Join(", ", d.GetInvocationList().Select(l => l.Target?.GetType().Name ?? "static"))
            : "none";
    static int Listeners(AgentBridge.App.Core.Observable o) =>
        typeof(AgentBridge.App.Core.Observable).GetField(nameof(o.PropertyChanged), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is { } field
            ? (field.GetValue(o) as Delegate)?.GetInvocationList().Length ?? 0
            : -1;

    // A page's column lies within the window: centred, not pushed past its right edge.
    static bool Fits(MainWindow window, string paneId)
    {
        if (ById<FrameworkElement>(window.RootElement, paneId) is not { } pane || Find<StackPanel>(pane).FirstOrDefault() is not { } column) { return false; }
        var left = column.TransformToVisual(window.RootElement).TransformPoint(default).X;
        return left >= 0 && left + column.ActualWidth <= window.RootElement.ActualWidth + 1;
    }

    static async Task<bool> Until(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) { return false; }
            await Task.Delay(200);
        }
        return true;
    }

    static List<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        var found = new List<T>();
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T t) { found.Add(t); }
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) { stack.Push(VisualTreeHelper.GetChild(node, i)); }
        }
        return found;
    }

    static T? ById<T>(DependencyObject root, string id) where T : FrameworkElement =>
        Find<T>(root).FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == id);

    static async Task Picture(UIElement element, string file)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element);
            var pixels = await bitmap.GetPixelsAsync();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            var dpi = 96.0;
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels.ToArray());
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(file, bytes);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            AppLog.Write($"self-test picture not saved: {e.Message}");
        }
    }
}
