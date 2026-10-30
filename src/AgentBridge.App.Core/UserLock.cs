using System.Diagnostics;

namespace AgentBridge.App.Core;

// A lock across the person's Windows sessions. The app's single-instance lock is the session's
// own (Local\), so the same person signed in twice runs the app twice. This is a file in their
// own folder, opened by one process alone: other users cannot hold it (unlike a Global\ name,
// which anyone can take first), and it is let go when the process ends, however it ends.
public static class UserLock
{
    // The open file while this process holds it; null while another one does. Throws when the
    // lock cannot be had at all (any other IOException, UnauthorizedAccessException).
    public static FileStream? TryTake(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when (Held(e)) { return null; }
    }

    // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION on Windows; elsewhere .NET takes an
    // flock, and EWOULDBLOCK (Linux 11, macOS 35) is the errno it reports.
    static bool Held(IOException e) => OperatingSystem.IsWindows()
        ? e.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
        : e.HResult is 11 or 35;

    // Runs body holding the lock, waiting up to `wait` for another process to let it go. Past
    // that, or when the lock cannot be had at all, body runs without it: the lock only keeps
    // two copies from doing the same thing at once, and must never stop the app.
    public static async Task<T> RunAsync<T>(string path, Func<T> body, TimeSpan wait, Action<string>? log = null, CancellationToken ct = default)
    {
        var waited = Stopwatch.StartNew();
        FileStream? held = null;
        try
        {
            while (true)
            {
                try { held = TryTake(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"{Path.GetFileName(path)} could not be opened, so it is not kept: {e.Message}");
                    break;
                }
                if (held is not null) { break; }
                if (waited.Elapsed >= wait)
                {
                    log?.Invoke($"{Path.GetFileName(path)} is still held elsewhere after {wait.TotalSeconds:0} s: going on without it");
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
            return body();
        }
        finally { held?.Dispose(); }
    }
}
