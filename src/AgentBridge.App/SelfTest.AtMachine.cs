using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckAtMachineShape(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check, string outFile)
    {
        var original = main.AllCards.Single(c => c.Id == "02d9e5d5-6b35-49bd-b083-39572fbff648").Session;
        // The listing for legacy {question: "Keep the old witness format?", choices: ["Yes", "No"]}.
        var question = new Question
        {
            Id = "legacy-887", Kind = QuestionKind.Form, Message = "Keep the old witness format?",
            AskedAt = DateTimeOffset.UtcNow, AnswerAtMachine = true, AnswerAtMachineReason = "shape",
            Fields = [new Field { Name = "answer", Type = FieldType.Choice, Options = [new("Yes"), new("No")], AllowOther = true, Required = true }],
        };
        try
        {
            Resize(window, 1440, 1000);
            await demo.Upsert(original with { Question = question, Status = SessionStatus.Waiting, UpdatedAt = DateTimeOffset.UtcNow });
            var view = await Choose(window, main, original.Id, "AtMachineExplanation");
            if (view is null) { check(false, "draws the legacy question's at-machine explanation"); return; }
            var card = ById<QuestionView>(view, "Question")!;
            var explanation = ById<TextBlock>(card, "AtMachineExplanation")!;
            var lines = Find<TextBlock>(card);
            var first = lines.FirstOrDefault(b => b.Text == "Answer at the machine · Demo machine");
            check(first is not null && lines.IndexOf(first) < lines.IndexOf(explanation)
                && explanation.Text == "This kind of question can only be answered at the machine."
                && AutomationProperties.GetLiveSetting(explanation) == AutomationLiveSetting.Off
                && new TextBlockAutomationPeer(explanation).GetName() == explanation.Text
                && Find<Button>(card).Count == 0 && Find<TextBox>(card).Count == 0 && Find<RadioButton>(card).Count == 0,
                "shows the legacy question whole with no answer controls, then the machine and the explanation, accessible in order without a live region");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                window.RootElement.RequestedTheme = theme;
                await Task.Delay(400);
                var colour = ((SolidColorBrush)Ui.ThemedBrush("AbNeedsText", theme)).Color;
                check(first?.Foreground is SolidColorBrush f && f.Color == colour && explanation.Foreground is SolidColorBrush e && e.Color == colour,
                    $"draws both at-machine lines in Needs you's text colour in {theme}");
                var key = theme.ToString();
                var shell = Application.Current.Resources.MergedDictionaries
                    .Select(d => d.ThemeDictionaries.TryGetValue(key, out var td) && td is ResourceDictionary rd && rd.TryGetValue("AbShell", out var b) && b is SolidColorBrush s ? s.Color : (Windows.UI.Color?)null)
                    .FirstOrDefault(c => c is not null) ?? default;
                await Picture(view, Path.ChangeExtension(outFile, $".at-machine.legacy.{theme.ToString().ToLowerInvariant()}.png"), shell);
            }
        }
        finally
        {
            window.RootElement.RequestedTheme = ElementTheme.Default;
            await demo.Upsert(original with { UpdatedAt = DateTimeOffset.UtcNow });
        }
    }
}
