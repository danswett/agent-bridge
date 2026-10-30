using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Launch;

// Where each agent keeps its sessions on this machine.
public sealed record AgentHomes(string Copilot, string Claude, string Codex);

// A session's transcript as it moves between machines (docs/host-transfer.md), as
// agent-ha-bridge's Get-BridgeSessionBundleSpec, New-BridgeSessionBundle and
// Install-BridgeSessionBundle (hooks/session-launch.ps1) move it:
//   * Packed read-only: nothing at the source is renamed, moved, locked or marked, and the copy
//     there stays exactly as resumable as it was. A session that changes while it is packed is
//     refused: it is open somewhere, and a half-copied conversation is worse than none.
//   * Installed under a NEW id. Two machines holding one id would need something to decide
//     which may write, across machines that cannot see each other's keyboards; with a new id
//     there is nothing to decide. The original and the copy are two sessions.
//   * Only what makes the session: Copilot's events.jsonl, workspace.yaml, checkpoints/ and
//     files/ (never its rewind snapshots or session.db, which describe this machine's disks and
//     cannot be renamed); Claude's <id>.jsonl; Codex's rollout-…-<id>.jsonl. Never through a
//     link: a junction under files/ would otherwise send whatever it points at.
//   * Checked before it is believed: a bundle's paths are relative and stay inside the
//     session, its size and file count are bounded, and what is installed must be a session
//     the agent can open. Handed --session-id for a session with no transcript, Copilot does
//     not fail: it starts an empty one under that id and exits 0.
public static class SessionBundles
{
    public sealed record Packed(long Bytes, byte[] Sha256, int Files);

    sealed record Spec(AgentKind Kind, string Root, IReadOnlyList<string> Files);

