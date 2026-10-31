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
