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

// --self-test <file>: CI's check that the published app starts, draws the demo's cards and
// answers through them. It finds the controls the way UI Automation does (by automation id),
// clicks through their automation peers, waits for the demo host's answer to come back to the
// card, saves a picture of each page beside the result file, writes what it saw and exits.
static partial class SelfTest
{
    public static async Task RunAsync(MainWindow window, MainViewModel main, LaunchViewModel launch, string outFile, DemoHost demo, DemoHost remote, Action<int> exit)
    {
        var result = new Dictionary<string, object?>();
        var checks = new List<string>();
        var failures = new List<string>();
        void Check(bool ok, string what) { (ok ? checks : failures).Add(what); }
        // A ThemeResource or StaticResource that resolves to nothing, in any theme: logged, and a
        // failure, since an installed app may die of it with nothing but 0xC000027B.
        Application.Current.DebugSettings.XamlResourceReferenceFailed += (_, e) =>
        {
            AppLog.Write($"self-test: a XAML resource reference failed: {e.Message}");
            failures.Add($"a XAML resource reference failed: {e.Message}");
        };
        try
        {
            // This machine's sessions and the other machine's, through the demo's stand-in for the relay.
            var expected = demo.Count + remote.Count;
            result["expectedCards"] = expected;
            var drawn = await Until(() => Find<SessionCard>(window.RootElement).Count == expected, TimeSpan.FromSeconds(60));
            var cards = Find<SessionCard>(window.RootElement);
            result["drawnCards"] = cards.Count;
            Check(drawn, $"draws all {expected} demo sessions of both machines as cards");
            Check(main.Waiting == 4, "counts four sessions waiting on you across both machines");
            Check(main.ShowMachines && main.Machines.Any(m => m.Label.StartsWith(remote.MachineName, StringComparison.Ordinal)), "offers the other machine in the machine picker");
            Check(cards.FirstOrDefault(c => c.Card?.Id == "demo-remote-codex")?.Card?.Meta.StartsWith(remote.MachineName, StringComparison.Ordinal) == true, "names the machine on another machine's card");

            var approval = cards.FirstOrDefault(c => c.Card?.Id == "demo-approval");
            Check(approval is not null && ById<Button>(approval, "Option:approve") is { IsEnabled: true } && ById<Button>(approval, "Option:deny") is not null,
                "shows Approve once and Deny on a Copilot permission prompt");
            var form = cards.FirstOrDefault(c => c.Card?.Id == "02d9e5d5-6b35-49bd-b083-39572fbff648");
            Check(form is not null && ById<Button>(form, "SendAnswer") is not null && ById<TextBox>(form, "Field:notes") is not null,
                "draws a form question with its fields and Send");
            Check(cards.Any(c => ById<FrameworkElement>(c, "Response") is MarkdownView { Children.Count: > 0 }), "renders a response's Markdown");
            // Status is a glyph and words (docs/design/ui.md §2.2), the glyph drawn from the generated
            // styles and named as its row reads (§9).
            var glyphs = cards.Select(c => (c.Card, Glyph: ById<StatusGlyphView>(c, "StatusGlyph"))).ToList();
            var unnamed = glyphs.Where(g => g.Glyph is null || g.Card is null || AutomationProperties.GetName(g.Glyph) != g.Card.GlyphLabel || Find<Microsoft.UI.Xaml.Shapes.Path>(g.Glyph).Count == 0).Select(g => g.Card?.Id ?? "?").ToList();
            Check(glyphs.Count > 0 && unnamed.Count == 0, $"draws every card's status glyph, named with its state{(unnamed.Count > 0 ? $": not {string.Join(", ", unnamed)}" : "")}");
            result["glyphs"] = glyphs.Select(g => $"{g.Card?.Glyph}{(g.Card?.Driven == true ? "+driven" : "")}: {g.Card?.StatusText}").ToList();
            var navIcons = Find<ContentControl>(window.RootElement).Where(c => AutomationProperties.GetAutomationId(c) == "NavIcon").ToList();
            Check(navIcons.Count >= 3 && navIcons.All(i => Find<Microsoft.UI.Xaml.Shapes.Path>(i).FirstOrDefault()?.Stroke is SolidColorBrush { Color.A: > 0 }),
                $"draws the navigation's design icons in a visible colour ({navIcons.Count} found)");
            CheckDesign(window, Check);
            await CheckShell(window, main, Check);
            await CheckStatusLines(window, cards, Check);

            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".window.png"));
            await Picture(window.CardList, Path.ChangeExtension(outFile, ".sessions.png"));

