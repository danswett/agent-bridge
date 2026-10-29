namespace AgentBridge.Host;

// The host's log: the console, as before, and a file in the profile's folder, since a host
// started at logon has no console and the app shows the file's end. Never content: what the
// host logs is counts, ids and outcomes, as it always has.
public static class HostLog
{
    const long MaxBytes = 2 * 1024 * 1024;
    static readonly object Gate = new();
    static string? file;

    public static string? FilePath => file;

    public static void OpenFile(string path)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            file = path;
        }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        Console.WriteLine(line);
        lock (Gate)
        {
            if (file is null) { return; }
            try
            {
                var info = new FileInfo(file);
                // One old file is kept: enough to see what led to a restart.
                if (info.Exists && info.Length > MaxBytes) { File.Move(file, file + ".1", overwrite: true); }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
            }
            // The console still has it; a log that cannot be written must not stop the host.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