    // The session's files, or null when it is not on this machine.
    static Spec? Find(AgentKind kind, AgentHomes homes, string id)
    {
        if (!Guid.TryParseExact(id, "D", out var g)) { return null; }
        id = g.ToString("D");
        switch (kind)
        {
            case AgentKind.Copilot:
            {
                var dir = Path.Combine(homes.Copilot, "session-state", id);
                if (!Directory.Exists(dir) || Linked(dir)) { return null; }
                var files = new List<string>();
                foreach (var name in new[] { "events.jsonl", "workspace.yaml" })
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p) && !Linked(p)) { files.Add(p); }
                }
                // Without its transcript there is no conversation to move.
                if (!files.Any(f => Path.GetFileName(f) == "events.jsonl")) { return null; }
                foreach (var sub in new[] { "checkpoints", "files" })
                {
                    var p = Path.Combine(dir, sub);
                    if (Directory.Exists(p) && !Linked(p)) { files.AddRange(Unlinked(p)); }
                }
                return new Spec(kind, dir, files);
            }
            case AgentKind.Claude:
            {
                var projects = Path.Combine(homes.Claude, "projects");
                if (!Directory.Exists(projects)) { return null; }
                foreach (var folder in Directory.EnumerateDirectories(projects))
                {
                    var p = Path.Combine(folder, $"{id}.jsonl");
                    if (!Linked(folder) && File.Exists(p) && !Linked(p)) { return new Spec(kind, folder, [p]); }
                }
                return null;
            }
            case AgentKind.Codex:
            {
                var sessions = Path.Combine(homes.Codex, "sessions");
                if (!Directory.Exists(sessions)) { return null; }
                var file = Directory.EnumerateFiles(sessions, $"rollout-*{id}.jsonl", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .FirstOrDefault();
                return file is null ? null : new Spec(kind, Path.GetDirectoryName(file)!, [file]);
            }
            default: return null;
        }
    }

    public static bool Exists(AgentKind kind, AgentHomes homes, string id) => Find(kind, homes, id) is not null;

    static bool Linked(string path)
    {
        try { return new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    // Every file below a folder without stepping through a link, as
    // Get-BridgeUnlinkedDescendantFile walks: a session's files/ holds whatever it worked on.
    static List<string> Unlinked(string root)
    {
        var files = new List<string>();
        var pending = new Queue<string>([root]);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Dequeue()))
            {
                var info = new FileInfo(entry);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { continue; }
                if (info.Attributes.HasFlag(FileAttributes.Directory)) { pending.Enqueue(entry); }
                else { files.Add(entry); }
            }
        }
        return files;
    }

    // For a test to change the tree between the walk and a file's open, as a race would.
    [ThreadStatic] internal static Action<string>? BeforeOpen;

    // Packs a session into zip (a new file), bounded by maxSource bytes read and maxFiles files.
    // Throws InvalidOperationException, saying why, for one that cannot be sent.
    // cancellationToken is checked between and within files, so a deadline stops a slow pack.
    public static Packed Pack(AgentKind kind, AgentHomes homes, string id, string zip, long maxSource, int maxFiles = TransferLimits.MaxFiles, CancellationToken cancellationToken = default)
    {
        var spec = Find(kind, homes, id) ?? throw new InvalidOperationException("that session's files are not on this machine");
        if (spec.Files.Count > maxFiles) { throw new InvalidOperationException($"that session has {spec.Files.Count} files, over the {maxFiles} a transfer takes"); }
        // What each file looked like before it was read, and the bound on reading them, before a
        // byte is copied: files/ is a session's own store, and nothing else bounds it.
        var witness = spec.Files.ToDictionary(f => f, Witness);
        var total = witness.Values.Sum(w => w.Length);
        if (total > maxSource) { throw new InvalidOperationException($"that session is {TransferLimits.Mib(total)} on disk, over the {TransferLimits.Mib(maxSource)} a transfer reads"); }
        try
        {
            using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                string? realRoot = null;
                foreach (var file in spec.Files)
                {
                    var relative = Path.GetRelativePath(spec.Root, file);
                    var name = spec.Kind == AgentKind.Copilot ? relative.Replace('\\', '/') : Path.GetFileName(file);
                    BeforeOpen?.Invoke(file);
                    using var from = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    realRoot = Opened(from, spec.Root, relative, realRoot);
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using var to = entry.Open();
                    Copy(from, to, witness[file].Length, cancellationToken);
                }
            }
            // Read again after the copy: a file that grew or was rewritten while it was read
            // makes the archive a snapshot of no single moment.
            if (spec.Files.Any(f => !File.Exists(f) || Witness(f) != witness[f]))
            {
                throw new InvalidOperationException(Changed);
            }
            byte[] sha;
            long bytes;
            cancellationToken.ThrowIfCancellationRequested();
            using (var read = File.OpenRead(zip)) { sha = SHA256.HashData(read); bytes = read.Length; }
            return new Packed(bytes, sha, spec.Files.Count);
        }
        catch
        {
            try { File.Delete(zip); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // Exactly the length witnessed before packing: the agent may be writing to the file (it was
    // resumed at its machine meanwhile), and reading to its end would read and stage whatever it
    // grows to, past maxSource, before the check after the copy refused it.
    static void Copy(Stream from, Stream to, long length, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var left = length;
        while (left > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n == 0) { throw new InvalidOperationException(Changed); }
            to.Write(buffer, 0, n);
            left -= n;
        }
        if (from.ReadByte() >= 0) { throw new InvalidOperationException(Changed); }
    }

    // After a file is opened: it is the one the walk found, not one a link swapped in since (a
    // folder under files/ replaced by a junction between the walk and the open would otherwise
    // put files from anywhere in the bundle). Every folder from the session's root down, and the
    // file, must still be no link; on Windows the handle's own final path must also be where the
    // file sits under the root the first file was opened in. Returns that root.
    static string? Opened(FileStream from, string root, string relative, string? realRoot)
    {
        var path = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (Linked(path)) { throw new InvalidOperationException(Changed); }
            path = Path.Combine(path, part);
        }
        if (Linked(path)) { throw new InvalidOperationException(Changed); }
        if (!OperatingSystem.IsWindows() || FinalPath(from.SafeFileHandle) is not { } real) { return realRoot; }
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
        var found = real;
        for (var i = 0; i < parts; i++) { found = Path.GetDirectoryName(found) ?? ""; }
        if (realRoot is not null && !string.Equals(found, realRoot, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException(Changed); }
        if (!string.Equals(Path.GetRelativePath(found, real), relative, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException(Changed); }
        return found;
    }

    // The path Windows resolves an open handle to, links and all, or null if it will not say.
    static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (n > buffer.Length) { buffer = new char[n]; n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0); }
        if (n == 0 || n > buffer.Length) { return null; }
        var s = new string(buffer, 0, (int)n);
        return s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + s[8..] : s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    const string Changed = "that session changed while it was being packed; it is probably open somewhere";

    static (long Length, long Written) Witness(string file)
    {
        var info = new FileInfo(file);
        return (info.Length, info.LastWriteTimeUtc.Ticks);
    }

    // Installs a bundle as session newId, recording cwd as where it works, and returns its
    // transcript's path. Throws InvalidDataException for a bundle that is not a session of this
    // kind, leaving nothing behind. unpack is a private folder of this host's, deleted after.
    public static string Install(string zip, AgentKind kind, string oldId, string newId, string cwd, AgentHomes homes, string unpack, long maxUnpacked, DateTimeOffset now, int maxFiles = TransferLimits.MaxFiles)
    {
        if (!Guid.TryParseExact(oldId, "D", out _) || !Guid.TryParseExact(newId, "D", out _)) { throw new InvalidDataException("a session id is not a UUID"); }
        Directory.CreateDirectory(unpack);
        string? installed = null;
        try
        {
            var files = Rehome(Extract(zip, kind, unpack, maxUnpacked, maxFiles), unpack, oldId, newId);            // Claude's and Codex's one file is the transcript, which also records the folder.
            var relocate = kind == AgentKind.Copilot ? null : (Func<string, string>)(line => Relocate(kind, line, cwd));
            foreach (var f in files.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".jsonl" or ".yaml" or ".json" or ".md")) { Rename(f, oldId, newId, relocate); }
            switch (kind)
            {
                case AgentKind.Copilot:
                {
                    var dest = Path.Combine(homes.Copilot, "session-state", newId);
                    if (Directory.Exists(dest)) { throw new InvalidDataException("a session with the new id is already here"); }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    SetCopilotCwd(Path.Combine(unpack, "workspace.yaml"), cwd);
                    installed = dest;
                    MoveTree(unpack, dest);
                    break;
                }
                case AgentKind.Claude:
                {
                    // Any folder name resolves; this one says where the session now runs.
                    var dir = Directory.CreateDirectory(Path.Combine(homes.Claude, "projects", ProjectName(cwd))).FullName;
                    var dest = Path.Combine(dir, $"{newId}.jsonl");
                    installed = dest;
                    File.Move(files[0], dest);
                    break;
                }
                case AgentKind.Codex:
                {
                    // The id has to stay in the name; the stamp and folders are free.
                    var dir = Directory.CreateDirectory(Path.Combine(homes.Codex, "sessions", now.ToString("yyyy"), now.ToString("MM"), now.ToString("dd"))).FullName;
                    var dest = Path.Combine(dir, $"rollout-{now:yyyy-MM-ddTHH-mm-ss}-{newId}.jsonl");
                    installed = dest;
                    File.Move(files[0], dest);
                    break;
                }
                default: throw new InvalidDataException($"cannot install a {kind} session");
            }
            // Found where the agent will look for it, or withdrawn: see above for why an empty
            // Copilot session is the one outcome that must never be launched.
            if (Find(kind, homes, newId) is not { } spec) { throw new InvalidDataException("the bundle did not install as a session the agent can open"); }
            return spec.Kind == AgentKind.Copilot ? Path.Combine(spec.Root, "events.jsonl") : spec.Files[0];
        }
        catch when (installed is not null)
        {
            Remove(installed);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(unpack)) { Directory.Delete(unpack, recursive: true); } }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // Files whose names carry the old id get the new one, as the transcript's mentions of them
    // will: a saved file named for its session would otherwise be referred to by a name it no
    // longer has.
    static List<string> Rehome(List<string> files, string unpack, string oldId, string newId)
    {
        var root = Path.GetFullPath(unpack);
        var moved = new List<string>(files.Count);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file);
            if (!relative.Contains(oldId, StringComparison.OrdinalIgnoreCase)) { moved.Add(file); continue; }
            var to = Path.Combine(root, relative.Replace(oldId, newId, StringComparison.OrdinalIgnoreCase));
            if (File.Exists(to) || Directory.Exists(to)) { throw new InvalidDataException("the bundle holds a file under both the old and the new session id"); }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(file, to);
            moved.Add(to);
            // A folder named for the old id, now empty, goes too.
            for (var dir = Path.GetDirectoryName(file); dir is not null && dir.Length > root.Length && !Directory.EnumerateFileSystemEntries(dir).Any(); dir = Path.GetDirectoryName(dir)) { Directory.Delete(dir); }
        }
        return moved;
    }
    // Withdraws what Install put in place.
    static void Remove(string path)
    {
        try
        {
            if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            else if (File.Exists(path)) { File.Delete(path); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // The bundle's files, written below unpack. Only names a session of this kind has, relative,
    // inside it, at most maxFiles of them, and at most maxUnpacked bytes as they decompress
    // (what the archive claims is not believed).
    static List<string> Extract(string zip, AgentKind kind, string unpack, long maxUnpacked, int maxFiles)
    {
        var root = Path.GetFullPath(unpack) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        long total = 0;
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count == 0) { throw new InvalidDataException("the bundle is empty"); }
        if (archive.Entries.Count > maxFiles) { throw new InvalidDataException($"the bundle has more than {maxFiles} files"); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (name.EndsWith('/')) { continue; }
            if (!Allowed(kind, name)) { throw new InvalidDataException($"the bundle holds '{Limits.KeepStart(name, 80)}', which is not part of a {kind} session"); }
            if (!names.Add(name)) { throw new InvalidDataException("the bundle holds a file twice"); }
            var target = Path.GetFullPath(Path.Combine(unpack, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) { throw new InvalidDataException("the bundle holds a path outside the session"); }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var from = entry.Open())
            using (var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = from.Read(buffer)) > 0)
                {
                    total += n;
                    if (total > maxUnpacked) { throw new InvalidDataException($"the bundle unpacks to more than {TransferLimits.Mib(maxUnpacked)}"); }
                    to.Write(buffer, 0, n);
                }
            }
            files.Add(target);
        }
        var transcript = kind == AgentKind.Copilot ? names.Contains("events.jsonl") : files.Count == 1;
        if (!transcript) { throw new InvalidDataException($"the bundle holds no {kind} transcript"); }
        return files;
    }

    // The names each kind's bundle may hold: plain relative segments, no drive, no '..'.
    static bool Allowed(AgentKind kind, string name)
    {
        if (name.Length == 0 || name.Length > 512 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.Any(char.IsControl)) { return false; }
        var parts = name.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or "..")) { return false; }
        return kind switch
        {
            AgentKind.Copilot => parts.Length == 1 ? parts[0] is "events.jsonl" or "workspace.yaml" : parts[0] is "checkpoints" or "files",
            AgentKind.Claude or AgentKind.Codex => parts.Length == 1 && parts[0].EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    // A transcript names its session inside as well as by its file: every mention becomes the
    // new id, line by line, so a transcript of any size is never held whole.
    static void Rename(string file, string oldId, string newId, Func<string, string>? relocate = null)
    {
        // A name no entry can have (files/ may hold "x.md.rename" beside "x.md"), made new, never
        // opened over something already there.
        var temp = Path.Combine(Path.GetDirectoryName(file)!, $".{Guid.NewGuid():N}.rename");
        var changed = false;
        try
        {
            using var reader = new StreamReader(file, new UTF8Encoding(false));
            using var writer = new StreamWriter(new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false));
            string? line;
            var first = true;
            var buffer = new StringBuilder();
            while ((line = ReadLine(reader, buffer)) is not null)
            {
                if (!first) { writer.Write('\n'); }
                first = false;
                if (line.Contains(oldId, StringComparison.OrdinalIgnoreCase))
                {
                    line = line.Replace(oldId, newId, StringComparison.OrdinalIgnoreCase);
                    changed = true;
                }
                if (relocate is not null)
                {
                    var moved = relocate(line);
                    changed |= !ReferenceEquals(moved, line);
                    line = moved;
                }
                writer.Write(line);
            }
            // A final line break is kept as it was.
            if (!first && EndsWithNewline(file)) { writer.Write('\n'); }
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        if (changed) { File.Move(temp, file, overwrite: true); }
        else { File.Delete(temp); }
    }

    // A transcript line, at most MaxLine characters: one line is held whole to be rewritten,
    // and a bundle small enough to send can unpack to a single line of hundreds of megabytes.
    public const int MaxLine = 16 * 1024 * 1024;

    static string? ReadLine(StreamReader reader, StringBuilder line)
    {
        line.Clear();
        int c;
        while ((c = reader.Read()) >= 0 && c != '\n')
        {
            if (line.Length >= MaxLine) { throw new InvalidDataException($"the bundle holds a line over {MaxLine / (1024 * 1024)} MiB, which this machine does not rewrite"); }
            line.Append((char)c);
        }
        if (c < 0 && line.Length == 0) { return null; }
        if (line.Length > 0 && line[^1] == '\r') { line.Length--; }
        return line.ToString();
    }

    static bool EndsWithNewline(string file)
    {
        using var s = File.OpenRead(file);
        if (s.Length == 0) { return false; }
        s.Seek(-1, SeekOrigin.End);
        return s.ReadByte() == '\n';
    }

    static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The folder a Claude or Codex transcript records, which is where this machine's resume list
    // places the session (ResumableStore: a Claude prompt's cwd, Codex's session_meta). Left as
    // the other machine's, a folder not approved here, the copy would drop off the list once it
    // ended, or if its launch failed. Only those fields change; the line is returned as it was
    // (the same string) when it has none, or is not JSON.
    static string Relocate(AgentKind kind, string line, string cwd)
    {
        if (!line.Contains("\"cwd\"", StringComparison.Ordinal)) { return line; }
        JsonObject? o;
        try { o = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return line; }
        if (o is null) { return line; }
        var holder = kind switch
        {
            AgentKind.Claude => o,
            AgentKind.Codex when o["type"] is JsonValue type && type.TryGetValue<string>(out var name) && name is "session_meta" or "turn_context" => o["payload"] as JsonObject,
            _ => null,
        };
        if (holder?["cwd"] is not JsonValue old || !old.TryGetValue<string>(out _)) { return line; }
        holder["cwd"] = cwd;
        return o.ToJsonString(Relaxed);
    }

    // The folder under Claude's projects/ the copy goes in. Any name resolves; this one says
    // where it now runs, shortened with a hash for a deep folder, since one path component is at
    // most 255 characters and the whole path is better kept short.
    static string ProjectName(string cwd)
    {
        var slug = cwd.Replace(':', '-').Replace('\\', '-').Replace('/', '-').Trim('-');
        if (slug.Length == 0) { return "bridge-transferred"; }
        if (slug.Length <= 100) { return slug; }
        return $"{slug[..100].TrimEnd('-')}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)))[..16]}";
    }

    // workspace.yaml's cwd: the folder the copy works in here, not the other machine's. A line at
    // a time, bounded as transcripts are: the file came from another machine.
    static void SetCopilotCwd(string yaml, string cwd)
    {
        if (!File.Exists(yaml)) { return; }
        var entry = "cwd: '" + cwd.Replace("'", "''") + "'";
        var temp = Path.Combine(Path.GetDirectoryName(yaml)!, $".{Guid.NewGuid():N}.rename");
        try
        {
            using var reader = new StreamReader(yaml, new UTF8Encoding(false));
            using var writer = new StreamWriter(new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false));
            var found = false;
            var buffer = new StringBuilder();
            string? line;
            while ((line = ReadLine(reader, buffer)) is not null)
            {
                if (line.StartsWith("cwd:", StringComparison.Ordinal)) { (line, found) = (entry, true); }
                writer.Write(line);
                writer.Write('\n');
            }
            if (!found) { writer.Write(entry); writer.Write('\n'); }
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        File.Move(temp, yaml, overwrite: true);
    }

    static void MoveTree(string from, string to)
    {
        try { Directory.Move(from, to); }
        catch (IOException)
        {
            // Another volume: copied, then the unpacked copy goes with the unpack folder.
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target);
            }
        }
    }
}
