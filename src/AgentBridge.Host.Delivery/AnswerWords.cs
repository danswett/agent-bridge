using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace AgentBridge.Host.Delivery;

// The words of answers typed but not yet confirmed, so that a host that restarted can read the
// session's record again and compare it with what was typed (#1159 slice 0). Kept apart from the
// attempt ledger, whose folder the Codex sandbox group can read, in private\answer-words.json under
// the host's profile folder, on Trust's conditions (#1161, comment 6712503332):
//   * the folder's ACL is protected and grants this user and SYSTEM alone (0700, files 0600, off
//     Windows), and is checked before any words are written: wider, and no words are kept;
//   * only each answer's text and field values, under a key the ledger entry holds;
//   * written whole to a temporary file beside it and renamed over it, never memory-mapped and with
//     no other copy; a failed write deletes its temporary file;
//   * at most MaxEntries answers, MaxEntryBytes each and MaxTotalBytes in all, counting words whose
//     delete has not reached the file yet: past them an answer is refused, nothing is evicted or cut;
//   * nothing of them is ever logged: a failure is logged by its exception's type alone.
// Used only under the AttemptLedger's lock.
internal sealed class AnswerWords
{
    public const string FolderName = "private";
    public const string FileName = "answer-words.json";
    public const int MaxEntries = 32;
    public const int MaxEntryBytes = 16 * 1024;
    public const int MaxTotalBytes = 256 * 1024;
    const int Version = 1;
    static readonly JsonSerializerOptions StrictJson = new() { RespectRequiredConstructorParameters = true };

    // What was sent, as sent: an answer's text, or each field's value for a form.
    public sealed record Words(string? Text, IReadOnlyDictionary<string, JsonElement>? Fields);
    sealed record FileData(int Version, Dictionary<string, Words> Held);

    readonly string? folder;
    readonly string? file;
    readonly Action<string> log;
    readonly Dictionary<string, (Words Words, int Size)> held = new(StringComparer.Ordinal);
    // Words no entry may use any more whose delete has not reached the file yet: never read again,
    // and counted against the bounds until a rewrite without them succeeds.
    readonly Dictionary<string, int> doomed = new(StringComparer.Ordinal);
    bool loaded;
    // The file could be neither read nor deleted: what it holds is not known, so it fills the
    // bounds until a delete succeeds.
    bool unknown;

