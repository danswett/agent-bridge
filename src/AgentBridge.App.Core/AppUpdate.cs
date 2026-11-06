using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// What restarting to update would interrupt on this machine (#516): Restart to update says so
// first, and goes ahead only once the person has read it. Restarting stops the host for a moment;
// the agents' consoles keep running, but a question or approval the host was carrying for them
// may then have to be answered at the machine (host-hooks.md, "Approvals across an outage").
public static class RestartWarning
{
    // Null when nothing here is waiting on the person, or the host is not running (it carries
    // nothing then, and the new version starts it). A host still running but not connected now
    // (reconnecting after its pipe dropped, or refusing this app) may be carrying questions the
    // app can't see: not knowing is said, as for a list still being read.
    public static string? For(HostConnection host)
    {
        if (host.State == LinkState.NotRunning) { return null; }
        if (host.State != LinkState.Connected)
        {
            return "This machine's host isn't connected right now, so whether a session is waiting on you can't be told. "
                + "Restarting stops the host for a moment, and a question or approval waiting may then have to be answered at the machine.";
        }
        // A list still being read may be missing the busy session (as AutoUpdater.Idle): not
        // knowing is said, not taken for nothing waiting.
        if (!host.Sessions.Complete || host.Sessions.Unsure)
        {
            return "This machine's sessions are still being read, so whether one is waiting on you can't be told yet. "
                + "Restarting stops the host for a moment, and a question or approval waiting may then have to be answered at the machine.";
        }
        return For(host.Sessions.All);
    }

    public static string? For(IEnumerable<Session> sessions)
    {
        var all = sessions.ToList();
        var asking = all.Count(s => s.Question is not null || s.Status == SessionStatus.Waiting);
        var working = all.Count(s => s.Question is null && s.Status == SessionStatus.Working);
        if (asking == 0 && working == 0) { return null; }
        var parts = new List<string>();
        if (asking > 0) { parts.Add($"{Count(asking)} {(asking == 1 ? "is" : "are")} waiting on you"); }
        if (working > 0) { parts.Add($"{(asking > 0 ? working.ToString(System.Globalization.CultureInfo.InvariantCulture) : Count(working))} {(working == 1 ? "has" : "have")} a turn running"); }
        return $"{string.Join(", and ", parts)}. Restarting stops this machine's host for a moment: the agents keep running, "
            + "but a question or approval waiting may then have to be answered at the machine.";
    }

    static string Count(int n) => n == 1 ? "1 session here" : $"{n} sessions here";
}

// This machine's update once it is downloaded (#516): the app's banner, over every page, with
// Restart to update. It stays while the release waits, and nothing restarts until they press
// it. It is the only signal: no toast says an update is ready, as one can wait (Creative, #719).
public sealed class AppUpdateViewModel : Observable
{
    readonly Func<string?> ready;
    readonly Func<Func<string, Task<bool>>?, Task<string?>> restart;
    string? problem;
    bool restarting;

    // None: the demo and a copy the updater cannot update.
    public AppUpdateViewModel() : this(() => null, _ => Task.FromResult<string?>(AutoUpdater.NotInstalled)) { }

    // restart(confirm): AutoUpdater.RestartToUpdateAsync, which asks confirm(warning) itself, last,
    // when a session here is busy.
    public AppUpdateViewModel(Func<string?> ready, Func<Func<string, Task<bool>>?, Task<string?>> restart)
    {
        this.ready = ready;
        this.restart = restart;
    }

    public static AppUpdateViewModel For(AutoUpdater auto) => new(() => auto.Ready, auto.RestartToUpdateAsync);

    public string? Version => ready();
    public bool Shown => restarting || Version is not null || problem is not null;
    public bool CanRestart => !restarting && Version is not null;
    // The button stays while a restart is being checked, so it keeps keyboard focus, and the
    // confirmation dialog has it to return to (ui.md §9.1 A4); RestartAsync ignores a second press.
    public bool Offered => restarting || Version is not null;
    public string Message => restarting ? "Restarting to update\u2026"
        : problem is { } p ? DidNotInstall(p, Version is not null)
        : Version is { } v ? Ready(v)
        : "";
    // The bar's state, for its severity and its button (Creative on #719, 6675195544): a failed
    // install is a warning, and its button is Try again; while restarting, the button shows its
    // progress ring and says so to a screen reader, ignores presses, and keeps focus.
    public bool Restarting => restarting;
    public bool Failed => !restarting && problem is not null;
    public string ActionLabel => Failed && Version is not null ? "Try again" : "Restart to update";
    public string ActionStatus => restarting ? "Restarting\u2026" : "";