            // Answer the single-choice question with one click, as a person would.
            var choice = cards.FirstOrDefault(c => c.Card?.Id == "demo-choice");
            if (choice is not null && ById<Button>(choice, "Option:pg") is { } pg)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(pg).GetPattern(PatternInterface.Invoke)).Invoke();
                var answered = await Until(() => choice.Card?.ReceiptText.StartsWith("Your answer: Delivered", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(20));
                result["receipt"] = choice.Card?.ReceiptText;
                Check(answered, "answers a question from its card and shows it was delivered");
            }
            else { Check(false, "finds the single-choice question's option button"); }

            // Answer a question on the other machine the same way: it goes to that machine's host.
            // Found afresh: the answer above re-sorts the list, which recycles the card elements found
            // before it, so the earlier list can hold this card's element under another session.
            SessionCard? remoteChoice = null;
            await Until(() => (remoteChoice = Find<SessionCard>(window.RootElement).FirstOrDefault(c => c.Card?.Id == "demo-remote-claude" && ById<Button>(c, "Option:device") is not null)) is not null, TimeSpan.FromSeconds(10));
            if (remoteChoice is not null && ById<Button>(remoteChoice, "Option:device") is { } device)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(device).GetPattern(PatternInterface.Invoke)).Invoke();
                var answered = await Until(() => remoteChoice.Card?.ReceiptText.StartsWith("Your answer: Delivered", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(20));
                result["remoteReceipt"] = remoteChoice.Card?.ReceiptText;
                Check(answered, "answers a question on another machine from its card and shows it was delivered");
            }
            else { Check(false, "finds the other machine's question's option button"); }

            // The picker narrows the list to one machine.
            var watched = main.AllCards.First(c => !c.Id.StartsWith("demo-remote", StringComparison.Ordinal));
            main.SelectedMachine = main.Machines.First(m => m.Key == DemoHost.RemoteId);
            var narrowed = await Until(() => Find<SessionCard>(window.RootElement).Count == remote.Count, TimeSpan.FromSeconds(10));
            Check(narrowed, "shows only the chosen machine's cards");
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".remote.png"));
            await Task.Delay(500);
            var hidden = Listeners(watched);
            main.SelectedMachine = main.Machines[0];
            await Until(() => Find<SessionCard>(window.RootElement).Count == expected, TimeSpan.FromSeconds(10));
            await Task.Delay(500);
            var shown = Listeners(watched);

            // Back and forth: each time the cards get new controls, and the old ones let go.
            for (var i = 0; i < 5; i++)
            {
                main.SelectedMachine = main.Machines.First(m => m.Key == DemoHost.RemoteId);
                await Until(() => Find<SessionCard>(window.RootElement).Count == remote.Count, TimeSpan.FromSeconds(10));
                main.SelectedMachine = main.Machines[0];
                await Until(() => Find<SessionCard>(window.RootElement).Count == expected, TimeSpan.FromSeconds(10));
            }
            // A leak grows with each change; the count read as shown can also include a recycled
            // control not yet unloaded (4 shown, 2 settled, seen once in four runs), so it is a ceiling.
            var settled = await Until(() => Listeners(watched) is var n && n <= shown && n > hidden, TimeSpan.FromSeconds(10));
            var again = Find<SessionCard>(window.RootElement).FirstOrDefault(c => c.Card?.Id == "demo-approval");
            Check(again is not null && ById<Button>(again, "Option:approve") is { IsEnabled: true }, "still shows a question's choices on a card drawn again after the picker changes");
            Check(settled && shown > hidden && hidden >= 0, $"a card's control listens while shown and lets go when hidden, however often the picker changes ({hidden} listeners hidden, {shown} shown, {Listeners(watched)} after five more changes)");

            // Launch a session from the Launch page: it starts, its card appears, and Open goes to it.
            window.ShowPage("launch");
            var ready = await Until(() => launch.ShowForm && launch.Machines.Count == 2 && ById<Button>(window.RootElement, "LaunchButton") is { IsEnabled: true }, TimeSpan.FromSeconds(10));
            Check(ready, "offers both machines on the Launch page");
            // Wider than a page's column, which is where they once spilled off the right edge.
            var scale = window.RootElement.XamlRoot.RasterizationScale;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1000 * scale), (int)(900 * scale)));
            await Task.Delay(700);
            Check(Fits(window, "LaunchPane"), "fits the Launch page's column in a wide window");
            window.ShowPage("sessions");
            await Task.Delay(500);
            Check(Fits(window, "SessionsPane"), "fits the Sessions page's column in a wide window");
            window.ShowPage("launch");
            await Task.Delay(500);
            var before = main.AllCards.Count();
            launch.Prompt = "Summarize the open pull requests.";
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".launch.png"));
            if (ById<Button>(window.RootElement, "LaunchButton") is { } go)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(go).GetPattern(PatternInterface.Invoke)).Invoke();
                var started = await Until(() => launch.State == "started" && main.AllCards.Count() == before + 1, TimeSpan.FromSeconds(20));
                result["launchStatus"] = launch.StatusText;
                Check(started, "launches a session from the Launch page and shows its card");
            }
            else { Check(false, "finds the Launch button"); }
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".launched.png"));

            // Codex on the other machine starts only on a first message: nothing is sent without one.
            // The new session's card opened on the Sessions page: back to Launch, which fetches its choices.
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

            // The search box finds a session on any machine and opens it, from any page.
            var sought = main.AllCards.First(c => c.Id == "demo-remote-claude");
            window.SearchFor(sought.Title);
            var hits = window.Search.ItemsSource as IReadOnlyList<SearchHit> ?? [];
            Check(hits.Any(h => h.SessionId == sought.Id && h.Machine == DemoHost.RemoteId), $"finds another machine's session by its title in the search box ({hits.Count} found)");
            sought.Folded = true;
            if (hits.FirstOrDefault(h => h.SessionId == sought.Id) is { } hit) { window.OpenHit(hit); }
            await Task.Delay(500);
            Check(window.CurrentPage == "sessions" && !sought.Folded && window.Search.Text.Length == 0, "opens a session chosen in the search box on the Sessions page");

            await CheckMachinesPage(window, main, Check, outFile);
            await CheckNarrowest(window, Check, outFile);
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
        await Write(outFile, result, checks, failures, null);
        exit(failures.Count == 0 ? 0 : 1);
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
    // The status words in their tone's text colour, the current tool's glyph named for its state
    // (ui.md §9), and the status line inside its card. Then the window's theme flipped: the words
    // and the glyphs must take the other theme's colours without a session update, as the cards
    // behind them do; a brush looked up once kept the old theme's, dark on dark.
    static async Task CheckStatusLines(MainWindow window, List<SessionCard> cards, Action<bool, string> check)
    {
        static string Colour(object? b) => b is SolidColorBrush s ? s.Color.ToString() : "none";
        var lines = cards.Where(c => c.Card is not null).Select(c => (Control: c, Card: c.Card!, Words: ById<TextBlock>(c, "Status"), Glyph: ById<StatusGlyphView>(c, "StatusGlyph"))).ToList();
        List<string> Wrong(Func<StatusTone, object?> expected) => [.. lines.Where(l => l.Words is null || Colour(l.Words.Foreground) != Colour(expected(l.Card.StatusTone))).Select(l => l.Card.Id)];
        var wrong = Wrong(Ui.WordsBrush);
        check(lines.Count > 0 && wrong.Count == 0, $"sets every status's words in its tone's text colour{(wrong.Count > 0 ? $": not {string.Join(", ", wrong)}" : "")}");

        var tools = lines.Where(l => l.Card.HasTool).Select(l => (l.Card, Glyph: ById<StatusGlyphView>(l.Control, "ToolGlyph"))).ToList();
        var unnamed = tools.Where(t => t.Glyph is null || AutomationProperties.GetName(t.Glyph) != t.Card.ToolState || AutomationProperties.GetAccessibilityView(t.Glyph) != AccessibilityView.Content).Select(t => t.Card.Id).ToList();
        check(tools.Count > 0 && unnamed.Count == 0, $"names every current tool's glyph with its state{(unnamed.Count > 0 ? $": not {string.Join(", ", unnamed)}" : "")}");

        var outside = lines.Where(l => l.Words is { } w && w.TransformToVisual(l.Control).TransformPoint(new Windows.Foundation.Point(w.ActualWidth, 0)).X > l.Control.ActualWidth + 0.5).Select(l => l.Card.Id).ToList();
        check(outside.Count == 0, $"keeps every status's words inside its card{(outside.Count > 0 ? $": not {string.Join(", ", outside)}" : "")}");

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
        }
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
        var (w, h) = WindowPlacement.Smallest(scale);
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

    // At the smallest window (360×600, ui.md §5.2), every control on every page is within the
    // window: none cut off at the right, where nothing scrolls to reach it.
    static async Task CheckNarrowest(MainWindow window, Action<bool, string> check, string outFile)
    {
        var root = window.RootElement;
        var before = window.AppWindow.Size;
        Resize(window, DesignLayout.MinWidth, DesignLayout.MinHeight);
        try
        {
            await Until(() => root.ActualWidth <= DesignLayout.MinWidth + 1, TimeSpan.FromSeconds(10));
            var cut = new List<string>();
            foreach (var (tag, pane) in Pages.Where(p => p.Tag != "sessions"))
            {
                window.ShowPage(tag);
                await Task.Delay(500);
                if (ById<FrameworkElement>(root, pane) is not { } page) { cut.Add($"{tag}: no page"); continue; }
                foreach (var control in Find<Control>(page).Where(c => c is Button or ComboBox or TextBox or ToggleSwitch && Shown(c, page)))
                {
                    var right = control.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(control.ActualWidth, 0)).X;
                    if (right > root.ActualWidth + 1) { cut.Add($"{tag}: {(AutomationProperties.GetAutomationId(control) is { Length: > 0 } id ? id : control.GetType().Name)} to {right:0}"); }
                }
                await Picture(root, Path.ChangeExtension(outFile, $".{tag}.narrow.png"));
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
            window.ShowPage("sessions");
        }
        finally
        {
            await contrast.EndAsync();
            AppLog.Write("self-test: high contrast put back");
        }
    }

    // Windows' high contrast for the self-test's pass, on CI only: a developer's own setting is
    // never changed. Turned back as it was when the pass ends, however it ends.
    sealed partial class HighContrastPass
    {
        const uint GetHighContrast = 0x42, SetHighContrast = 0x43, On_ = 0x1, SendChange = 0x2;
        uint before;
        bool changed;

        public bool On { get; private set; }
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
