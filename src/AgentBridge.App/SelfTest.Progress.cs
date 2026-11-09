using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// The progress gallery (ui.md §6.5, #600, #617): every state of a reply's and an answer's
// receipt, drawn from real requests to the demo host (DemoHost.Script holds them or picks their
// outcome) and saved as result.progress.<state>.<pass>.png, so the design is signed off on what
// the app draws rather than on its boards. Run once per pass: light, dark, narrow and high
// contrast. Each state is checked as well as drawn, so a wrong state fails the run.
static partial class SelfTest
{
    // Light and dark at the window's size, then the narrowest window in the default theme. High
    // contrast is drawn in its own pass (CheckHighContrast), since only CI may turn it on.
    static async Task ProgressPasses(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check, string outFile, Dictionary<string, object?> result)
    {
        var root = window.RootElement;
        window.ShowPage("sessions");
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                await Task.Delay(300);
                await ProgressGallery(window, main, demo, check, outFile, result, theme.ToString().ToLowerInvariant());
            }
            root.RequestedTheme = ElementTheme.Default;
        }
        var before = window.AppWindow.Size;
        Resize(window, DesignLayout.MinWidth, DesignLayout.MinHeight);
        try
        {
            await Until(() => root.ActualWidth <= DesignLayout.MinWidth + 1, TimeSpan.FromSeconds(10));
            await ProgressGallery(window, main, demo, check, outFile, result, "narrow");
        }
        finally
        {
            window.AppWindow.Resize(before);
            await Task.Delay(500);
        }
    }

    static async Task ProgressGallery(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check, string outFile, Dictionary<string, object?> result, string pass)
    {
        AppLog.Write($"self-test: the progress gallery, {pass}");
        var narrow = pass == "narrow";
        var drawn = new List<string>();
        var now = DateTimeOffset.UtcNow;
        var ids = new List<string>();
        string Id(string what) => $"gallery-{pass}-{what}";

        async Task<SessionCardViewModel?> Add(string what, Question? question)
        {
            var id = Id(what);
            ids.Add(id);
            await demo.PutAsync(new Session
            {
                Id = id,
                Agent = AgentKind.Copilot,
                Title = question is null ? "Tidy the release notes" : "Plan the next step",
                Repository = "danswett/agent-bridge",
                Branch = "main",
                Status = question is null ? SessionStatus.Idle : SessionStatus.Waiting,
                StatusSince = now,
                UpdatedAt = now,
                Activity = new Activity { Response = question is null ? "The notes are drafted." : "Two ways to go.", TurnCount = 3 },
                Question = question,
            });
            await Until(() => main.AllCards.Any(c => c.Id == id), TimeSpan.FromSeconds(10));
            return main.AllCards.FirstOrDefault(c => c.Id == id);
        }

        async Task Draw(string state)
        {
            // A layout pass and a frame, so what changed is on screen before it is pictured.
            await Task.Delay(250);
            var name = $".progress.{state}.{pass}.png";
            UIElement target = narrow ? window.RootElement : window.Sessions.SessionPaneView;
            await Picture(target, Path.ChangeExtension(outFile, name));
            drawn.Add($"result{name}");
        }

        static Question Tap(string id) => new()
        {
            Id = id,
            Kind = QuestionKind.Form,
            Message = "Which should come first?",
            AskedAt = DateTimeOffset.UtcNow,
            Fields = [new Field { Name = "answer", Type = FieldType.Choice, Options = [new Option("fixes", "Fixes first"), new Option("docs", "Docs first")] }],
        };

        // The colours of a button's in-flight ring, its ink and its fill, as drawn now.
        static (Windows.UI.Color? Ring, Windows.UI.Color? Ink, Windows.UI.Color? Fill)? RingInk(Button? b)
        {
            if (b is null || Find<ProgressRing>(b).FirstOrDefault() is not { } ring) { return null; }
            static Windows.UI.Color? Of(Microsoft.UI.Xaml.Media.Brush? brush) => (brush as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color;
            return (Of(ring.Foreground), Of(b.Foreground), Of(b.Background));
        }

        static void Press(Button b) => ((IInvokeProvider)new ButtonAutomationPeer(b).GetPattern(PatternInterface.Invoke)).Invoke();

    // The width an option's label is laid out at, beside the room kept for its ring (WithRing).
    static double LabelWidth(Button? b) => b?.Content is Grid { Children: [_, _, FrameworkElement label, ..] } ? label.ActualWidth : -1;

        // Whether a screen reader hears an option as pressed (OptionButton's Toggle pattern): only the
        // answer that went, while it stands.
        static ToggleState? Heard(Button? b) => b is null ? null : (FrameworkElementAutomationPeer.CreatePeerForElement(b)?.GetPattern(PatternInterface.Toggle) as IToggleProvider)?.ToggleState;

        try
        {
            // A reply: Send's ring in the quiet window, Still sending… once 3 s pass with nothing
            // heard, Send's check once delivered, then a reply the machine fails with Send again.
            if (await Add("reply", null) is { } replyCard && await Choose(window, main, replyCard.Id, "SendReply") is not null && replyCard.Reply is { } reply)
            {
                var held = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                demo.Script(replyCard.Id, () => held.Task);
                reply.Text = "Carry on with the release notes";
                reply.Send();
                await Until(() => reply.Pending, TimeSpan.FromSeconds(2));
                check(reply.Pending && reply.StatusText.Length == 0 && !reply.SendRest, $"shows only Send's ring while a reply goes, nothing on the line ({pass}: \"{reply.StatusText}\")");
                await Draw("send-ring");
                // The ring took the platform's accent, which is Send's own fill: teal on teal, a
                // solid button with nothing turning on it.
                var sendInk = RingInk(ById<Button>(window.Content, "SendReply"));
                check(sendInk is { Ring: { } ringInk, Ink: { } buttonInk, Fill: var fill } && ringInk == buttonInk && ringInk != fill,
                    $"draws Send's ring in Send's own ink, never its fill ({pass}: ring {sendInk?.Ring}, ink {sendInk?.Ink}, fill {sendInk?.Fill})");
                var stalled = await Until(() => reply.StatusText == "Still sending…", TimeSpan.FromSeconds(8));
                check(stalled, $"says Still sending… once 3 s pass with nothing heard ({pass}: \"{reply.StatusText}\")");
                await Draw("still-sending");
                held.SetResult(new ReplyResult(AnswerOutcome.Delivered));
                var checkShown = await Until(() => reply.SendCheck, TimeSpan.FromSeconds(5));
                check(checkShown && reply.StatusText.Length == 0, $"shows Send's check once delivered, nothing on the line ({pass}: \"{reply.StatusText}\")");
                var polite = await Until(() => ById<TextBlock>(window.Content, "ReplyStatus") is { } line && AutomationProperties.GetLiveSetting(line) == AutomationLiveSetting.Polite, TimeSpan.FromSeconds(3));
                check(polite && !reply.AnnouncementUrgent, $"announces a delivered reply politely ({pass}: \"{reply.Announcement}\")");
                await Draw("send-check");
                await Until(() => reply.SendRest, TimeSpan.FromSeconds(5));
                demo.Script(replyCard.Id, () => throw new InvalidOperationException("the session's terminal closed"));
                reply.Text = "And bump the version";
                reply.Send();
                var failed = await Until(() => reply.ShowResend, TimeSpan.FromSeconds(10));
                check(failed && reply.SendRest && reply.StatusText.StartsWith("Couldn't send it", StringComparison.Ordinal), $"shows a failed reply's reason and Send again, Send at rest ({pass}: \"{reply.StatusText}\")");
                // A failure is said assertively, a delivery politely (ui.md §9.1 A6; #625, Copilot 4478422096).
                var assertive = await Until(() => ById<TextBlock>(window.Content, "ReplyStatus") is { } line && AutomationProperties.GetLiveSetting(line) == AutomationLiveSetting.Assertive, TimeSpan.FromSeconds(3));
                check(assertive && reply.AnnouncementUrgent, $"announces a failed reply assertively ({pass}: \"{reply.Announcement}\")");
                await Draw("reply-failed");
            }
            else { check(false, $"opens the gallery's reply session ({pass})"); }

            // Tap to answer: the tapped option's ring, then unconfirmed (it stays pressed, the
            // rest disabled: one attempt per prompt).
            if (await Add("tap", Tap("gallery-tap")) is { } tapCard && await Choose(window, main, tapCard.Id, "Option:fixes") is { } tapView && tapCard.Question is { } tq)
            {
                var held = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                demo.Script(tapCard.Id, () => held.Task);
                tapView.UpdateLayout();
                var atRest = LabelWidth(ById<Button>(tapView, "Option:fixes"));
                if (ById<Button>(tapView, "Option:fixes") is { } fixes) { Press(fixes); }
                await Until(() => tq.InFlight, TimeSpan.FromSeconds(2));
                check(tq.InFlight && ById<Button>(tapView, "Option:fixes") is { IsEnabled: true } && ById<Button>(tapView, "Option:docs") is { IsEnabled: false },
                    $"keeps the tapped option at full strength with its ring while the others are disabled ({pass})");
                await Draw("tap-ring");
                // The ring takes room kept for it at rest, so the label never re-wraps (#625,
                // Copilot 4478422056).
                tapView.UpdateLayout();
                var withRing = LabelWidth(ById<Button>(tapView, "Option:fixes"));
                check(atRest > 0 && Math.Abs(withRing - atRest) < 0.5, $"keeps the tapped option's label as wide with its ring as at rest ({pass}: {atRest:0.#} px, then {withRing:0.#})");
                held.SetResult(new AnswerResult(AnswerOutcome.Unconfirmed, "no word from the agent"));
                var unconfirmed = await Until(() => tq.StatusText.StartsWith("Sent, but not confirmed", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
                check(unconfirmed && !tq.CanAnswer && ById<Button>(tapView, "Option:fixes") is { IsEnabled: true } && ById<Button>(tapView, "Option:docs") is { IsEnabled: false },
                    $"keeps the tapped option pressed and the others disabled once unconfirmed ({pass}: \"{tq.StatusText}\")");
                check(Heard(ById<Button>(tapView, "Option:fixes")) == ToggleState.On && Heard(ById<Button>(tapView, "Option:docs")) == ToggleState.Off,
                    $"has a screen reader hear the tapped option as pressed once unconfirmed, and the others not ({pass}: {Heard(ById<Button>(tapView, "Option:fixes"))}, {Heard(ById<Button>(tapView, "Option:docs"))})");
                await Draw("tap-unconfirmed");
            }
            else { check(false, $"opens the gallery's tap question ({pass})"); }

            // Stale: the same as unconfirmed, the tapped option pressed and the rest disabled.
            if (await Add("stale", Tap("gallery-stale")) is { } staleCard && await Choose(window, main, staleCard.Id, "Option:docs") is { } staleView && staleCard.Question is { } sq)
            {
                demo.Script(staleCard.Id, () => Task.FromResult<object?>(new AnswerResult(AnswerOutcome.Stale, "the session is no longer asking that question")));
                if (ById<Button>(staleView, "Option:docs") is { } docs) { Press(docs); }
                var stale = await Until(() => sq.StatusText.StartsWith("Already answered or gone", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
                check(stale && !sq.CanAnswer && ById<Button>(staleView, "Option:docs") is { IsEnabled: true } && ById<Button>(staleView, "Option:fixes") is { IsEnabled: false },
                    $"keeps the tapped option pressed and the others disabled once stale ({pass}: \"{sq.StatusText}\")");
                check(Heard(ById<Button>(staleView, "Option:docs")) == ToggleState.On && Heard(ById<Button>(staleView, "Option:fixes")) == ToggleState.Off,
                    $"has a screen reader hear the tapped option as pressed once stale, and the others not ({pass}: {Heard(ById<Button>(staleView, "Option:docs"))}, {Heard(ById<Button>(staleView, "Option:fixes"))})");
                await Draw("tap-stale");
            }
            else { check(false, $"opens the gallery's stale question ({pass})"); }

            // Busy, then refused: nothing was typed, so every option is offered again.
            if (await Add("busy", Tap("gallery-busy")) is { } busyCard && await Choose(window, main, busyCard.Id, "Option:fixes") is { } busyView)
            {
                demo.Script(busyCard.Id, () => Task.FromResult<object?>(new AnswerResult(AnswerOutcome.Busy, "the agent is mid-step")));
                if (ById<Button>(busyView, "Option:fixes") is { } fixes) { Press(fixes); }
                var busyShown = await Until(() => busyCard.Question is { StatusText.Length: > 0 }, TimeSpan.FromSeconds(8));
                var busyText = busyCard.Question?.StatusText;
                check(busyShown, $"says the agent is busy ({pass}: \"{busyText}\")");
                await Draw("tap-busy");
                // The line stays only as long as the pause that follows it; after that the options are back and the line is gone.
                var again = await Until(() => busyCard.Question is { CanAnswer: true, StatusText.Length: 0 } && ById<Button>(busyView, "Option:docs") is { IsEnabled: true }, TimeSpan.FromSeconds(8));
                check(again, $"offers every option again after busy, with the busy line gone ({pass}: \"{busyCard.Question?.StatusText}\")");
                demo.Script(busyCard.Id, () => Task.FromResult<object?>(new AnswerResult(AnswerOutcome.Refused, "the prompt changed")));
                if (ById<Button>(busyView, "Option:docs") is { } docs) { Press(docs); }
                var refused = await Until(() => busyCard.Question is { CanAnswer: true } q && q.StatusText.StartsWith("The machine refused", StringComparison.Ordinal) && ById<Button>(busyView, "Option:fixes") is { IsEnabled: true }, TimeSpan.FromSeconds(8));
                check(refused, $"offers every option again after refused ({pass}: \"{busyCard.Question?.StatusText}\")");
                check(Heard(ById<Button>(busyView, "Option:fixes")) == ToggleState.Off && Heard(ById<Button>(busyView, "Option:docs")) == ToggleState.Off,
                    $"has a screen reader hear no option as pressed after refused ({pass})");
                await Draw("tap-refused");
            }
            else { check(false, $"opens the gallery's busy question ({pass})"); }

            // You answered · value: a tap's option label, free text's first line, nothing for a form.
            // The demo's own handling: delivered, and the question cleared, so the card's receipt says it.
            async Task Answered(string what, Question question, string part, Action<QuestionViewModel, SessionView> answer, string want)
            {
                if (await Add($"answered-{what}", question) is { } card && await Choose(window, main, card.Id, part) is { } view && card.Question is { } q)
                {
                    answer(q, view);
                    var said = await Until(() => card.ReceiptText == want, TimeSpan.FromSeconds(10));
                    check(said && card.ReceiptTone == Tone.Progress, $"resolves to \"{want}\" in text2 ({pass}: \"{card.ReceiptText}\")");
                    await Draw($"answered-{what}");
                }
                else { check(false, $"opens the gallery's {what} question ({pass})"); }
            }
            await Answered("tap", Tap("gallery-answered"), "Option:docs", (_, v) => { if (ById<Button>(v, "Option:docs") is { } b) { Press(b); } }, "You answered · \u2068Docs first\u2069");
            await Answered("text", new Question { Id = "gallery-text", Kind = QuestionKind.Form, Message = "What should the release notes lead with?", AskedAt = now, Fields = [] }, "SendAnswer",
                (q, _) => { q.FreeText = "The quieter receipt, then the fixes\nand the rest after"; q.Send(); }, "You answered · \u2068The quieter receipt, then the fixes…\u2069");
            await Answered("form", new Question
            {
                Id = "gallery-form",
                Kind = QuestionKind.Form,
                Message = "Fill in the release.",
                AskedAt = now,
                Fields = [new Field { Name = "version", Title = "Version", Type = FieldType.Text, Required = true }, new Field { Name = "notes", Title = "Notes", Type = FieldType.Text }],
            }, "SendAnswer", (q, _) => { q.Fields[0].Text = "0.2.0"; q.Fields[1].Text = "Quieter receipts."; q.Send(); }, "You answered");
        }
        finally
        {
            foreach (var id in ids) { demo.Script(id, null); }
            await Task.WhenAll(ids.Select(demo.RemoveSessionAsync));
            result[$"progress.{pass}"] = drawn;
        }
    }
}
