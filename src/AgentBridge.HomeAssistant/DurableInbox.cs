using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.HomeAssistant;

internal sealed record InboxMessage(string Node, string Rid, string Sender, string Payload, DateTimeOffset QueuedAt, DateTimeOffset? DeliveredAt,
    DateTimeOffset? StartedAt = null, string? Outcome = null, int Attempt = 0, string? HostRequest = null,
    string? Incarnation = null, List<InboxCleanupFence>? ConsumedTokens = null);
internal sealed record InboxCleanupFence(string Token, long Revision, int Attempt, string HostRequest, string Incarnation, string? Receipt = null);
internal enum InboxResolution { IncarnationEnded, OperatorResolved }
internal sealed record InboxRetirement(string Node, DateTimeOffset At, InboxResolution Resolution);

internal sealed class DurableInbox
{
    const int MaxEntriesPerNode = 100;
    const int MaxEntries = 1000;
    const int AbsencePasses = 3;
    sealed record Snapshot(int Version, List<InboxMessage> Messages, List<InboxRetirement>? Retired = null);
    static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    readonly string path;
    readonly Dictionary<string, int> absent = new(StringComparer.Ordinal);

    public DurableInbox(string path) => this.path = Path.GetFullPath(path);

    public InboxMessage Enqueue(string node, string? rid, string? sender, string payload, DateTimeOffset at)
        => Enqueue(node, rid, sender, payload, at, out _);

    public InboxMessage Enqueue(string node, string? rid, string? sender, string payload, DateTimeOffset at, out bool duplicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        rid = string.IsNullOrWhiteSpace(rid) ? Guid.NewGuid().ToString("N") : rid;
        sender = string.IsNullOrWhiteSpace(sender) ? "legacy" : sender;
        duplicate = false;
        using var held = Lock();
        var state = Load();
        var messages = state.Messages;
        if (state.Retired!.Find(r => r.Node == node) is { } retired)
        {
            return new InboxMessage(node, rid, sender, "", at, retired.At, Outcome: "refused");
        }
        var prior = messages.Find(m => m.Node == node && m.Rid == rid);
        if (prior is not null) { duplicate = true; return prior; }
        var full = false;
        while (messages.Count(m => m.Node == node) >= MaxEntriesPerNode)
        {
            var oldest = messages.FindIndex(m => m.Node == node && m.DeliveredAt is not null && !Pinned(m));
            if (oldest < 0) { full = true; break; }
            messages.RemoveAt(oldest);
        }
        while (!full && messages.Count >= MaxEntries)
        {
            var oldest = messages.FindIndex(m => m.DeliveredAt is not null && !Pinned(m));
            if (oldest < 0) { full = true; break; }
            messages.RemoveAt(oldest);
        }
        var message = new InboxMessage(node, rid, sender, full ? "" : payload, at, full ? at : null, Outcome: full ? "refused" : null);
        messages.Add(message);
        Save(state);
        absent.Remove(node);
        return message;
    }

