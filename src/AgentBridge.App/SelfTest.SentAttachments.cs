using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckSentAttachments(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check)
    {
        var card = main.AllCards.First(c => c.Id == PromptSession);
        var original = card.Session;
        var folder = Path.Combine(Path.GetTempPath(), $"sent-images-{Guid.NewGuid():N}", "attachments");
        Directory.CreateDirectory(folder);
        var image = Path.Combine(folder, $"{Guid.NewGuid():N}-pasted.png");
        var missing = Path.Combine(folder, $"{Guid.NewGuid():N}-missing.png");
        var document = Path.Combine(folder, $"{Guid.NewGuid():N}-report.pdf");
        await File.WriteAllBytesAsync(image, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+ip1sAAAAASUVORK5CYII="));
        await File.WriteAllTextAsync(document, "not an image");
        try
        {
            await demo.Upsert(original with
            {
                Status = SessionStatus.Idle, Question = null, UpdatedAt = DateTimeOffset.UtcNow,
                Activity = original.Activity with { UserMessage = $"@{image}\n@{missing}\n@{document}\nWhat is this?", UserMessageFrom = UserMessageFrom.Person },
            });
            var view = await Choose(window, main, PromptSession, "Response");
            if (view is null) { check(false, "opens a session to check sent attachments"); return; }
            Border? Bubble() => ById<Border>(view, card.UserMessageBelow ? "YourMessageBelow" : "YourMessage");
            await Until(() => Bubble() is { } b && ById<Image>(b, "SentImage")?.Source is BitmapImage { PixelWidth: > 0 }, TimeSpan.FromSeconds(10));
            var bubble = Bubble();
            var thumbnail = bubble is null ? null : ById<Button>(bubble, "SentAttachment");
            var picture = bubble is null ? null : ById<Image>(bubble, "SentImage");
            var text = bubble is null ? "" : string.Join("\n", Find<TextBlock>(bubble).Select(t => t.Text));
            check(picture?.Source is BitmapImage { PixelWidth: > 0 } && thumbnail is { ActualWidth: >= 44 and <= 240, ActualHeight: >= 44 and <= 240, CornerRadius.TopLeft: 8 }
                && AutomationProperties.GetName(thumbnail).StartsWith("Image, Pasted image,", StringComparison.Ordinal),
                "renders a disk-backed pasted image as a bounded, rounded, accessible thumbnail");
            check(text.Contains("What is this?", StringComparison.Ordinal) && text.Contains("missing.png", StringComparison.Ordinal) && text.Contains("report.pdf", StringComparison.Ordinal)
                && text.Contains("Not available on this device", StringComparison.Ordinal) && !text.Contains(folder, StringComparison.Ordinal) && !text.Contains("@", StringComparison.Ordinal),
                "keeps message words and named missing-file/document chips without attachment paths");
            if (thumbnail is not null)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(thumbnail).GetPattern(PatternInterface.Invoke)).Invoke();
                ContentDialog? Dialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot)
                    .SelectMany(p => p.Child is ContentDialog d ? new[] { d } : p.Child is { } child ? Find<ContentDialog>(child).ToArray() : []).FirstOrDefault();
                await Until(() => Dialog() is not null, TimeSpan.FromSeconds(5));
                var viewer = Dialog();
                check(viewer is { CloseButtonText: "Close" } && Find<Image>(viewer).Any(i => i.Source is BitmapImage { PixelWidth: > 0 })
                    && !viewer.Title.ToString()!.Contains(folder, StringComparison.Ordinal), "opens the image larger in a path-free viewer with Close");
                var close = viewer is null ? null : Find<Button>(viewer).FirstOrDefault(b => b.Name == "CloseButton");
                if (close is not null) { ((IInvokeProvider)new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke)).Invoke(); }
                check(await Until(() => Dialog() is null, TimeSpan.FromSeconds(5)), "closes the enlarged image through its Close button");
                viewer?.Hide();
            }
            card.Stored?.AddLive(new HistoryTurn
            {
                Id = "sent-image-history", At = DateTimeOffset.UtcNow.AddMinutes(-1),
                Prompt = new HistoryPrompt { Text = $"@{image}" },
                Parts = [new HistoryPart { Id = "sent-image-history-reply", Kind = HistoryPartKind.Response, At = DateTimeOffset.UtcNow.AddMinutes(-1), Text = "An earlier image." }],
            });
            check(await Until(() => Find<Border>(view).Any(b => b.Name == "HistoryYourMessage"
                && ById<Image>(b, "SentImage")?.Source is BitmapImage { PixelWidth: > 0 }
                && Find<TextBlock>(b).All(t => !t.Text.Contains(folder, StringComparison.Ordinal))), TimeSpan.FromSeconds(10)),
                "renders an image-only historical sent bubble as a thumbnail without path text");
        }
        finally
        {
            await demo.Upsert(original with { UpdatedAt = DateTimeOffset.UtcNow });
            File.Delete(image);
            File.Delete(document);
            Directory.Delete(folder);
            Directory.Delete(Path.GetDirectoryName(folder)!);
        }
    }
}
