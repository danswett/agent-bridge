namespace AgentBridge.App.Core;

public static class SlotEntry
{
    public sealed class Launch : IDisposable
    {
        readonly IDisposable held;
        public string Executable { get; }
        public string Directory => Path.GetDirectoryName(Executable)!;
        public SlotPointer.Target? Target { get; }
        internal Launch(string executable, IDisposable held, SlotPointer.Target? target)
        { Executable = executable; this.held = held; Target = target; }
        public void Dispose() => held.Dispose();
    }

    public static Launch Resolve(string root, string entry)
    {
        if (entry is not ("AgentBridge.exe" or "agent-bridge-host.exe" or "agent-bridge-hostw.exe"))
        { throw new InvalidDataException("Unknown stable entry point."); }
        root = Path.GetFullPath(root);
        if (new SlotPointer(root).Read() is { } snapshot)
        { return new(snapshot.EntryPoint(entry), snapshot, snapshot.Target); }
        var legacy = Path.Combine(root, "current", entry);
        SlotPreparation.NoLinks(legacy);
        return new(legacy, new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read), null);
    }

    public static string? RootForPayload(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = Path.GetDirectoryName(directory);
        var root = string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase) ? parent
            : parent is not null && string.Equals(Path.GetFileName(parent), "slots", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(parent) : null;
        if (root is null) { return null; }
        SlotPreparation.NoLinks(root);
        return File.Exists(Path.Combine(root, "Update.exe")) ? root : null;
    }

    public static string? StablePath(string payload, string entry)
    {
        if (entry is not ("AgentBridge.exe" or "agent-bridge-hostw.exe"))
        { throw new InvalidDataException("Unknown registered entry point."); }
        if (RootForPayload(payload) is not { } root) { return null; }
        var helper = Path.Combine(root, "agent-bridge-hostw.exe");
        SlotPreparation.NoLinks(helper);
        if (!File.Exists(helper))
        {
            if (File.Exists(Path.Combine(root, "active-slot.json")))
                throw new FileNotFoundException("Stable launcher is missing after slot activation.", helper);
            return null;
        }
        var executable = Path.Combine(root, entry);
        SlotPreparation.NoLinks(executable);
        return File.Exists(executable) ? executable : throw new FileNotFoundException("Stable entry is missing.", executable);
    }
}