    public InboxMessage? Peek(string node)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        using var held = Lock();
        return Load().Messages.Find(m => m.Node == node && m.DeliveredAt is null);
    }

    public InboxMessage? Find(string node, string rid)
    {
        using var held = Lock();
        return Load().Messages.Find(m => m.Node == node && m.Rid == rid);
    }

    public void ForgetExcept(IReadOnlySet<string> nodes)
    {
        using var held = Lock();
        var state = Load();
        foreach (var node in nodes) { absent.Remove(node); }
        var expired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in state.Messages.Select(m => m.Node).Distinct().Where(n => !nodes.Contains(n)))
        {
            absent[node] = Math.Min(AbsencePasses, absent.GetValueOrDefault(node) + 1);
            if (absent[node] == AbsencePasses) { expired.Add(node); }
        }
        if (state.Messages.RemoveAll(m => expired.Contains(m.Node) && !Pinned(m)) > 0) { Save(state); }
        foreach (var node in expired) { absent.Remove(node); }
    }

    public void Start(string node, string rid, DateTimeOffset at, string? request = null, string? incarnation = null)
    {
        if ((request is null) != (incarnation is null) || request is not null &&
            (string.IsNullOrWhiteSpace(request) || string.IsNullOrWhiteSpace(incarnation)))
        { throw new ArgumentException("Attempt identity must include request and incarnation."); }
        using var held = Lock();
        var state = Load();
        var messages = state.Messages;
        var index = messages.FindIndex(m => m.Node == node && m.DeliveredAt is null);
        if (index < 0 || messages[index].Rid != rid || messages[index].StartedAt is not null)
        {
            throw new InvalidOperationException("Only an unclaimed FIFO head can start delivery.");
        }
        messages[index] = messages[index] with { StartedAt = at, Attempt = checked(messages[index].Attempt + 1),
            HostRequest = request, Incarnation = incarnation };
        Save(state);
    }

    public void Forget(string node, string? rid = null)
    {
        using var held = Lock();
        var state = Load();
        if (state.Messages.RemoveAll(m => m.Node == node && (rid is null || m.Rid == rid) && !Pinned(m)) > 0) { Save(state); }
    }

    static bool Pinned(InboxMessage m) => m.Outcome is "unconfirmed" or "refused" ||
        (m.StartedAt is not null && m.DeliveredAt is null) || m.ConsumedTokens is { Count: > 0 };
    static bool Valid(InboxCleanupFence f) => f is not null && !string.IsNullOrWhiteSpace(f.Token) && f.Revision >= 0 &&
        f.Attempt > 0 && !string.IsNullOrWhiteSpace(f.HostRequest) && !string.IsNullOrWhiteSpace(f.Incarnation) && f.Receipt is not "";

    public bool ConsumeCleanup(string node, string rid, InboxCleanupFence fence)
    {
        if (!Valid(fence)) { throw new ArgumentException("Invalid cleanup identity.", nameof(fence)); }
        using var held = Lock();
        var state = Load();
        if (state.Retired!.Any(r => r.Node == node) || state.Messages.Where(m => m.Node == node)
            .SelectMany(m => m.ConsumedTokens ?? []).Any(f => f.Token == fence.Token && f.Incarnation == fence.Incarnation)) { return false; }
        var index = state.Messages.FindIndex(m => m.Node == node && m.DeliveredAt is null);
        if (index < 0 || state.Messages[index] is not { StartedAt: not null } m || m.Rid != rid ||
            m.Attempt != fence.Attempt || m.HostRequest != fence.HostRequest || m.Incarnation != fence.Incarnation)
        {
            throw new InvalidOperationException("Cleanup must belong to the started FIFO attempt.");
        }
        state.Messages[index] = m with { ConsumedTokens = [.. m.ConsumedTokens ?? [], fence] };
        Save(state);
        return true;
    }

    // The caller supplies authoritative resolution, never an empty screen or cache expiry.
    public void ResolveAndFence(string node, InboxResolution resolution, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        if (!Enum.IsDefined(resolution)) { throw new ArgumentOutOfRangeException(nameof(resolution)); }
        using var held = Lock();
        var state = Load();
        if (!state.Retired!.Any(r => r.Node == node)) { state.Retired!.Add(new(node, at, resolution)); }
        state.Messages.RemoveAll(m => m.Node == node);
        Save(state);
    }

    public InboxMessage Deliver(string node, string rid, DateTimeOffset at, string outcome = "delivered")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        if (outcome is not ("delivered" or "unconfirmed" or "refused")) { throw new ArgumentOutOfRangeException(nameof(outcome)); }
        using var held = Lock();
        var state = Load();
        var messages = state.Messages;
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
        Save(state);
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

    Snapshot Load()
    {
        DeleteLeftovers();
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) when (!File.Exists(path + ".v2")) { return new(2, [], []); }
        Snapshot? snapshot;
        using (file) { snapshot = JsonSerializer.Deserialize<Snapshot>(file, Json); }
        if (snapshot is null || snapshot.Version is not (1 or 2) || snapshot.Messages is null ||
            (snapshot.Version == 1 && File.Exists(path + ".v2")) ||
            snapshot.Messages.Any(m => m is null || string.IsNullOrWhiteSpace(m.Node) || string.IsNullOrWhiteSpace(m.Rid) ||
                string.IsNullOrWhiteSpace(m.Sender) || m.Payload is null || (m.DeliveredAt is null && string.IsNullOrWhiteSpace(m.Payload))) ||
            snapshot.Messages.Any(m => m.Outcome is not (null or "delivered" or "unconfirmed" or "refused") ||
                (m.DeliveredAt is null && m.Outcome is not null) || m.Attempt < 0 ||
                (m.ConsumedTokens?.Any(f => !Valid(f)) ?? false)) ||
            (snapshot.Version == 2 && snapshot.Retired is null) ||
            (snapshot.Retired?.Any(r => r is null || string.IsNullOrWhiteSpace(r.Node) || !Enum.IsDefined(r.Resolution)) ?? false) ||
            (snapshot.Retired?.Select(r => r.Node).Distinct().Count() ?? 0) != (snapshot.Retired?.Count ?? 0) ||
            snapshot.Messages.Any(m => snapshot.Retired?.Any(r => r.Node == m.Node) == true) ||
            (snapshot.Version == 1 && (snapshot.Retired is not null || snapshot.Messages.Any(m =>
                m.Attempt != 0 || m.HostRequest is not null || m.Incarnation is not null || m.ConsumedTokens is not null || m.Outcome == "refused"))) ||
            snapshot.Messages.Select(m => (m.Node, m.Rid)).Distinct().Count() != snapshot.Messages.Count)
        {
            throw new InvalidDataException("Invalid durable inbox snapshot.");
        }
        if (snapshot.Version == 2) { MarkV2(); }
        var changed = snapshot.Version == 1;
        if (changed) { PreserveV1(); }
        snapshot = snapshot with { Version = 2, Retired = snapshot.Retired ?? [] };
        for (var index = 0; index < snapshot.Messages.Count; index++)
        {
            var message = snapshot.Messages[index];
            if (message.StartedAt is not null && message.Attempt == 0)
            {
                message = message with { Attempt = 1 };
                snapshot.Messages[index] = message;
                changed = true;
            }
            if (message.DeliveredAt is not null && message.Payload.Length > 0)
            {
                snapshot.Messages[index] = message with { Payload = "" };
                changed = true;
            }
        }
        if (changed) { Save(snapshot); }
        return snapshot;
    }

    void PreserveV1()
    {
        var backup = path + ".v1";
        if (File.Exists(backup))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(backup))) { throw new InvalidDataException("Legacy inbox backup differs."); }
            return;
        }
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        using var output = new FileStream(backup, options);
        using var input = File.OpenRead(path);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    void Save(Snapshot state)
    {
        DeleteLeftovers();
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        try
        {
            using (var file = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(file, state);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            MarkV2();
        }
        finally
        {
            if (File.Exists(temp)) { File.Delete(temp); }
        }
    }

    void MarkV2()
    {
        if (File.Exists(path + ".v2")) { return; }
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        using var marker = new FileStream(path + ".v2", options);
        marker.Flush(flushToDisk: true);
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
