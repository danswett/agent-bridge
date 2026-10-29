using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.Host.Migration;

// What a migration changed, and what each thing was before, so a rollback can put it back byte
// for byte: migration.json in the profile's folder, and the original bytes of every file it
// rewrote in migration-snapshot\ beside it (the profile's folder is private to this user).
// Written before anything is changed, as "migrating", so an interrupted migration can still be
// rolled back; "migrated" once it has finished.
//
// Files migration only checks (agent-ha-bridge's config and installation record, its instruction
// files) are recorded by hash alone: the config can hold secrets, and is never copied.
public sealed class MigrationRecord
{
    public const string FileName = "migration.json";
    public const string SnapshotFolder = "migration-snapshot";
    public const int Version = 1;

    public string State { get; set; } = "migrating";
    public required string InstallationId { get; init; }
    public required string BridgeHome { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? MigratedAt { get; set; }
    public List<TaskRecord> Tasks { get; init; } = [];
    public List<FileRecord> Files { get; init; } = [];
    public List<CheckedFile> Checked { get; init; } = [];
    public HookRecord? Hook { get; set; }

    public sealed record TaskRecord(string Name, bool Enabled);

    // A file migration rewrote. Changes is empty for the profile's own config.json, which is
    // restored whole or not at all.
    public sealed class FileRecord
    {
        public required string Role { get; init; }
        public required string Path { get; init; }
        public required bool Existed { get; init; }
        public required string Backup { get; init; }
        public string? Before { get; init; }
        public string? After { get; set; }
        public List<EntryChange> Changes { get; init; } = [];
    }

    public sealed record CheckedFile(string Path, string? Hash);

    // The hook program and its pointer, and whether this migration put each there.
    public sealed record HookRecord(string Path, string Pointer, bool Created, bool PointerCreated);

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string PathIn(string profileFolder) => System.IO.Path.Combine(profileFolder, FileName);

    public static string SnapshotIn(string profileFolder) => System.IO.Path.Combine(profileFolder, SnapshotFolder);

    public static string? Hash(string path) => File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Save(string profileFolder)
    {
        var o = new JsonObject
        {
            ["version"] = Version,
            ["state"] = State,
            ["installationId"] = InstallationId,
            ["bridgeHome"] = BridgeHome,
            ["startedAt"] = StartedAt.ToString("o"),
            ["migratedAt"] = MigratedAt?.ToString("o"),
            ["tasks"] = new JsonArray(Tasks.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["enabled"] = t.Enabled }).ToArray()),
            ["files"] = new JsonArray(Files.Select(f => (JsonNode)new JsonObject
            {
                ["role"] = f.Role,
                ["path"] = f.Path,
                ["existed"] = f.Existed,
                ["backup"] = f.Backup,
                ["before"] = f.Before,
                ["after"] = f.After,
                ["changes"] = new JsonArray(f.Changes.Select(c => (JsonNode)new JsonObject
                {
                    ["agent"] = c.Agent,
                    ["event"] = c.Event,
                    ["original"] = c.Original.DeepClone(),
                    ["replacement"] = c.Replacement.DeepClone(),
                }).ToArray()),
            }).ToArray()),
            ["checked"] = new JsonArray(Checked.Select(c => (JsonNode)new JsonObject { ["path"] = c.Path, ["hash"] = c.Hash }).ToArray()),
            ["hook"] = Hook is null ? null : new JsonObject { ["path"] = Hook.Path, ["pointer"] = Hook.Pointer, ["created"] = Hook.Created, ["pointerCreated"] = Hook.PointerCreated },
        };
        var path = PathIn(profileFolder);
        var partial = path + ".tmp";
        File.WriteAllText(partial, o.ToJsonString(Pretty) + Environment.NewLine);
        File.Move(partial, path, overwrite: true);
    }

    public static (MigrationRecord? Record, string? Problem) Load(string profileFolder)
    {
        var path = PathIn(profileFolder);
        try
        {
            if (!File.Exists(path)) { return (null, null); }
            var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("not an object");
            if ((int?)o["version"] != Version) { throw new JsonException($"not version {Version}"); }
            string S(JsonNode? n) => (string?)n ?? throw new JsonException("a field is missing");
            var record = new MigrationRecord
            {
                State = S(o["state"]),
                InstallationId = S(o["installationId"]),
                BridgeHome = S(o["bridgeHome"]),
                StartedAt = DateTimeOffset.Parse(S(o["startedAt"]), System.Globalization.CultureInfo.InvariantCulture),
                MigratedAt = o["migratedAt"] is { } m ? DateTimeOffset.Parse((string)m!, System.Globalization.CultureInfo.InvariantCulture) : null,
                Tasks = [.. (o["tasks"] as JsonArray ?? []).OfType<JsonObject>().Select(t => new TaskRecord(S(t["name"]), (bool?)t["enabled"] ?? false))],
                Files = [.. (o["files"] as JsonArray ?? []).OfType<JsonObject>().Select(f => new FileRecord
                {
                    Role = S(f["role"]),
                    Path = S(f["path"]),
                    Existed = (bool?)f["existed"] ?? false,
                    Backup = S(f["backup"]),
                    Before = (string?)f["before"],
                    After = (string?)f["after"],
                    Changes = [.. (f["changes"] as JsonArray ?? []).OfType<JsonObject>().Select(c => new EntryChange(S(c["agent"]), S(c["event"]), c["original"]!.DeepClone(), c["replacement"]!.DeepClone()))],
                })],
                Checked = [.. (o["checked"] as JsonArray ?? []).OfType<JsonObject>().Select(c => new CheckedFile(S(c["path"]), (string?)c["hash"]))],
                Hook = o["hook"] is JsonObject h ? new HookRecord(S(h["path"]), S(h["pointer"]), (bool?)h["created"] ?? false, (bool?)h["pointerCreated"] ?? false) : null,
            };
            return (record, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            return (null, $"{path} could not be read ({e.Message}); roll back by hand from {SnapshotIn(profileFolder)}");
        }
    }
}
