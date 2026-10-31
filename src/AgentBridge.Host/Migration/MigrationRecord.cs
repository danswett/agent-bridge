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

    public sealed record TaskRecord(string Name, bool Enabled)
    {
        // Whether this migration turned it off. A task the scheduler refused to disable was never
        // touched, and a rollback must neither enable nor start it (#114): with read access alone
        // both fail, and every rollback would keep the record. Null in a record from a build
        // that did not say, where a rollback judges by the task's state now.
        public bool? Disabled { get; set; }
    }

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
        // How the file was linked when migrated (DurableFile.Link): "symlink:<target>" or
        // "hardlink"; null for a plain file. Writes went through it, and a rollback puts it back.
        public string? Link { get; init; }
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
            ["tasks"] = new JsonArray(Tasks.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["enabled"] = t.Enabled, ["disabled"] = t.Disabled }).ToArray()),
            ["files"] = new JsonArray(Files.Select(f => (JsonNode)new JsonObject
            {
                ["role"] = f.Role,
                ["path"] = f.Path,
                ["existed"] = f.Existed,
                ["backup"] = f.Backup,
                ["before"] = f.Before,
                ["after"] = f.After,
                ["link"] = f.Link,
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
        DurableFile.Write(path, new System.Text.UTF8Encoding(false).GetBytes(o.ToJsonString(Pretty) + Environment.NewLine));
    }

    public static (MigrationRecord? Record, string? Problem) Load(string profileFolder)
    {
        var path = PathIn(profileFolder);
        try
        {
            if (!File.Exists(path)) { return (null, null); }
            var o = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("not an object");
            if ((int?)o["version"] != Version) { throw new JsonException($"not version {Version}"); }
            // Everything Save writes is required, as it wrote it: a list missing, or an entry in it
            // that is not an object, read as empty would let a rollback skip a file or the
            // daemon's task, report success and delete the record and snapshot it needed.
            string S(JsonNode? n) => (string?)n ?? throw new JsonException("a field is missing");
            bool B(JsonNode? n) => (bool?)n ?? throw new JsonException("a field is missing");
            IEnumerable<JsonObject> Objects(JsonNode? n, string name) =>
                (n as JsonArray ?? throw new JsonException($"{name} is missing or not a list"))
                .Select(e => e as JsonObject ?? throw new JsonException($"an entry in {name} is not an object"));
            JsonNode Node(JsonNode? n) => n?.DeepClone() ?? throw new JsonException("a field is missing");
            // Null is a value of its own (a file not yet written, or absent before), so present
            // as null and missing are told apart: an after missing would read as never written.
            JsonNode? Present(JsonObject parent, string name) => parent.TryGetPropertyValue(name, out var v) ? v : throw new JsonException($"{name} is missing");
            string? N(JsonObject parent, string name) => (string?)Present(parent, name);
            var record = new MigrationRecord
            {
                State = S(o["state"]),
                InstallationId = S(o["installationId"]),
                BridgeHome = S(o["bridgeHome"]),
                StartedAt = DateTimeOffset.Parse(S(o["startedAt"]), System.Globalization.CultureInfo.InvariantCulture),
                MigratedAt = Present(o, "migratedAt") is { } m ? DateTimeOffset.Parse((string)m!, System.Globalization.CultureInfo.InvariantCulture) : null,
                Tasks = [.. Objects(o["tasks"], "tasks").Select(t => new TaskRecord(S(t["name"]), B(t["enabled"])) { Disabled = (bool?)t["disabled"] })],
                Files = [.. Objects(o["files"], "files").Select(f => new FileRecord
                {
                    Role = S(f["role"]),
                    Path = S(f["path"]),
                    Existed = B(f["existed"]),
                    Backup = S(f["backup"]),
                    Before = N(f, "before"),
                    After = N(f, "after"),
                    Link = N(f, "link"),
                    Changes = [.. Objects(f["changes"], "changes").Select(c => new EntryChange(S(c["agent"]), S(c["event"]), Node(c["original"]), Node(c["replacement"])))],
                })],
                Checked = [.. Objects(o["checked"], "checked").Select(c => new CheckedFile(S(c["path"]), N(c, "hash")))],
                Hook = Present(o, "hook") switch
                {
                    null => null,
                    JsonObject h => new HookRecord(S(h["path"]), S(h["pointer"]), B(h["created"]), B(h["pointerCreated"])),
                    _ => throw new JsonException("hook is not an object"),
                },
            };
            return (record, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            return (null, $"{path} could not be read ({e.Message}); roll back by hand from {SnapshotIn(profileFolder)}");
        }
    }
}
