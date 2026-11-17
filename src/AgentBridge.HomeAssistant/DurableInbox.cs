using System.Diagnostics;
using System.Text.Json;

namespace AgentBridge.HomeAssistant;

internal sealed record InboxMessage(string Node, string Rid, string Sender, string Payload, DateTimeOffset QueuedAt, DateTimeOffset? DeliveredAt);

internal sealed class DurableInbox
{
    sealed record Snapshot(int Version, List<InboxMessage> Messages);
    readonly string path;

    public DurableInbox(string path) => this.path = Path.GetFullPath(path);

    public InboxMessage Enqueue(string node, string rid, string sender, string payload, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        ArgumentException.ThrowIfNullOrWhiteSpace(sender);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        using var held = Lock();
        var messages = Load();
        var prior = messages.Find(m => m.Node == node && m.Rid == rid);
        if (prior is not null)
        {
            if (prior.Sender != sender || prior.Payload != payload) { throw new InvalidOperationException("Request id already belongs to a different message."); }
            return prior;
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

    public InboxMessage Deliver(string node, string rid, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
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
        var delivered = message with { DeliveredAt = at };
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
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return []; }
        using (file)
        {
            var snapshot = JsonSerializer.Deserialize<Snapshot>(file);
            if (snapshot is null || snapshot.Version != 1 || snapshot.Messages is null ||
                snapshot.Messages.Any(m => m is null || string.IsNullOrWhiteSpace(m.Node) || string.IsNullOrWhiteSpace(m.Rid) ||
                    string.IsNullOrWhiteSpace(m.Sender) || string.IsNullOrWhiteSpace(m.Payload)) ||
                snapshot.Messages.Select(m => (m.Node, m.Rid)).Distinct().Count() != snapshot.Messages.Count)
            {
                throw new InvalidDataException("Invalid durable inbox snapshot.");
            }
            return snapshot.Messages;
        }
    }

    void Save(List<InboxMessage> messages)
    {
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
}
