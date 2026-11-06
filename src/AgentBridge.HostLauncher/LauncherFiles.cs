using AgentBridge.Local;

namespace AgentBridge.HostLauncher;

// The serving launcher's files in the profile's folder, which the app reads and writes too
// (HostLauncherContract): the app's stop marker, and the launcher's record of itself.
public sealed class LauncherFiles(string profileFolder, Action<string> log) : IDisposable
{
    // Only a serving launcher's: `shadow run`, run by a launcher of its own beside the serving one
    // on the same profile (docs/host-shadow.md), must neither clear the app's stop nor take the
    // serving launcher's record (#420's review).
    public static LauncherFiles? ForServe(string[] args, string? profileFolder, Action<string> log) =>
        args.FirstOrDefault() == "serve" && profileFolder is not null ? new LauncherFiles(profileFolder, log) : null;

    string Marker => Path.Combine(profileFolder, HostLauncherContract.StopMarker);
    string Record => Path.Combine(profileFolder, HostLauncherContract.LauncherRecord);

    // The record, while this launcher holds it.
    FileStream? record;
    bool told;

    public bool Stopped() => File.Exists(Marker);

    // Forgets a stop asked for before this launcher started: starting one (at sign-in, or the
    // app's Start, which clears the marker itself first) is a start on purpose. Not one asked for
    // since, by an app that could not yet find this launcher (it had not reached its record):
    // that stop stands, and the look before each start honours it (#420's review). The look at
    // its time and the delete are one step under the marker's lock, which the app writes it
    // under: between them, a Stop's new marker was deleted as the earlier one (#478 review,
    // Copilot 4470090733). Without the lock nothing is deleted, and the stop stands.
    public void ClearStoppedBefore(DateTime startedUtc)
    {
        try
        {
            if (!File.Exists(Marker)) { return; }
            using var held = HostLauncherContract.LockStopMarker(profileFolder, LockWait);
            if (held is null)
            {
                log("an earlier stop was left in place: its lock could not be had");
                return;
            }
            if (File.Exists(Marker) && File.GetLastWriteTimeUtc(Marker) < startedUtc)
            {
                BetweenReadAndDelete?.Invoke();
                File.Delete(Marker);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"an earlier stop could not be cleared: {e.Message}"); }
    }

    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);

    // For a test alone, set by reflection: a Stop put between the look at the marker's time and
    // its delete. Nothing in the launcher sets it.
    internal Action? BetweenReadAndDelete { get; set; }

    // So an app opened after this started, with no logon task to ask, can find and end it (#420).
    // Held open, readable by others but writable by none, and deleted as its handle closes: a
    // second launcher cannot write over a live one's record, a launcher killed leaves none
    // behind, and none deletes another's. Tried again before each start of the host, so one that
    // could not hold it (another launcher had it, a transient sharing violation) takes it once
    // free. Whether this launcher holds it now: a host started without it is told to look for
    // the app's stop for itself (HostLauncherContract.CheckStopVariable).
    public bool HoldRecord(int pid, DateTime startedUtc)
    {
        if (record is not null) { return true; }
        try
        {
            Directory.CreateDirectory(profileFolder);
            var held = new FileStream(Record, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.DeleteOnClose);
            try
            {
                held.SetLength(0);
                held.Write(System.Text.Encoding.UTF8.GetBytes(HostLauncherContract.FormatLauncher(pid, startedUtc)));
                held.Flush();
            }
            catch
            {
                held.Dispose();
                throw;
            }
            record = held;
            told = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!told) { log($"this launcher could not record itself for the app, so the host will look for a stop itself: {e.Message}"); }
            told = true;
            return false;
        }
    }

    public void Dispose()
    {
        record?.Dispose();
        record = null;
    }

    // Whether this launcher holds its record now.
    public bool Holding => record is not null;
}
