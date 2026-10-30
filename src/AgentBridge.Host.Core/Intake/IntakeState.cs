using System.Text.Json;

namespace AgentBridge.Host.Core.Intake;

// Whether this profile has taken over hook handling from an agent-ha-bridge installation, and
// where that installation's runtime folder is: intake.json in the profile's folder, written by
// `agent-bridge-host migrate` and removed by `rollback` (docs/host-hooks.md, docs/migration.md).
// Its absence is the answer for every machine that has not migrated, and on those the host
// never reads the spool, never writes a heartbeat and never writes a registration.
public sealed record IntakeState(string RuntimeRoot, string BridgeHome, string InstallationId, DateTimeOffset MigratedAt)
{
    public const string FileName = "intake.json";
    public const int Version = 1;

    // Null when the profile has not migrated, or the file cannot be trusted: a file that cannot
    // be read is not taken as permission to consume another program's spool.
    public static (IntakeState? State, string? Problem) Load(string profileFolder)
    {
        var path = Path.Combine(profileFolder, FileName);
        try
        {
            // Read, not File.Exists first: that is false too when existence cannot be checked (no
            // access), and an unreadable record must refuse Codex approvals, not look unmigrated.
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception absent) when (absent is FileNotFoundException or DirectoryNotFoundException) { return (null, null); }
            using var doc = JsonDocument.Parse(bytes);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || Json.Int(e, "version") != Version) { return (null, $"{path} is not a version {Version} intake record; hook intake is off"); }
            var runtime = Json.Str(e, "runtimeRoot");
            var bridge = Json.Str(e, "bridgeHome");
            var id = Json.Str(e, "installationId");
            if (runtime is null || !Path.IsPathFullyQualified(runtime) || bridge is null || !Path.IsPathFullyQualified(bridge) || string.IsNullOrEmpty(id))
            {
                return (null, $"{path} is incomplete; hook intake is off");
            }
            return (new IntakeState(runtime, bridge, id, Json.Time(e, "migratedAt") ?? DateTimeOffset.MinValue), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"{path} could not be read ({e.Message}); hook intake is off");
        }
    }

    public void Save(string profileFolder)
    {
        Directory.CreateDirectory(profileFolder);
        var path = Path.Combine(profileFolder, FileName);
        var partial = path + ".tmp";
        using (var stream = File.Create(partial))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("runtimeRoot", RuntimeRoot);
            writer.WriteString("bridgeHome", BridgeHome);
            writer.WriteString("installationId", InstallationId);
            writer.WriteString("migratedAt", MigratedAt.ToString("o"));
            writer.WriteEndObject();
        }
        File.Move(partial, path, overwrite: true);
    }

    public static bool Delete(string profileFolder)
    {
        var path = Path.Combine(profileFolder, FileName);
        if (!File.Exists(path)) { return false; }
        File.Delete(path);
        return true;
    }
}
