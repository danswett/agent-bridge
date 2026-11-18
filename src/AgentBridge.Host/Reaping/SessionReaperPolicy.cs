using System.Text.RegularExpressions;

namespace AgentBridge.Host.Reaping;

public enum ReaperRole { Builder, Reviewer }
public enum ReaperVerdict { Clean, NotClean }
public sealed record ReaperGit(bool? Clean, bool? Published, bool? Contained, bool? MidOperation,
    string? Head, string? Remote, int? Unpushed, int? StashCount);
public sealed record ReaperSession(string Id, string Name, string Machine)
{
    public ReaperRole? Role { get; init; }
    public string? State { get; init; }
    public DateTimeOffset? At { get; init; }
    public DateTimeOffset? IdleSince { get; init; }
    public DateTimeOffset? SourceUpdatedAt { get; init; }
    public int? RunningTools { get; init; }
    public bool? AskingQuestion { get; init; }
    public bool? Held { get; init; }
    public bool? Private { get; init; }
    public bool? Keep { get; init; }
    public bool? OpenOwnedPr { get; init; }
    public bool? WorkComplete { get; init; }
    public ReaperVerdict? Verdict { get; init; }
    public DateTimeOffset? VerdictAt { get; init; }
    public ReaperGit? Git { get; init; }
}
public sealed record ReaperSnapshot(string Machine, DateTimeOffset At, bool Complete, bool? IsVm,
    bool? OwnerLaptop, IReadOnlyList<ReaperSession> Sessions);
public sealed record ReaperDecision(string Id, string Name, string Machine, ReaperRole? Role, bool Eligible, string Reason)
{
    public DateTimeOffset? SourceUpdatedAt { get; init; }
}

public static class SessionReaperPolicy
{
    public static bool Fresh(DateTimeOffset? at, DateTimeOffset now) =>
        at is { Offset: var offset } stamp && offset == TimeSpan.Zero && stamp <= now && now - stamp <= TimeSpan.FromSeconds(90);

    public static ReaperRole? RoleOf(string name)
    {
        var builder = Regex.IsMatch(name, @"\bbuilder\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var reviewer = Regex.IsMatch(name, @"\breviewer\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return builder == reviewer ? null : builder ? ReaperRole.Builder : ReaperRole.Reviewer;
    }

    public static IReadOnlyList<ReaperDecision> Plan(ReaperSnapshot snapshot, DateTimeOffset now,
        IReadOnlyList<string>? protectedIds = null, IReadOnlyList<string>? protectedNames = null, IReadOnlyList<string>? reserved = null)
    {
        var rows = snapshot.Sessions.Where(row => row.Machine == snapshot.Machine).Select(row =>
        {
            var reason = Reason(row, snapshot, now);
            if (snapshot.Sessions.Count(other => other.Id == row.Id || other.Name == row.Name) != 1) { reason = "ambiguous identity"; }
            if (protectedIds?.Contains(row.Id) == true || protectedNames?.Contains(row.Name, StringComparer.OrdinalIgnoreCase) == true)
                reason = "explicit preservation";
            if (reserved?.Contains(row.Id) == true) { reason = "local stop intent already recorded; never retry"; }
            return new ReaperDecision(row.Id, row.Name, row.Machine, row.Role, reason == "eligible", reason)
                { SourceUpdatedAt = row.SourceUpdatedAt };
        }).ToArray();
        var reserve = rows.Where(row => row.Eligible && row.Role == ReaperRole.Reviewer).OrderBy(row => row.Id, StringComparer.Ordinal).FirstOrDefault();
        return rows.Select(row => row == reserve ? row with { Eligible = false, Reason = "local reviewer reserve (one)" } : row).ToArray();
    }

    static string Reason(ReaperSession row, ReaperSnapshot snapshot, DateTimeOffset now)
    {
        if (!snapshot.Complete || !Fresh(snapshot.At, now)) { return "unknown or stale discovery"; }
        if (new[] { row.Id, row.Name, row.Machine }.Any(value => string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)))
            return "unknown identity";
        if (Regex.IsMatch(row.Name, @"Lead|Chief|Reader|F[- _]?Chain|KEEP|held|private|Builder[- _]*618\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "protected name";
        if (row.Role is null || RoleOf(row.Name) != row.Role) { return "protected or ambiguous role"; }
        if (snapshot.IsVm != true || snapshot.OwnerLaptop != false) { return "protected or unknown host"; }
        if (row.State != "idle" || !Fresh(row.At, now)) { return "not freshly idle"; }
        if (row.RunningTools != 0) { return "running or unknown tools"; }
        if (row.AskingQuestion != false) { return "question or unknown question state"; }
        if (row.Held != false || row.Private != false || row.Keep != false) { return "protected or unknown custody"; }
        if (row.IdleSince is not { Offset: var offset } idle || offset != TimeSpan.Zero || idle > now)
            return "unknown idle timestamp";
        if (now - idle < TimeSpan.FromMinutes(row.Role == ReaperRole.Reviewer ? 60 : 30)) { return "idle threshold"; }
        if (row.Role == ReaperRole.Reviewer)
        {
            if (row.Verdict is not (ReaperVerdict.Clean or ReaperVerdict.NotClean) || row.VerdictAt is not { Offset: var zone } verdict
                || zone != TimeSpan.Zero || verdict > now || (row.Verdict == ReaperVerdict.NotClean && now - verdict < TimeSpan.FromMinutes(60)))
                return "verdict threshold or unknown";
        }
        else if (row.Git is not { Clean: true, Published: true, Contained: true, MidOperation: false, Unpushed: 0, StashCount: 0 } git
            || !Hash(git.Head) || !Hash(git.Remote)) { return "git containment or cleanliness unproved"; }
        return "eligible";
    }

    static bool Hash(string? value) => value is not null && Regex.IsMatch(value, @"\A(?:[a-f0-9]{40}|[a-f0-9]{64})\z");
}
