namespace AgentBridgeS3;

static class CrashLog
{
    public static void Write(Exception? e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentBridgeS3Data");
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:O} {e}\n");
    }
}
