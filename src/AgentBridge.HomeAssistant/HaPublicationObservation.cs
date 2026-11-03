using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Host.Delivery.Staging;

namespace AgentBridge.HomeAssistant;

internal sealed class HaPublicationReceipts
{
    sealed class LocalTurn
    {
        public readonly SemaphoreSlim Queue = new(1, 1);
        public int Users;
    }
    static readonly object turnsGate = new();
    static readonly Dictionary<string, LocalTurn> turns = new(StringComparer.Ordinal);
    readonly string folder;
    readonly string path;
    readonly string dashboard;
    internal string PublicationKey { get; }

    // Scope receipts to a configured HA instance and dashboard, never to the token.
    public HaPublicationReceipts(string profileFolder, Uri instance, string dashboard)
    {
        if (dashboard.Length == 0 || dashboard.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
        {
            throw new ArgumentException("Invalid dashboard path.", nameof(dashboard));
        }
        this.dashboard = dashboard;
        var key = PublicationKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(HomeAssistantClient.NormalizeBaseUrl(instance).AbsoluteUri.TrimEnd('/') + "\n" + dashboard)));
        folder = Path.GetFullPath(Path.Combine(profileFolder, "ha-publication", key));
        path = Path.Combine(folder, "receipt.json");
    }

    public HaPublicationPolicy? Read() => Locked(ReadCore);

    HaPublicationPolicy? ReadCore()
    {
        PrivateFolder.Ensure(folder, "the publication receipt folder", "no publication is authorized");
        // File.Exists treats inaccessible paths as absent. Open directly so only a genuinely
        // missing file permits a first observation.
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || !IsPrivateFile(path))
            {
                throw new IOException("The publication receipt is not a private regular file.");
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 16384) { throw new InvalidDataException("Oversized publication receipt."); }
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 10 });
            return HaPublicationPolicy.Read(document.RootElement, dashboard);
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException e) { throw new InvalidDataException("Unreadable publication receipt; it is not absence.", e); }
    }

    static bool IsPrivateFile(string path) => OperatingSystem.IsWindows() ? IsPrivateWindowsFile(path) : PrivateFolder.IsPrivate(path);

    [SupportedOSPlatform("windows")]
    static bool IsPrivateWindowsFile(string path)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!me.Equals(security.GetOwner(typeof(SecurityIdentifier)))) { return false; }
        var mine = false;
        // Files inherit the private parent's user-only DACL; unlike the parent, they
        // need not disable inheritance.
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (!me.Equals(rule.IdentityReference)) { return false; }
            mine |= rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights.HasFlag(FileSystemRights.FullControl);
        }
        return mine;
    }

    public void Accept(HaPublicationPolicy policy, CancellationToken cancellationToken = default)
    {
        if (policy.Dashboard != dashboard) { throw new InvalidDataException("Receipt names a different dashboard."); }
        Locked(() =>
        {
            var previous = ReadCore();
            if (previous is not null)
            {
                policy.CheckProgressFrom(previous);
                if (policy.Hash == previous.Hash) { return 0; }
            }
            var pending = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".pending");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var stream = PrivateFolder.CreateNew(pending))
                {
                    stream.Write(Encoding.UTF8.GetBytes(policy.CanonicalJson));
                    stream.Flush(flushToDisk: true);
                }
                if (OperatingSystem.IsWindows()) { ProtectWindowsReceipt(pending); }
                if (!IsPrivateFile(pending)) { throw new IOException("The new publication receipt is not private to this user."); }
                File.Move(pending, path, overwrite: true);
            }

            finally { if (File.Exists(pending)) { File.Delete(pending); } }
            return 0;
        }, cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    static void ProtectWindowsReceipt(string path)
    {
        // Elevated Windows tokens may default a new file's owner to the
        // Administrators group even inside a user-owned private directory.
        var me = WindowsIdentity.GetCurrent().User!;
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        security.SetOwner(me);
        new FileInfo(path).SetAccessControl(security);
    }

    void EnsureLockFile(string lockPath)
    {
        try { File.GetAttributes(lockPath); return; }
        catch (FileNotFoundException) { }
        var pending = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".lock.pending");
        try
        {
            using (var stream = PrivateFolder.CreateNew(pending)) { stream.Flush(flushToDisk: true); }
            if (OperatingSystem.IsWindows()) { ProtectWindowsReceipt(pending); }
            if (!IsPrivateFile(pending)) { throw new IOException("The publication receipt lock is not private."); }
            try { File.Move(pending, lockPath); }
            catch (IOException) when (File.Exists(lockPath)) { }
        }
        finally { if (File.Exists(pending)) { File.Delete(pending); } }
    }

    // The persistent private file's exclusive handle serializes ACL checks too.
    // Never delete the lock: another process could otherwise lock a new inode.
    T Locked<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        var name = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        LocalTurn turn;
        lock (turnsGate)
        {
            if (!turns.TryGetValue(name, out turn!)) { turns[name] = turn = new(); }
            turn.Users++;
        }
        FileStream? held = null;
        var localHeld = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = Stopwatch.StartNew();
            // Synchronous locks permit hot readers to barge ahead. The asynchronous
            // queue admits local callers fairly before cross-process exclusion.
            localHeld = turn.Queue.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).GetAwaiter().GetResult();
            if (!localHeld) { throw new IOException("Publication receipt is busy; no shared write is authorized."); }
            PrivateFolder.Ensure(folder, "the publication receipt folder", "no publication is authorized");
            var lockPath = Path.Combine(folder, "receipt.lock");
            EnsureLockFile(lockPath);
            while (held is null && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint) || !IsPrivateFile(lockPath))
                    {
                        throw new InvalidDataException("The publication receipt lock is not a private regular file.");
                    }
                    held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (Exception e) when (e is not InvalidDataException && e is IOException or UnauthorizedAccessException)
                {
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
                }
            }
            if (held is null) { throw new IOException("Publication receipt is busy; no shared write is authorized."); }
            return action();
        }
        finally
        {
            held?.Dispose();
            if (localHeld) { turn.Queue.Release(); }
            lock (turnsGate)
            {
                if (--turn.Users == 0) { turns.Remove(name); turn.Queue.Dispose(); }
            }
        }
    }
}

