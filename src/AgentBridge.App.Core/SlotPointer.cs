using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Local;

namespace AgentBridge.App.Core;

public sealed class SlotPointer(string root)
{
    public sealed record Target(SlotPreparation.Prepared Slot, string Package);
    sealed record State(Target Current, Target? Previous);
    sealed record StateLease(State State, FileStream File) : IDisposable
    {
        public void Dispose() => File.Dispose();
    }
    readonly string root = Path.GetFullPath(root);
    string Pointer => Path.Combine(root, "active-slot.json");
    static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public sealed class Snapshot : IDisposable
    {
        internal readonly List<IDisposable> Held = [];
        public Target Target { get; }
        public string Directory { get; }
        internal Snapshot(Target target, string directory) { Target = target; Directory = directory; }
        public string EntryPoint(string name) =>
            name is "AgentBridge.exe" or "agent-bridge-host.exe" or "agent-bridge-hostw.exe"
                ? Path.Combine(Directory, name) : throw new InvalidDataException("Unknown slot entry point.");
        public void Dispose()
        {
            for (var i = Held.Count - 1; i >= 0; i--) { Held[i].Dispose(); }
            Held.Clear();
        }
    }

    public Snapshot? Read()
    {
        var held = LoadState();
        if (held is null) { return null; }
        try
        {
            var snapshot = Pin(held.State.Current);
            snapshot.Held.Add(held);
            return snapshot;
        }
        catch { held.Dispose(); throw; }
    }

    StateLease? LoadState()
    {
        SlotPreparation.NoLinks(Pointer);
        FileStream pointer;
        try { pointer = new(Pointer, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            if (pointer.Length is <= 0 or > 8192) { throw new InvalidDataException("Invalid slot pointer size."); }
            using var document = JsonDocument.Parse(pointer, new() { AllowDuplicateProperties = false, MaxDepth = 8 });
            var state = document.RootElement.Deserialize<State>(Json);
            if (state?.Current is null) { throw new InvalidDataException("Missing current slot reference."); }
            CheckReference(state.Current);
            if (state.Previous is not null) { CheckReference(state.Previous); }
            return new(state, pointer);
        }
        catch { pointer.Dispose(); throw; }
    }

    public void Publish(Target target, Action? beforeCommit = null) => Commit(target, false, beforeCommit);
    public void Rollback(Action? beforeCommit = null) => Commit(null, true, beforeCommit);

    void Commit(Target? target, bool rollback, Action? beforeCommit)
    {
        SlotPreparation.NoLinks(root);
        var lockPath = Path.Combine(root, ".active-slot.lock");
        SlotPreparation.NoLinks(lockPath);
        using var writer = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var old = LoadState();
        target = rollback ? old?.State.Previous ?? throw new InvalidDataException("No previous slot to restore.") : target;
        using var next = Pin(target ?? throw new InvalidDataException("Missing target slot."));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new State(next.Target, old?.State.Current), Json);
        if (bytes.Length > 8192) { throw new InvalidDataException("Slot pointer exceeds its limit."); }
        var staging = Path.Combine(root, ".active-slot-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            beforeCommit?.Invoke();
            SlotPreparation.NoLinks(Pointer);
            // ReplaceFileW on Windows; initial same-directory rename must not overwrite a winner.
            if (old is not null) { File.Replace(staging, Pointer, null); }
            else { File.Move(staging, Pointer); }
        }
        finally { File.Delete(staging); }
    }

    static void CheckReference(Target target)
    {
        if (target?.Slot is not { RelativePath: not null, Version: not null, Digest.Hex: not null } slot
            || target.Package is null || !SlotPreparation.SafeRelative(target.Package)
            || target.Package.Split('/') is not ["packages", _]
            || !SlotPreparation.SafeRelative(slot.RelativePath.Replace('\\', '/'))
            || slot.RelativePath != Path.Combine("slots", $"{slot.Version}-{slot.Digest.Hex.ToLowerInvariant()}"))
        { throw new InvalidDataException("Invalid relative slot/package reference."); }
    }

    internal Snapshot Pin(Target target)
    {
        CheckReference(target);
        var slot = target.Slot;
        var directory = Path.Combine(root, slot.RelativePath);
        SlotPreparation.NoLinks(directory);
        if (!System.IO.Directory.Exists(directory)) { throw new InvalidDataException("Prepared slot is missing."); }
        var snapshot = new Snapshot(target, directory);
        try
        {
            var package = Path.Combine(root, target.Package.Replace('/', Path.DirectorySeparatorChar));
            SlotPreparation.NoLinks(package);
            snapshot.Held.Add(DownloadMark.Hold(package, slot.Digest)
                ?? throw new PackageMismatchException("Slot package no longer matches its release digest."));
            var pending = new Stack<string>([directory]);
            while (pending.TryPop(out var folder))
            {
                SlotPreparation.NoLinks(folder);
                foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(folder))
                {
                    SlotPreparation.NoLinks(path);
                    if (System.IO.Directory.Exists(path)) { pending.Push(path); }
                    else { snapshot.Held.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)); }
                    if (snapshot.Held.Count + pending.Count > 10000) { throw new InvalidDataException("Slot exceeds its entry limit."); }
                }
            }
            // Revalidate after acquiring all payload leases; a constructed receipt proves nothing.
            if (SlotPreparation.Prepare(root, slot.Version, package, slot.Digest, UpdateVersions.Identity) != slot)
            { throw new InvalidDataException("Slot receipt differs from the verified package."); }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }
}
