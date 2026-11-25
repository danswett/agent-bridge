using System.Globalization;

namespace AgentBridge.App.Core;

// When this machine installs by itself (#4468, slice 2; owner, Nov 24 ~08:15 PT: "Window 02:00-05:00 PT"): the update
// window, any time, or a hold, per machine and profile, kept in the app's preferences for AutoUpdater to read at its next
// look (InstallWindow.Of). A person's own Restart to update is never limited by any of them.
public sealed partial class SettingsViewModel
{
    readonly TimeProvider clock;

    public const string InstallAnyTimeNote = "Installs by itself at any hour. Restart to update is never limited.";
    public const string InstallHeldNote = "Nothing installs by itself. Restart to update still works.";
    public const string InstallHeldWaiting = "An update is ready; it is held until you turn the hold off or choose Restart to update.";

    // 0 the update window, 1 any time, 2 hold: the order Settings lists them in.
    public int InstallWhenIndex
    {
        get => InstallWindow.Of(prefs()) is { Held: true } ? 2 : InstallWindow.Of(prefs()).AllDay ? 1 : 0;
        set
        {
            if (value is < 0 or > 2 || value == InstallWhenIndex) { return; }
            var p = prefs();
            Save(value switch
            {
                2 => p with { InstallHold = true },
                1 => p with { InstallHold = false, InstallWindowStart = "00:00", InstallWindowEnd = "00:00" },
                // Back to the window: the hours a person set and kept through a hold stay; all day gives way to the default.
                _ => InstallWindow.Of(p with { InstallHold = false }).AllDay
                    ? p with { InstallHold = false, InstallWindowStart = null, InstallWindowEnd = null }
                    : p with { InstallHold = false },
            });
        }
    }

    public bool InstallWindowEditable => InstallWhenIndex == 0;

    public TimeSpan InstallWindowStartTime { get => InstallWindow.Of(prefs()).Start.ToTimeSpan(); set => SetInstallTime(value, start: true); }
    public TimeSpan InstallWindowEndTime { get => InstallWindow.Of(prefs()).End.ToTimeSpan(); set => SetInstallTime(value, start: false); }

    // A start the same as the end would be all day, which is its own choice, so it is refused; the picker is told to show
    // what is kept. So is anything that is not a time of day.
    void SetInstallTime(TimeSpan value, bool start)
    {
        var window = InstallWindow.Of(prefs());
        var other = start ? window.End : window.Start;
        if (value >= TimeSpan.Zero && value < TimeSpan.FromDays(1) && new TimeOnly(value.Hours, value.Minutes) is var chosen && chosen != other)
        {
            if (chosen != (start ? window.Start : window.End)) { SaveInstallHours(start ? chosen : other, start ? other : chosen); }
            return;
        }
        Raise(start ? nameof(InstallWindowStartTime) : nameof(InstallWindowEndTime));
    }

    void SaveInstallHours(TimeOnly start, TimeOnly end) =>
        Save(prefs() with { InstallWindowStart = Hhmm(start), InstallWindowEnd = Hhmm(end) });

    static string Hhmm(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);

    // What the choice does, in one sentence (ui.md §8).
    public string InstallWhenNote
    {
        get
        {
            var w = InstallWindow.Of(prefs());
            if (w.Held) { return InstallHeldNote; }
            return w.AllDay ? InstallAnyTimeNote : $"Installs by itself only from {w.Start:HH\\:mm} to {w.End:HH\\:mm} {w.ZoneLabel}. Restart to update is never limited.";
        }
    }

    // Said where a person looks: a release downloaded that the window or the hold keeps waiting, and when it goes. Only
    // for what installs by itself (Install between turns, with downloads on); a press needs no window.
    public string InstallWaitNote
    {
        get
        {
            var p = prefs();
            if (!p.AutoUpdate || p.WhenReady != UpdateWhenReady.InstallBetweenTurns || updater.InstalledVersion is null || updater.Ready is null) { return ""; }
            var w = InstallWindow.Of(p);
            if (w.Held) { return InstallHeldWaiting; }
            return w.IsOpen(clock.GetUtcNow()) ? "" : $"An update is ready; it installs at {w.Start:HH\\:mm} {w.ZoneLabel}.";
        }
    }

    public bool HasInstallWaitNote => InstallWaitNote.Length > 0;
}
