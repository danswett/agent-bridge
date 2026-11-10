using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// The move strip (#1716) drawn from a fake step stream (S5, #1712, not landed) as result.move.<state>.png, with UIA names checked.
static partial class SelfTest
{
    static async Task CheckMoveStrip(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check, string outFile, Dictionary<string, object?> result)
    {
        var id = "move-strip";
        var drawn = new List<string>();
        try
        {
            await demo.PutAsync(new Session { Id = id, Agent = AgentKind.Copilot, Title = "Tidy the release notes", Repository = "danswett/agent-bridge", Branch = "main", Status = SessionStatus.Idle, StatusSince = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Activity = new Activity { Response = "The notes are drafted.", TurnCount = 3 } });
            await Until(() => main.AllCards.Any(c => c.Id == id), TimeSpan.FromSeconds(10));
            if (await Choose(window, main, id, "SendReply") is not { } view || main.AllCards.First(c => c.Id == id) is not { } card) { check(false, "opens the move strip's session"); return; }

            string[] Names() => Find<FrameworkElement>(view).Where(e => AutomationProperties.GetAutomationId(e) == "MoveStep").Select(AutomationProperties.GetName).ToArray();
            async Task Draw(string state, IEnumerable<MoveEvent> events)
            {
                var move = card.Move = new MoveProgressViewModel("Machine A", "Machine B");
                foreach (var e in events) { move.Apply(e); }
                await Task.Delay(500);
                var name = $".move.{state}.png";
                await Picture(window.Sessions.SessionPaneView, Path.ChangeExtension(outFile, name));
                drawn.Add($"result{name}");
            }

            await Draw("moving", FakeMoveSteps.Succeeding().Take(2));
            check(ById<FrameworkElement>(view, "MoveStrip") is { } strip && AutomationProperties.GetName(strip) == "Moving from Machine A to Machine B", "names the move region for a screen reader");
            var names = Names();
            check(names.Length == 5 && names[0].StartsWith("1 Hand-off requested, done", StringComparison.Ordinal) && names[1].Contains("done · 3 s", StringComparison.Ordinal) && names[2].Contains("working", StringComparison.Ordinal), $"names each step with its state ({string.Join(" | ", names)})");
            check(card.Reply is { CanSend: false } && card.RowStatus.Contains("Moving to Machine B", StringComparison.Ordinal), "holds Send and says it on the row while it moves");

            await Draw("failed", FakeMoveSteps.FailingAt(MoveStepName.ArtifactsPushed, "Machine B didn't take the files. Pick another machine or try again."));
            names = Names();
            check(names.Length == 5 && names[2].StartsWith("3 Files sent to Machine B, failed", StringComparison.Ordinal) && names[3].Contains("not started", StringComparison.Ordinal), $"names a failed step and the ones after it ({string.Join(" | ", names)})");
            check(ById<Button>(view, "MoveTryAgain")?.Visibility == Visibility.Visible && ById<Button>(view, "MoveGoTo")?.Visibility == Visibility.Collapsed, "offers Try again, not Go to it, once failed");

            await Draw("moved", FakeMoveSteps.Succeeding());
            check(ById<Button>(view, "MoveGoTo")?.Visibility == Visibility.Visible && Names().All(n => n.Contains("done", StringComparison.Ordinal)), "offers Go to it once moved, every step done");
            card.Move = null;
        }
        finally
        {
            await demo.RemoveSessionAsync(id);
            result["move"] = drawn;
        }
    }
}
