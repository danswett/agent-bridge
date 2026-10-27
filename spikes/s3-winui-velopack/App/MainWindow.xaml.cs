using System.Reflection;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Velopack;
using Windows.Foundation.Collections;

namespace AgentBridgeS3;

public sealed partial class MainWindow : Window
{
    // Outside the Velopack install folder, which an update replaces.
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridgeS3Data");
    static string FeedFile => Path.Combine(DataDir, "feed.txt");
    static string LogFile => Path.Combine(DataDir, "activations.log");

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(DataDir);
        var installed = new UpdateManager(ReadFeed() ?? DataDir).CurrentVersion?.ToString();
        var assembly = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = $"Version: {installed ?? "not installed (dev run)"} (assembly {assembly})";
    }

    public void SetLaunch(string how) => LaunchText.Text = $"Launched: {how}";

    // A toast mirrors an ask_user question: option buttons, plus a text box for "Other".
    void ShowQuestion()
    {
        var q = DateTime.UtcNow.ToString("HHmmss");
        // The toast-level argument is copied onto every button, so q comes back from all of them.
        new ToastContentBuilder()
            .AddArgument("q", q)
            .AddText("copilot · agent-bridge asks")
            .AddText("Which database should I use?")
            .AddInputTextBox("other", "Type your own answer", "Other")
            .AddButton(new ToastButton().SetContent("Send").AddArgument("action", "other").SetTextBoxId("other"))
            .AddButton(new ToastButton().SetContent("PostgreSQL (Recommended)").AddArgument("action", "choose").AddArgument("choice", "PostgreSQL"))
            .AddButton(new ToastButton().SetContent("SQLite").AddArgument("action", "choose").AddArgument("choice", "SQLite"))
            .Show(t => t.Tag = "session-1");
        Log($"shown q={q}");
    }

    public void ShowAnswer(string how, ToastArguments args, ValueSet input)
    {
        args.TryGetValue("action", out var action);
        args.TryGetValue("choice", out var choice);
        args.TryGetValue("q", out var q);
        var typed = input.TryGetValue("other", out var o) ? o as string : null;
        var text = action switch
        {
            "choose" => $"chose \"{choice}\"",
            "other" => $"typed \"{typed}\"",
            _ => "opened the toast",
        };
        AnswerText.Text = $"{text} (q={q}, {how})";
        Log($"answer {how}: {text} q={q}");
    }

    void ShowToast_Click(object sender, RoutedEventArgs e) => ShowQuestion();

    async void ToastAndExit_Click(object sender, RoutedEventArgs e)
    {
        ShowQuestion();
        await Task.Delay(500);
        Application.Current.Exit();
    }

    async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var feed = ReadFeed();
        if (feed == null) { UpdateText.Text = $"No feed configured ({FeedFile})"; return; }
        try
        {
            var mgr = new UpdateManager(feed);
            if (!mgr.IsInstalled) { UpdateText.Text = "Not installed by Velopack (dev run)"; return; }
            UpdateText.Text = "Checking…";
            var update = await mgr.CheckForUpdatesAsync();
            if (update == null) { UpdateText.Text = $"Up to date ({mgr.CurrentVersion})"; return; }
            UpdateText.Text = $"Downloading {update.TargetFullRelease.Version}…";
            await mgr.DownloadUpdatesAsync(update);
            Log($"applying {update.TargetFullRelease.Version}");
            mgr.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex) { UpdateText.Text = $"Update failed: {ex.GetType().Name}: {ex.Message}"; }
    }

    static string? ReadFeed() => File.Exists(FeedFile) ? File.ReadAllText(FeedFile).Trim() : null;

    static void Log(string line) => File.AppendAllText(LogFile, $"{DateTime.Now:O} {line}{Environment.NewLine}");
}
