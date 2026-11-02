using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// Delegates (ui.md §6.3, §6.4; #229): the demo's session with delegates, at three panes and two.
// The strip under the header with its "N finished" fold; the conversation's one-line events, whose
// link opens a delegate; the inspector loaded with session.get { id, agent }, its ID in mono; and
// "Ask main agent to stop it", which fills the reply box and sends nothing. Then the Delegates tab
// below three panes, its list and a delegate pushed in its place.
static partial class SelfTest
{
    const string DelegatingId = "demo-delegates";
    const string Witness = "demo-agent-witness";

    static async Task CheckDelegates(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile)
    {
        var page = window.Sessions;
        var layout = page.Layout!;
        Resize(window, 1440, 900);
        await Until(() => layout.Mode == PaneMode.Three, TimeSpan.FromSeconds(10));
        var view = await Choose(window, main, DelegatingId, "DelegateStrip");
        if (view?.Card is not { } card) { check(false, "opens the demo's session with delegates"); return; }
        FrameworkElement? Item(string id) => ById<FrameworkElement>(view, SessionView.StripId(id));
        var strip = ById<ListView>(view, "DelegateStrip");
        var drawn = await Until(() => strip is { Visibility: Visibility.Visible, ActualHeight: > 0 } && Item(Witness) is { ActualWidth: > 0 } && Item("finished") is { ActualWidth: > 0 }, TimeSpan.FromSeconds(10));
        check(drawn && Item("demo-agent-grep") is null && AutomationProperties.GetName(Item(Witness)!).Contains("2 nested", StringComparison.Ordinal),
            $"draws the delegate strip under the session's header, a nested delegate counted on its parent, the finished folded ({(Item(Witness) is { } w ? AutomationProperties.GetName(w) : "none")})");

        // The main agent's own words, never a delegate's, and its delegates as one-line events.
        var events = ById<ItemsControl>(view, "DelegateEvents");
        var links = events is null ? [] : Find<HyperlinkButton>(events).Where(l => l.Visibility == Visibility.Visible).ToList();
        check(card.Speaker == "Main agent" && card.Response.StartsWith("Started 2 background agents", StringComparison.Ordinal) && links.Count >= 1,
            $"shows the main agent's own words, and its delegates starting and reporting back as lines with links ({links.Count} links)");

        // A delegate's link in the conversation opens it in the inspector.
        if (links.FirstOrDefault(l => l.Tag is "demo-agent-lint") is { } lint)
        {
            ((IInvokeProvider)new HyperlinkButtonAutomationPeer(lint).GetPattern(PatternInterface.Invoke)).Invoke();
            var opened = await Until(() => card.Delegates.SelectedId == "demo-agent-lint" && layout.ShowDelegateInspector, TimeSpan.FromSeconds(5));
            check(opened && !card.Delegates.Detail.CanAskToStop, "opens a delegate from its line in the conversation, with nothing to stop once it has failed");
        }
        else { check(false, "finds a delegate's link in the conversation"); }

        layout.OpenDelegate(Witness);
        var inspector = ById<FrameworkElement>(page, "Inspector") is { } pane ? ById<FrameworkElement>(pane, "DelegateInspector") : null;
        var loaded = await Until(() => inspector is { ActualHeight: > 0 } && card.Delegates.Detail.Latest.StartsWith("The witness follows", StringComparison.Ordinal)
            && ById<FrameworkElement>(inspector, "DelegateLatest") is MarkdownView { Children.Count: > 0 }, TimeSpan.FromSeconds(10));
        var id = inspector is null ? null : ById<TextBlock>(inspector, "Delegate:ID");
        var took = inspector is null ? [] : Find<TextBlock>(inspector).Where(t => AutomationProperties.GetAutomationId(t) == "DelegateToolTook").Select(t => t.Text).ToList();
        check(loaded && id?.Text == Witness && id.FontFamily.Source == ((Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["MonoFontFamily"]).Source && took.Count >= 2 && took.All(t => t.Contains(':', StringComparison.Ordinal)),
            $"shows the delegate selected in the inspector: its own words fetched, its ID in mono, and its tools with how long each took ({string.Join(", ", took)})");
        check(Item(Witness) is { } selected && AutomationProperties.GetName(selected).EndsWith(", selected", StringComparison.Ordinal), "marks the delegate shown in the inspector as selected in the strip");
        await Picture(window.RootElement, Path.ChangeExtension(outFile, ".delegates.png"));

        // "Ask main agent to stop it": the reply box, filled, and nothing sent.
        if (inspector is not null && ById<Button>(inspector, "AskToStop") is { Visibility: Visibility.Visible } ask && card.Reply is { } reply)
        {
            var before = reply.Text;
            ((IInvokeProvider)new ButtonAutomationPeer(ask).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(300);
            var box = ById<TextBox>(view, "ReplyText");
            check(box?.Text.Contains($"({Witness})", StringComparison.Ordinal) == true && !reply.Pending && reply.StatusText.Length == 0,
                "fills the reply box to ask the main agent to stop a delegate, and sends nothing");
            reply.Text = before;
        }
        else { check(false, "offers Ask main agent to stop it for a running delegate"); }

        // Below three panes: the Delegates tab, its list, and a delegate pushed in its place.
        Resize(window, 1000, 900);
        await Until(() => layout.Mode == PaneMode.Two, TimeSpan.FromSeconds(10));
        layout.CloseDelegate();
        layout.Tab = SessionTab.Delegates;
        var listed = await Until(() => ById<FrameworkElement>(view, "DelegateList") is { Visibility: Visibility.Visible, ActualHeight: > 0 } && ById<FrameworkElement>(view, $"Delegate:{Witness}") is { ActualHeight: > 0 }
            && ById<FrameworkElement>(view, "Delegate:demo-agent-grep") is { ActualHeight: > 0 }, TimeSpan.FromSeconds(10));
        check(listed && !layout.ShowStrip, "lists every delegate in the Delegates tab below three panes, a nested one under its parent");
        layout.OpenDelegate(Witness);
        var pushed = await Until(() => ById<FrameworkElement>(view, "DelegateDetail") is { Visibility: Visibility.Visible } detail && ById<FrameworkElement>(detail, "DelegateInspector") is { ActualHeight: > 0 }, TimeSpan.FromSeconds(10));
        check(pushed, "pushes a delegate opened from the Delegates tab in the list's place");
        await Picture(window.RootElement, Path.ChangeExtension(outFile, ".delegates2.png"));
        layout.CloseDelegate();
        layout.Tab = SessionTab.Conversation;
    }

    // For the theme passes: the session with delegates, at three panes, a delegate in the inspector.
    static async Task ShowADelegate(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        var layout = window.Sessions.Layout!;
        Resize(window, 1440, 1000);
        window.ShowPage("sessions");
        main.Selected = main.AllCards.First(c => c.Id == DelegatingId);
        layout.InspectorWanted = true;
        layout.OpenDelegate(Witness);
        var shown = await Until(() => layout.Mode == PaneMode.Three && layout.ShowStrip && layout.ShowDelegateInspector, TimeSpan.FromSeconds(10));
        check(shown, "shows a delegate in the strip and the inspector for every theme's pass");
    }

    // A theme's pass over the Sessions page: the strip and the delegate inspector drawn, when shown;
    // and the session's own Details and Tools, which the inspector shows with no delegate selected,
    // drawn too before the delegate is shown again.
    static async Task DrawDelegates(MainWindow window, FrameworkElement page, PaneLayoutViewModel layout, List<string> broken, string theme, string outFile)
    {
        var strip = ById<FrameworkElement>(page, "DelegateStrip");
        if (layout.ShowStrip && (strip is not { Visibility: Visibility.Visible, ActualHeight: > 0 } || !Find<StatusGlyphView>(strip).Any(g => g.ActualWidth > 0)))
        {
            broken.Add("the delegate strip");
        }
        var pane = ById<FrameworkElement>(page, "Inspector");
        var inspector = pane is null ? null : ById<FrameworkElement>(pane, "DelegateInspector");
        if (layout.ShowDelegateInspector && (inspector is not { ActualHeight: > 0 } || ById<TextBlock>(inspector, "DelegateName") is not { ActualHeight: > 0 }))
        {
            broken.Add("the delegate inspector");
        }
        if (!layout.ShowDelegateInspector || window.ViewModel.Selected?.Delegates.SelectedId is not { } shown) { return; }
        await Picture(window.RootElement, Path.ChangeExtension(outFile, $".delegate.{theme.ToLowerInvariant()}.png"));
        layout.CloseDelegate();
        if (!await Until(() => pane is not null && ById<FrameworkElement>(pane, "InspectorDetails") is { ActualHeight: > 0 }, TimeSpan.FromSeconds(5))) { broken.Add("the session's details beside its delegates"); }
        layout.OpenDelegate(shown);
        await Task.Delay(300);
    }
}