internal enum HaPublicationStateKind { Empty, Legacy, Established }
internal sealed record HaPublicationResource(string Id, string Url);
internal sealed record HaPublicationObservation(
    HaPublicationStateKind Kind, HaPublicationPolicy? Policy, HaPublicationResource? PolicyResource,
    HaPublicationResource? CardResource, JsonElement Dashboards, bool DashboardExists, HaDashboard Dashboard)
{
    static readonly Regex PolicyMarker = new(@"#agent-bridge-publication-policy\.js(?:\?|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public string CardUrl => CardResource?.Url ?? "";

    public static async Task<HaPublicationObservation> ReadAsync(IHomeAssistant ha, string dashboard,
        HaPublicationReceipts receipts, CancellationToken cancellationToken = default)
    {
        var resources = await ha.ReadResourcesAsync(cancellationToken);
        var dashboards = await ha.ReadDashboardsAsync(cancellationToken);
        if (resources.ValueKind != JsonValueKind.Array || dashboards.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Unreadable HA publication inventory; it is not absence.");
        }
        HaPublicationResource? policyResource = null, cardResource = null;
        foreach (var resource in resources.EnumerateArray())
        {
            var url = RequiredText(resource, "url");
            var isPolicy = PolicyMarker.IsMatch(url);
            var named = url.StartsWith("data:", StringComparison.Ordinal) && url.Contains('#') ? url[(url.LastIndexOf('#') + 1)..] : url;
            var name = named.Split('?')[0].Split('/', '\\')[^1];
            var isCard = name == "agent-bridge-reply-card.js";
            if (!isPolicy && !isCard) { continue; }
            var id = RequiredText(resource, "id");
            if (id.Length == 0 || RequiredText(resource, "type") != "module")
            {
                throw new InvalidDataException("Bridge resources require storage-mode module registrations.");
            }
            var owned = new HaPublicationResource(id, url);
            if (isPolicy)
            {
                if (policyResource is not null) { throw new InvalidDataException("Multiple publication resources make ownership ambiguous."); }
                policyResource = owned;
            }
            if (isCard)
            {
                if (cardResource is not null) { throw new InvalidDataException("Multiple card resources make ownership ambiguous."); }
                cardResource = owned;
            }
        }
        var targetCount = 0;
        var legacy = cardResource is not null;
        foreach (var entry in dashboards.EnumerateArray())
        {
            var url = RequiredText(entry, "url_path");
            if (RequiredText(entry, "id").Length == 0) { throw new InvalidDataException("Malformed HA dashboard inventory."); }
            if (url == dashboard) { targetCount++; }
            if (url == dashboard || url == "copilot-decisions") { legacy = true; }
        }
        if (targetCount > 1) { throw new InvalidDataException("Multiple target dashboards make publication ambiguous."); }
        var config = targetCount == 0 ? HaDashboard.NotFound : await ha.ReadDashboardAsync(dashboard, cancellationToken);
        var establishedConfig = false;
        if (config.Found)
        {
            using var document = JsonDocument.Parse(config.Json ?? throw new InvalidDataException("Missing dashboard config."));
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("Malformed dashboard config."); }
            establishedConfig = document.RootElement.TryGetProperty("agent_bridge_publication", out _);
        }
        var previous = receipts.Read();
        HaPublicationPolicy? policy = null;
        if (policyResource is not null)
        {
            policy = HaPublicationPolicy.ReadResource(policyResource.Url, dashboard);
            if (previous is not null) { policy.CheckProgressFrom(previous); }
            // Only accept after the entire actual HA snapshot has been validated. Accept
            // checks progression again under the private local lock; it is not a distributed lease.
            receipts.Accept(policy, cancellationToken);
        }
        else if (previous is not null || establishedConfig)
        {
            throw new InvalidDataException("Publication policy was lost after establishment; restore it, never bootstrap.");
        }
        return new(policy is not null ? HaPublicationStateKind.Established : legacy ? HaPublicationStateKind.Legacy : HaPublicationStateKind.Empty,
            policy, policyResource, cardResource, dashboards, targetCount != 0, config);
    }

    static string RequiredText(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            item.EnumerateObject().Count(p => p.Name == name) != 1)
        {
            throw new InvalidDataException("Malformed HA publication inventory; it is not absence.");
        }
        return value.GetString()!;
    }
}
