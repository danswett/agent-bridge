using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
namespace AgentBridge.Host.Delivery.Launch;
public sealed record LaunchStatus(string Request, string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Session,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Permissions, bool Registered, DateTimeOffset Time)
{
    public string? SourceRequest { get; init; }
    public string? Principal { get; init; }
}
public sealed class LaunchStatusLedger
{
    public const int Capacity = 128;
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);
    readonly string? folder;
    readonly Action<string> log;
    readonly Lock gate = new();
    List<LaunchStatus> rows = [];
    public LaunchStatusLedger(string? folder, Action<string> log)
    {
        this.folder = folder;
        this.log = log;
        if (folder is null) { return; }
        try
        {
            using var stream = File.OpenRead(Path.Combine(folder, "results.json"));
            if (stream.Length > 65519) { throw new InvalidDataException("oversize ledger"); }
            var read = JsonSerializer.Deserialize<List<LaunchStatus>>(stream, ProtocolJson.Options);
            if (read is null || read.Count > Capacity || read.Any(r => !Valid(r))
                || read.Select(r => (r.Principal, r.Request)).Distinct().Count() != read.Count) { throw new InvalidDataException("invalid ledger"); }
            // A host restart cannot prove an interrupted launch failed or registered.
            rows = read.Select(r => r.Outcome == "starting" ? r with { Outcome = "unconfirmed" } : r).ToList();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { log($"launch status: ledger unreadable ({e.GetType().Name}); status starts empty"); }
    }
    public static bool Valid(LaunchStatus? r, bool wire = false) => r is not null && Guid.TryParseExact(r.Request, "D", out _)
        && r.Outcome is ("started" or "starting" or "unconfirmed" or "failed" or "refused" or "busy" or "unknown")
        && r.Permissions is (null or LaunchProtocol.Ask or LaunchProtocol.AllowAll) && r.Time != default
        && (r.SourceRequest is null || Guid.TryParseExact(r.SourceRequest, "D", out _))
        && (r.Principal is null || r.Principal is { Length: > 0 and <= 256 })
        && (r.Session is null || (wire ? r.Session.Length == 16 && r.Session.All(char.IsAsciiHexDigitLower) : Guid.TryParseExact(r.Session, "D", out _)))
        && (!r.Registered || r.Session is not null)
        && (r.Outcome != "started" || r.Session is not null);
    public bool Publish(LaunchStatus status)
    {
        try { return PublishChecked(status); }
        catch (ArgumentException) { log("launch status: invalid record rejected; launch completion is unchanged"); return false; }
    }
    bool PublishChecked(LaunchStatus status)
    {
        if (!Valid(status)) { throw new ArgumentException("invalid launch status", nameof(status)); }
        lock (gate)
        {
            var retained = rows.Where(r => r.Time > status.Time - Retention).ToArray();
            var prior = retained.FirstOrDefault(r => r.Principal == status.Principal
                && (r.Request == status.Request || r.SourceRequest == status.Request || r.Request == status.SourceRequest));
            if (status.SourceRequest is null && prior?.SourceRequest is not null) { status = status with { Request = prior.Request, SourceRequest = prior.SourceRequest }; }
            status = status with { Session = status.Session ?? prior?.Session, Permissions = status.Permissions ?? prior?.Permissions };
            if (prior?.Outcome is ("started" or "failed") && status.Outcome is ("starting" or "unconfirmed"))
            { status = status with { Outcome = prior.Outcome, Session = prior.Session, Permissions = prior.Permissions }; }
            var next = retained.Where(r => r.Principal != status.Principal || r.Request != status.Request && r.Request != status.SourceRequest)
                .Append(status with { Registered = false }).OrderByDescending(r => r.Time).ToList();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, ProtocolJson.Options);
            if (next.Count > Capacity || bytes.Length > 65519)
            { log($"launch status: capacity; new record {status.Request} not retained; unexpired outcomes preserved"); return false; }
            if (folder is null) { rows = next; return true; }
            string? temp = null;
            try
            {
                var dir = PrivateFolder.Ensure(folder, "launch status folder", "no status is persisted");
                temp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = PrivateFolder.CreateNew(temp)) { stream.Write(bytes); stream.Flush(true); }
                File.Move(temp, Path.Combine(dir, "results.json"), overwrite: true);
                rows = next;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { log($"launch status: persistence failed ({e.GetType().Name}); launch authorization is unchanged"); return false; }
            finally
            {
                try { if (temp is not null && File.Exists(temp)) { File.Delete(temp); } }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log("launch status: temporary file cleanup failed"); }
            }
        }
    }
    public IReadOnlyList<LaunchStatus> Read() { lock (gate) { return rows.ToArray(); } }
}
