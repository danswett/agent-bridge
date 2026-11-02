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
static class SelfTest
{
    public static async Task RunAsync(MainWindow window, MainViewModel main, LaunchViewModel launch, string outFile, DemoHost demo, DemoHost remote, Action<int> exit)
    {
        var result = new Dictionary<string, object?>();
        var checks = new List<string>();
        var failures = new List<string>();
        void Check(bool ok, string what) { (ok ? checks : failures).Add(what); }
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
            await CheckStatusLines(window, cards, Check);

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
        }
        catch (Exception e)
        {
            failures.Add($"crashed: {e}");
        }
        result["passed"] = checks;
        result["failed"] = failures;
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        exit(failures.Count == 0 ? 0 : 1);
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
