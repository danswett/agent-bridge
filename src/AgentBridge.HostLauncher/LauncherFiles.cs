using AgentBridge.Local;

namespace AgentBridge.HostLauncher;

// The launcher's files in the profile's folder, which the app reads and writes too
// (HostLauncherContract): the app's stop marker, and the launcher's record of itself.
public sealed class LauncherFiles(string profileFolder, Action<string> log)
{
    string Marker => Path.Combine(profileFolder, HostLauncherContract.StopMarker);
    string Record => Path.Combine(profileFolder, HostLauncherContract.LauncherRecord);

    public bool Stopped() => File.Exists(Marker);

    public void ClearStopped()
    {
        try { File.Delete(Marker); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"an earlier stop could not be cleared: {e.Message}"); }
    }

    // So an app opened after this started, with no logon task to ask, can find and end it (#420).
    public void RecordSelf(int pid, DateTime startedUtc)
    {
        try
        {
            Directory.CreateDirectory(profileFolder);
            File.WriteAllText(Record, HostLauncherContract.FormatLauncher(pid, startedUtc));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"this launcher could not record itself for the app: {e.Message}"); }
    }

    // Only its own record: one written since by another launcher stays.
    public void ForgetSelf(int pid, DateTime startedUtc)
    {
        try
        {
            if (File.ReadAllText(Record) == HostLauncherContract.FormatLauncher(pid, startedUtc)) { File.Delete(Record); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
