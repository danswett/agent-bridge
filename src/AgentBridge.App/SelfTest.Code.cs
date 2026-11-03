using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// A response's code blocks (#296): a long command wraps in the block rather than scrolling off its
// side, and the block's Copy button puts the command on the clipboard exactly as it was written.
static partial class SelfTest
{
    const string LongCommand = "pwsh -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\me\\repos\\agent-bridge\\tools\\rehearsal\\run-rehearsal.ps1\" -Profile dev -Machine DEV-VM2 -Checklist docs\\parity-checklist.md -Evidence out\\evidence -Verbose";

    static async Task CheckCodeBlocks(MainWindow window, Action<bool, string> check, string outFile)
    {
        var put = MarkdownView.PutText;
        var copied = new List<string>();
        // Never the real clipboard: the person at the machine may have something on it.
        MarkdownView.PutText = text => { copied.Add(text); return true; };
        var md = new MarkdownView
        {
            Reading = true,
            Markdown = $"Run the rehearsal:\n\n```powershell\n{LongCommand}\n```\n\nThen:\n\n```\ngit status\ngit log -1\n```",
        };
        var host = new Border { Width = 420, Padding = new Thickness(12), Background = Ui.Brush("AbCard"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = md };
        var root = (Panel)window.RootElement;
        root.Children.Add(host);
        try
        {
            host.UpdateLayout();
            await Until(() => Find<TextBlock>(md).Any(t => t.Text == LongCommand && t.ActualHeight > 0), TimeSpan.FromSeconds(5));
            var code = Find<TextBlock>(md).FirstOrDefault(t => t.Text == LongCommand);
            var line = code is null ? 0 : code.LineHeight;
            check(code is { TextWrapping: TextWrapping.Wrap } && code.ActualHeight >= 2 * line && Find<ScrollViewer>(md).Count == 0,
                $"wraps a long command in a code block rather than scrolling it ({code?.ActualHeight:0} px high, {line:0} a line)");

            var buttons = Find<Button>(md).Where(b => AutomationProperties.GetAutomationId(b) == "CopyCode").ToList();
            check(buttons.Count == 2 && buttons.All(b => AutomationProperties.GetName(b) == CodeCopy.Name && b.IsTabStop && b.ActualHeight >= 32),
                "gives every code block a Copy button named Copy code that the keyboard reaches");
            if (buttons.Count == 2)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(buttons[0]).GetPattern(PatternInterface.Invoke)).Invoke();
                ((IInvokeProvider)new ButtonAutomationPeer(buttons[1]).GetPattern(PatternInterface.Invoke)).Invoke();
                await Until(() => copied.Count == 2, TimeSpan.FromSeconds(2));
                check(copied.SequenceEqual([LongCommand, "git status\ngit log -1"]), "copies each code block's text exactly, a wrapped command as the one line it is");
                var said = Find<TextBlock>(buttons[0]).Select(t => t.Text).ToList();
                check(said.Contains(CodeCopy.Done), $"says Copied on the button once copied ({string.Join(", ", said)})");
                await Picture(host, Path.ChangeExtension(outFile, ".code.png"));
            }
        }
        finally
        {
            root.Children.Remove(host);
            MarkdownView.PutText = put;
        }
    }
}
