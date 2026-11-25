using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The app the host starts headless with --update-only on a machine whose app is closed
// (docs/protocol/v1-update.md): it downloads a newer release and installs it at the first gap
// between turns, by AutoUpdater's own path and refusals, whatever the person chose for "When an
// update is ready" (nobody is there to press Restart to update). It shows nothing, and it ends once
// there is nothing left for it to do, so the host can start another for a later release.
public sealed class UpdateOnlyRun(Func<UpdateState> state, TimeProvider time)
{
    // AutoUpdater's backstop, and a little more: past it the run has been through every wait the app
    // itself would, and a run that stayed would keep the host from starting another.
    public static readonly TimeSpan Limit = AutoUpdater.WaitAtMost + TimeSpan.FromMinutes(15);

    readonly DateTimeOffset began = time.GetUtcNow();

    // What this run acts on: the person's file, with the two choices that would hold it back set aside.
    public static AppPreferences Prefs(AppPreferences chosen) =>
        chosen with { AutoUpdate = true, WhenUpdateReady = UpdateWhenReady.InstallBetweenTurns };

    // What is written back: everything the run changed (an update's record, for the new version to
    // announce) except those two, which stay the person's.
    public static AppPreferences ForFile(AppPreferences next, AppPreferences chosen) =>
        next with { AutoUpdate = chosen.AutoUpdate, WhenUpdateReady = chosen.WhenUpdateReady };

    // Why the run is over, or null while it has something to do: a copy no updater can update, a
    // check that found nothing newer, or the limit. Nothing ends it while it checks, downloads,
    // waits for a gap or installs (an install ends it by stopping the app).
    public string? Finished()
    {
        var s = state();
        if (s.Current is null) { return "this copy was not installed by its updater, so it cannot update"; }
        if (time.GetUtcNow() - began >= Limit) { return "it waited as long as the app itself would"; }
        if (s.Phase == UpdatePhase.Idle && s.CheckedAt is not null && s.Available is null) { return "there is nothing newer"; }
        return null;
    }
}
