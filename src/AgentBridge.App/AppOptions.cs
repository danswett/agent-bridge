using AgentBridge.App.Core;
using AgentBridge.Host.Core.Diagnostics;
using AgentBridge.Transport;
using Microsoft.UI.Dispatching;

namespace AgentBridge.App;

// What the command line asked for. Velopack's own hook arguments are not ours: Program runs
// Velopack first, which handles and exits on those.
sealed record AppOptions
{
    public string Profile { get; init; } = "default";
    // Started at sign-in: into the notification area, without a window.
    public bool Background { get; init; }
    // Windows started this process to deliver a toast click (the activator's command line).
    public bool ToastActivated { get; init; }
    // A pretend host inside the app, for trying it without agents and for CI.
    public bool Demo { get; init; }
    // Draw the demo's cards, write what was drawn to this file and exit (CI's smoke test).
    public string? SelfTest { get; init; }
    public bool HistoryScrollBudget { get; init; }
    public bool LiveListReplay { get; init; }
    public string? SelfTestGroup { get; init; }

    public static AppOptions Parse(string[] args)
    {
        var o = new AppOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Length && Identity.IsValidProfile(args[i + 1]): o = o with { Profile = args[++i] }; break;
                case "--background": o = o with { Background = true }; break;
                case "-ToastActivated" or "--toast-activated": o = o with { ToastActivated = true }; break;
                case "--demo": o = o with { Demo = true }; break;
                case "--self-test" when i + 1 < args.Length: o = o with { SelfTest = Path.GetFullPath(args[++i]), Demo = true }; break;
                case "--self-test-history-scroll" when i + 1 < args.Length: o = o with { SelfTest = Path.GetFullPath(args[++i]), Demo = true, HistoryScrollBudget = true }; break;
                case "--self-test-live-list" when i + 1 < args.Length: o = o with { SelfTest = Path.GetFullPath(args[++i]), Demo = true, LiveListReplay = true }; break;
                case "--self-test-group":
                    if (o.SelfTestGroup is not null || i + 1 == args.Length ||
                        args[i + 1].ToLowerInvariant() is not ("history" or "wheel"))
                    {
                        throw new ArgumentException("--self-test-group requires exactly one of: history, wheel.");
                    }
                    o = o with { SelfTestGroup = args[++i].ToLowerInvariant() };
                    break;
            }
        }
        if (o.SelfTestGroup is not null && (o.SelfTest is null || o.HistoryScrollBudget || o.LiveListReplay))
        {
            throw new ArgumentException("--self-test-group requires --self-test <result> and cannot be combined with a legacy self-test selector.");
        }
        return o;
    }

    public Task RunSelfTestAsync(Func<Task> full, Func<Task> history, Func<Task> wheel, Func<Task> liveList) =>
        LiveListReplay ? liveList() : HistoryScrollBudget ? wheel() : SelfTestGroup switch
        {
            null => full(),
            "history" => history(),
            "wheel" => wheel(),
            _ => throw new ArgumentException("Unknown self-test group."),
        };

    // The default profile is the installed app's; another (a developer's) keeps its own
    // toasts, Run entry and single-instance lock out of the way of it.
    public bool IsDefaultProfile => Profile == "default";
}

static class Paths
{
    public static string? DiagnosticRoot { get; set; }
    public static string DataRoot => Identity.DefaultRoot;
    public static string AppData => DiagnosticRoot ?? Path.Combine(DataRoot, "app");
    public static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AgentBridge.exe");
}

static class CrashLog
{
    // The app's own diagnostics journal (#737): the crash as its type and frames only, beside the
    // crash.log line, which stays as it was for the person's own reading.
    static readonly Lazy<DiagnosticsJournal> Journal = new(() => new DiagnosticsJournal(TimeProvider.System, Path.Combine(Paths.AppData, "diagnostics-journal.jsonl")));

    // A XAML failure surfaces only as 0xC000027B in Microsoft.UI.Xaml.dll with the real
    // exception lost (S3), so every unhandled exception is written down first.
    public static void Write(Exception? e)
    {
        try
        {
            Directory.CreateDirectory(Paths.AppData);
            File.AppendAllText(Path.Combine(Paths.AppData, "crash.log"), $"{DateTime.Now:O} {e}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Journal.Value.Record(JournalEvents.Crash(e, JournalEvents.Version(typeof(CrashLog).Assembly)));
    }
}

static class AppLog
{
    static readonly object Gate = new();

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.AppData);
                var file = Path.Combine(Paths.AppData, "app.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) { File.Move(file, file + ".1", overwrite: true); }
                File.AppendAllText(file, $"{DateTime.Now:O} {line}\n");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

// View models change on the UI thread; the host's events arrive on the pipe's.
sealed class QueueDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (queue.HasThreadAccess) { action(); }
        else { queue.TryEnqueue(() => action()); }
    }
}