    // ledger: the attempt ledger's path, beside which the private folder is kept; null keeps the
    // words in memory only, under the same bounds.
    public AnswerWords(string? ledger, Action<string> log)
    {
        this.log = log;
        if (ledger is null) { return; }
        folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ledger)) ?? ".", FolderName);
        file = Path.Combine(folder, FileName);
    }

    public int Count => held.Count + doomed.Count;

    // Test-only: fails the next writes and deletes with what it returns, as a full disk, or a file
    // another process holds open, would.
    internal Func<Exception?>? Failing { get; set; }

    public Words? Get(string key) => held.TryGetValue(key, out var kept) ? kept.Words : null;

    // At start, before anything is served: every leftover temporary file goes, and every answer's
    // words that no live entry refers to.
    public void Load(Func<string, bool> referenced)
    {
        if (loaded) { return; }
        loaded = true;
        if (folder is null) { return; }
        SweepTemporary();
        Dictionary<string, Words>? read = null;
        try { read = Read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            log($"the words of unconfirmed answers ({FileName}) could not be read ({e.GetType().Name}), so none is used, and it is deleted");
            unknown = true;
        }
        foreach (var (key, words) in read ?? [])
        {
            var size = Size(words);
            if (referenced(key)) { held[key] = (words, size); }
            else { doomed[key] = size; }
        }
        if (doomed.Count > 0 || unknown) { Flush(); }
    }

    // Keeps an answer's words, returning their key, or why not: the bounds, counting words whose
    // delete is still pending (tried again first), or a folder anyone else can read.
    public string? Hold(Words words, out string? key)
    {
        key = null;
        if (doomed.Count > 0 || unknown) { Flush(); }
        var size = Size(words);
        if (unknown || size > MaxEntryBytes || Count >= MaxEntries || Total + size > MaxTotalBytes) { return DeliveryReasons.CannotHoldAnswer; }
        if (folder is not null && !EnsurePrivate())
        {
            DropAll();
            return DeliveryReasons.CannotHoldAnswer;
        }
        var made = Guid.NewGuid().ToString("N");
        held[made] = (words, size);
        if (!Flush())
        {
            held.Remove(made);
            return DeliveryReasons.CannotHoldAnswer;
        }
        key = made;
        return null;
    }

    public void Drop(string key)
    {
        if (held.Remove(key, out var kept)) { doomed[key] = kept.Size; }
        if (doomed.Count > 0 || unknown) { Flush(); }
    }

    // Every write: words no entry refers to go, and a delete that failed before is tried again.
    public void Retain(Func<string, bool> referenced)
    {
        foreach (var key in held.Keys.Where(k => !referenced(k)).ToList()) { doomed[key] = held[key].Size; held.Remove(key); }
        if (doomed.Count > 0 || unknown) { Flush(); }
    }

    int Total => held.Values.Sum(h => h.Size) + doomed.Values.Sum();

    static int Size(Words words) => JsonSerializer.SerializeToUtf8Bytes(words).Length;

    // The file made to hold exactly what is held now; nothing held, no file. False when that did
    // not reach the disk, the words to delete then still counted.
    bool Flush()
    {
        if (folder is null || file is null)
        {
            doomed.Clear();
            return true;
        }
        try
        {
            if (Failing?.Invoke() is { } failure) { throw failure; }
            if (held.Count == 0)
            {
                if (Directory.Exists(folder)) { File.Delete(file); }
            }
            else
            {
                if (!EnsurePrivate())
                {
                    DropAll();
                    return false;
                }
                Write();
                if (!IsPrivate(file))
                {
                    DropAll();
                    return false;
                }
            }
            doomed.Clear();
            unknown = false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"the words of unconfirmed answers ({FileName}) could not be rewritten ({e.GetType().Name}); what was to be deleted is counted until it is");
            return false;
        }
    }

    // Anyone else can read the folder: no words are kept there, and those it has go.
    void DropAll()
    {
        foreach (var (key, kept) in held) { doomed[key] = kept.Size; }
        held.Clear();
        log($"the folder for the words of unconfirmed answers ({FolderName}) is not private to this user, so no words are kept");
        try
        {
            if (file is not null && File.Exists(file)) { File.Delete(file); }
            doomed.Clear();
            unknown = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"the words of unconfirmed answers ({FileName}) could not be deleted ({e.GetType().Name}); they are counted until they are");
        }
    }

    void Write()
    {
        var temp = file + ".tmp";
        try
        {
            if (File.Exists(temp)) { File.Delete(temp); }
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, new FileData(Version, held.ToDictionary(h => h.Key, h => h.Value.Words, StringComparer.Ordinal)));
                stream.Flush(flushToDisk: true);
            }
            // Renamed over the old file with no backup of it.
            File.Move(temp, file!, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) { File.Delete(temp); } }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    Dictionary<string, Words> Read()
    {
        try
        {
            using var stream = new FileStream(file!, FileMode.Open, FileAccess.Read, FileShare.Read);
            var data = JsonSerializer.Deserialize<FileData>(stream, StrictJson);
            if (data is null || data.Version != Version || data.Held is null || data.Held.Any(h => h.Value is null)) { throw new JsonException("unknown or invalid words"); }
            return new(data.Held, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    void SweepTemporary()
    {
        try
        {
            if (!Directory.Exists(folder)) { return; }
            foreach (var temp in Directory.EnumerateFiles(folder!, "*.tmp")) { File.Delete(temp); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"a temporary file in the folder for the words of unconfirmed answers could not be deleted ({e.GetType().Name})");
        }
    }

    // Created private if it is not there; true only when it, and the file in it, are private now.
    bool EnsurePrivate()
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(folder!)!);
                if (OperatingSystem.IsWindows()) { CreateWindows(folder!); }
                else { Directory.CreateDirectory(folder!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            }
            // A link would put the words wherever it points, under that place's permissions.
            if (new DirectoryInfo(folder!).Attributes.HasFlag(FileAttributes.ReparsePoint)) { return false; }
            return IsPrivate(folder!) && (!File.Exists(file) || IsPrivate(file!));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"the folder for the words of unconfirmed answers could not be made or checked ({e.GetType().Name})");
            return false;
        }
    }

    // Granted to this user and SYSTEM alone: nothing else holds an allow entry, and on Windows the
    // folder inherits nothing from above it.
    internal static bool IsPrivate(string path)
    {
        if (OperatingSystem.IsWindows()) { return IsPrivateWindows(path); }
        return (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;
    }

    [SupportedOSPlatform("windows")]
    static void CreateWindows(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var who in Allowed())
        {
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).Create(security);
    }

    [SupportedOSPlatform("windows")]
    static SecurityIdentifier[] Allowed() => [WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)];

    [SupportedOSPlatform("windows")]
    static bool IsPrivateWindows(string path)
    {
        var allowed = Allowed();
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        if (Directory.Exists(path) && !security.AreAccessRulesProtected) { return false; }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Any(a => a.Equals(rule.IdentityReference))) { return false; }
        }
        return true;
    }
}
