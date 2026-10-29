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
    public static async Task RunAsync(MainWindow window, MainViewModel main, string outFile, DemoHost demo, Action<int> exit)
    {
        var result = new Dictionary<string, object?>();
        var checks = new List<string>();
        var failures = new List<string>();
        void Check(bool ok, string what) { (ok ? checks : failures).Add(what); }
        try
        {
            var expected = demo.Count;
            result["expectedCards"] = expected;
            var drawn = await Until(() => Find<SessionCard>(window.RootElement).Count == expected, TimeSpan.FromSeconds(60));
            var cards = Find<SessionCard>(window.RootElement);
            result["drawnCards"] = cards.Count;
            Check(drawn, $"draws all {expected} demo sessions as cards");
            Check(main.Waiting == 3, "counts three sessions waiting on you");

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

            window.ShowPage("usage");
            await Task.Delay(500);
            Check(main.Usage.Rows.Count == 2, "shows usage for two agents");
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".usage.png"));
            window.ShowPage("settings");
            await Task.Delay(500);
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