    // The bar's words, and Settings' (ui.md §8: what it is and the next step, nothing to explain).
    public static string Ready(string version) => $"Version {version} is ready. Restart to update.";

    // An install that failed, in one sentence, its reason a kind, never the machine's detail (a
    // process id, a path, an error's own text: those are in the log). While the release is still
    // ready the bar's button, Try again, is the next step; otherwise the background download that
    // offers it again (Creative on #719, 6675195544).
    public static string DidNotInstall(string why, bool again) =>
        $"The update didn't install because {Reason(why)}{(again ? "" : $"; {Product.Name} offers it again once it is downloaded")}.";

    // What kind of reason the updater gave, as the person is told it.
    public static string Reason(string why) => why switch
    {
        _ when why.Contains("runs from this installation", StringComparison.Ordinal) || why.Contains("runs and cannot be looked at", StringComparison.Ordinal)
            => "another bridge profile is running from this installation",
        _ when why.Contains("already being installed", StringComparison.Ordinal) => "another update is already being installed",
        _ when why.StartsWith("the update downloaded changed", StringComparison.Ordinal) || why.StartsWith("the update downloaded has changed", StringComparison.Ordinal)
            || why.Contains("is no longer the update downloaded", StringComparison.Ordinal)
            => "a newer update was downloaded meanwhile",
        _ when why == AutoUpdater.NotInstalled => "this copy of the app wasn't installed by its updater",
        _ when why.StartsWith("no update has been downloaded", StringComparison.Ordinal) || why.StartsWith("no downloaded update", StringComparison.Ordinal)
            => "nothing has been downloaded yet",
        _ when why.StartsWith("the host's status could not be read", StringComparison.Ordinal) => "the host didn't answer",
        _ when why.StartsWith("installing ", StringComparison.Ordinal) && why.Contains(" failed: ", StringComparison.Ordinal) => "the installer reported an error",
        _ => "of an unexpected error",
    };

    // The bar's words changed (ready, restarting, failed): MainWindow raises the bar's polite live
    // region's LiveRegionChanged, once per change (ui.md §9.1 A6).
    public event Action? Announced;
    string said = "";

    void Notify()
    {
        RaiseAll();
        if (Message is var now && now != said)
        {
            said = now;
            if (now.Length > 0) { Announced?.Invoke(); }
        }
    }

    // confirm(warning): the dialog that says what a restart would interrupt; true to go ahead.
    // The one restart, whichever control started it: the banner's, or Settings' (SettingsViewModel
    // .ShareRestart), so both show its progress and how it ended (Engineering's second-family
    // review of #719, 6676638175; Creative, 6676653935). Null once handed to Velopack;
    // AutoUpdater.NotNow if the person chose not to; otherwise why it did not install.
    public async Task<string?> RestartAsync(Func<string, Task<bool>>? confirm)
    {
        // Not CanRestart: a release Settings downloaded itself is not the banner's until the updater
        // takes it (RestartToUpdateAsync adopts it), so Settings' press must reach the updater even
        // with no version here; the banner's button shows only with one (Engineering's
        // Claude-family review of #719, 6677216492).
        if (restarting) { return "an update is already being installed"; }
        restarting = true;
        problem = null;
        Notify();
        string? why = null;
        try
        {
            // The banner says it is restarting only once the person has said to.
            why = await restart(confirm is null ? null : async warning =>
            {
                restarting = false;
                Notify();
                var go = await confirm(warning);
                restarting = go;
                Notify();
                return go;
            });
            problem = why == AutoUpdater.NotNow ? null : why;
            return why;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { problem = e.Message; return e.Message; }
        finally
        {
            restarting = false;
            // What was ready once it failed: the problem is said until another release is ready,
            // or the next press.
            problemWith = Version;
            Notify();
        }
    }

    // The updater changed: a release downloaded, replaced or gone. A release ready now that was not
    // when the restart failed makes that failure old news, so it no longer stands in for the offer.
    public void Changed()
    {
        if (problem is not null && Version is { } now && now != problemWith) { problem = null; }
        Notify();
    }
    string? problemWith;
}