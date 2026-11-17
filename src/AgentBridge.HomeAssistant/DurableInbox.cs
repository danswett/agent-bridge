using System.Diagnostics;
using System.Text.Json;

namespace AgentBridge.HomeAssistant;

internal sealed record InboxMessage(string Node, string Rid, string Sender, string Payload, DateTimeOffset QueuedAt, DateTimeOffset? DeliveredAt,
    DateTimeOffset? StartedAt = null, string? Outcome = null);

internal sealed class DurableInbox
{
    const int MaxEntriesPerNode = 100;
    const int MaxEntries = 1000;
    sealed record Snapshot(int Version, List<InboxMessage> Messages);
    readonly string path;

    public DurableInbox(string path) => this.path = Path.GetFullPath(path);

    public InboxMessage Enqueue(string node, string? rid, string? sender, string payload, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        rid = string.IsNullOrWhiteSpace(rid) ? Guid.NewGuid().ToString("N") : rid;
        sender = string.IsNullOrWhiteSpace(sender) ? "legacy" : sender;
        using var held = Lock();
        var messages = Load();
        var prior = messages.Find(m => m.Node == node && m.Rid == rid);
        if (prior is not null) { return prior; }
        while (messages.Count(m => m.Node == node) >= MaxEntriesPerNode)
        {
            var oldest = messages.FindIndex(m => m.Node == node && m.DeliveredAt is not null);
            if (oldest < 0) { throw new InvalidOperationException("The node inbox is full."); }
            messages.RemoveAt(oldest);
        }
        while (messages.Count >= MaxEntries)
        {
            var oldest = messages.FindIndex(m => m.DeliveredAt is not null);
            if (oldest < 0) { throw new InvalidOperationException("The durable inbox is full."); }
            messages.RemoveAt(oldest);
        }
        var message = new InboxMessage(node, rid, sender, payload, at, null);
        messages.Add(message);
        Save(messages);
        return message;
    }

    public InboxMessage? Peek(string node)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        using var held = Lock();
        return Load().Find(m => m.Node == node && m.DeliveredAt is null);
    }

    public InboxMessage? Find(string node, string rid)
    {
        using var held = Lock();
        return Load().Find(m => m.Node == node && m.Rid == rid);
    }

    public void ForgetExcept(IReadOnlySet<string> nodes)
    {
        using var held = Lock();
        var messages = Load();
        if (messages.RemoveAll(m => !nodes.Contains(m.Node)) > 0) { Save(messages); }
    }

    public void Start(string node, string rid, DateTimeOffset at)
    {
        using var held = Lock();
        var messages = Load();
        var index = messages.FindIndex(m => m.Node == node && m.DeliveredAt is null);
        if (index < 0 || messages[index].Rid != rid || messages[index].StartedAt is not null)
        {
            throw new InvalidOperationException("Only an unclaimed FIFO head can start delivery.");
        }
        messages[index] = messages[index] with { StartedAt = at };
        Save(messages);
    }

    public void Forget(string node, string? rid = null)
    {
        using var held = Lock();
        var messages = Load();
        if (messages.RemoveAll(m => m.Node == node && (rid is null || m.Rid == rid)) > 0) { Save(messages); }
    }

    public InboxMessage Deliver(string node, string rid, DateTimeOffset at, string outcome = "delivered")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        if (outcome is not ("delivered" or "unconfirmed")) { throw new ArgumentOutOfRangeException(nameof(outcome)); }
        using var held = Lock();
        var messages = Load();
        var index = messages.FindIndex(m => m.Node == node && m.Rid == rid);
        if (index < 0) { throw new InvalidOperationException("Request id is not in the inbox."); }
        var message = messages[index];
        if (message.DeliveredAt is not null) { return message; }
        if (messages.FindIndex(m => m.Node == node && m.DeliveredAt is null) != index)
        {
            throw new InvalidOperationException("Only the oldest queued message can be delivered.");
        }
        var delivered = message with { Payload = "", DeliveredAt = at, Outcome = outcome };
        messages[index] = delivered;
        Save(messages);
        return delivered;
    }

    FileStream Lock()
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                return new FileStream(path + ".lock", options);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(10); }
        }
    }

    List<InboxMessage> Load()
    {
        DeleteLeftovers();
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return []; }
        Snapshot? snapshot;
        using (file) { snapshot = JsonSerializer.Deserialize<Snapshot>(file); }
        if (snapshot is null || snapshot.Version != 1 || snapshot.Messages is null ||
            snapshot.Messages.Any(m => m is null || string.IsNullOrWhiteSpace(m.Node) || string.IsNullOrWhiteSpace(m.Rid) ||
                string.IsNullOrWhiteSpace(m.Sender) || m.Payload is null || (m.DeliveredAt is null && string.IsNullOrWhiteSpace(m.Payload))) ||
            snapshot.Messages.Any(m => m.Outcome is not (null or "delivered" or "unconfirmed") || (m.DeliveredAt is null && m.Outcome is not null)) ||
            snapshot.Messages.Select(m => (m.Node, m.Rid)).Distinct().Count() != snapshot.Messages.Count)
        {
            throw new InvalidDataException("Invalid durable inbox snapshot.");
        }
        var changed = false;
        for (var index = 0; index < snapshot.Messages.Count; index++)
        {
            var message = snapshot.Messages[index];
            if (message.DeliveredAt is not null && message.Payload.Length > 0)
            {
                snapshot.Messages[index] = message with { Payload = "" };
                changed = true;
            }
        }
        if (changed) { Save(snapshot.Messages); }
        return snapshot.Messages;
    }

    void Save(List<InboxMessage> messages)
    {
        DeleteLeftovers();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        try
        {
            using (var file = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(file, new Snapshot(1, messages));
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) { File.Delete(temp); }
        }
    }

    void DeleteLeftovers()
    {
        var prefix = Path.GetFileName(path) + ".";
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var candidate in Directory.EnumerateFiles(Path.GetDirectoryName(path)!))
        {
            var name = Path.GetFileName(candidate);
            if (name.Length == prefix.Length + 36 && name.StartsWith(prefix, comparison) &&
                name.EndsWith(".tmp", comparison) && Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _))
            {
                File.Delete(candidate);
            }
        }
    }
}
