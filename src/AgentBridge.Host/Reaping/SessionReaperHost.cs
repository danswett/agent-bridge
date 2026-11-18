using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using System.Text.RegularExpressions;

namespace AgentBridge.Host.Reaping;

public static class SessionReaperHost
{
    public static SessionReaperService Create(string folder, string machine, ISessionSource source, Action<string> log,
        CancellationToken stopping = default, ISessionStopper? stops = null, Func<string?>? readOnly = null)
    {
        var git = new SessionReaperGit(new CommandRunner(new Dictionary<string, string>
            { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never" }), log);
        SessionReaperSettings Config()
        {
            var (settings, problem) = HostSettings.Load(folder);
            if (problem is not null) { throw new InvalidDataException("host config unavailable"); }
            return settings.Reaper;
        }
        return new(folder, machine, async ct =>
        {
            await source.RefreshAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var sessions = source.Snapshot();
            var rows = sessions.Select(session => Native(source.GetUnbudgeted(session.Id) ?? session, machine, now)).ToArray();
            var config = Config();
            var snapshot = new ReaperSnapshot(machine, now, source.Held is null,
                config.MachineId == machine ? config.IsVm : null, config.MachineId == machine ? config.OwnerLaptop : null, rows);
            var preflight = SessionReaperPolicy.Plan(snapshot, now, config.ProtectedIds, config.ProtectedNames);
            var needsGit = preflight.Where(row => row.Reason == "git containment or cleanliness unproved").Select(row => row.Id).ToHashSet();
            var needsVerdict = preflight.Where(row => row.Reason == "verdict threshold or unknown").Select(row => row.Id).ToHashSet();
            for (var i = 0; i < rows.Length; i++)
            {
                if (needsVerdict.Contains(rows[i].Id))
                {
                    var verdict = await VerdictAsync(source, rows[i].Id, now, ct);
                    rows[i] = rows[i] with { Verdict = verdict.Verdict, VerdictAt = verdict.At };
                }
                if (needsGit.Contains(rows[i].Id) && source.Get(rows[i].Id)?.Cwd is { } path)
                    rows[i] = rows[i] with { Git = await git.ReadAsync(path, ct) };
            }
            return snapshot;
        }, Config, log, stopping: stopping)
        {
            ResetEvidence = git.Reset,
            CanEnd = () => readOnly is not null && readOnly() is null && stops?.Available == true,
            End = (row, request, ct) => stops is StopService service
                ? service.StopAuditedAsync(request, (_, _) => Config().Live && readOnly?.Invoke() is null
                    && source.Held is null && source.GetUnbudgeted(row.Id) is { } current
                    && StillSafe(row, current, Config(), DateTimeOffset.UtcNow), ct, principal: "host:session-reaper")
                : Task.FromResult(new StopResult(StopOutcome.Refused, "native session.stop unavailable")),
        };
    }

    public static ReaperSession Native(Session session, string machine, DateTimeOffset now)
    {
        var own = session.Activity.Source?.Response is null ? session.Activity.Response : session.Activity.Own?.Response;
        return new(session.Id, session.Title ?? "", machine)
        {
            Role = SessionReaperPolicy.RoleOf(session.Title ?? ""), State = session.Status.ToString().ToLowerInvariant(),
            At = now, IdleSince = session.StatusSince, SourceUpdatedAt = session.UpdatedAt,
            Held = Hold(session.Cwd), Private = session.Title?.Contains("private", StringComparison.OrdinalIgnoreCase),
            Keep = session.Title?.Contains("KEEP", StringComparison.OrdinalIgnoreCase),
            RunningTools = session.Agent == AgentKind.Copilot ? (session.Activity.CurrentTool?.Status == ToolStatus.Running ? 1 : 0)
                + (session.Activity.Background?.Agents ?? 0) + (session.Activity.Background?.Shells ?? 0) : null,
            AskingQuestion = session.Question is not null || session.WaitingOn is not null || session.Status == SessionStatus.Waiting
                || own?.TrimEnd().EndsWith('?') == true,
        };
    }

    static bool? Hold(string? path)
    {
        try
        {
            if (path is null || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
                || path.StartsWith("//", StringComparison.Ordinal)) { return null; }
            return Directory.EnumerateFileSystemEntries(path).Any(entry =>
                string.Equals(Path.GetFileName(entry), ".slipgates-hold", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    public static bool StillSafe(ReaperDecision expected, Session current, SessionReaperSettings config, DateTimeOffset now) =>
        config.Live && expected.Eligible && expected.SourceUpdatedAt is not null && current.UpdatedAt == expected.SourceUpdatedAt
        && current.Id == expected.Id && current.Title == expected.Name && current.Status == SessionStatus.Idle
        && !config.ProtectedIds.Contains(current.Id) && !config.ProtectedNames.Contains(expected.Name, StringComparer.OrdinalIgnoreCase)
        && Native(current, expected.Machine, now) is { Held: false, Private: false, Keep: false, RunningTools: 0, AskingQuestion: false };

    public static async Task<(ReaperVerdict? Verdict, DateTimeOffset? At)> VerdictAsync(
        ISessionSource source, string id, DateTimeOffset now, CancellationToken ct)
    {
        if (!source.ServesHistory) { return default; }
        string? before = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var pageNumber = 0; pageNumber < HistoryLimits.Kept / HistoryLimits.MaxTurns; pageNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await source.HistoryAsync(id, before, HistoryLimits.MaxTurns, ct);
            if (page.Partial == true || page.Missing == true || page.Stale == true || page.Unavailable is not null
                || page.Turns is null) { return default; }
            foreach (var turn in page.Turns.Reverse())
            {
                if (turn.Truncated == true || turn.Unreadable == true || turn.PartsMore is > 0) { return default; }
                foreach (var part in turn.Parts.Reverse())
                {
                    if (part.Kind == HistoryPartKind.Unknown || part.Truncated == true) { return default; }
                    if (part.Kind != HistoryPartKind.Response) { continue; }
                    if (part.Text is null) { return default; }
                    var match = FinalVerdict(part.Text);
                    if (!match.Success)
                    {
                        if (Regex.IsMatch(part.Text, @"\bVERDICT\b|\bNOT[ \t]+CLEAN\b",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) { return default; }
                        continue;
                    }
                    if (part.At == default || part.At.Offset != TimeSpan.Zero || part.At > now) { return default; }
                    var notClean = match.Groups["verdict"].Value.StartsWith("NOT", StringComparison.OrdinalIgnoreCase);
                    return (notClean ? ReaperVerdict.NotClean : ReaperVerdict.Clean, part.At);
                }
            }
            if (page.Before is null || !cursors.Add(page.Before)) { return default; }
            before = page.Before;
        }
        return default;
    }

    static Match FinalVerdict(string text)
    {
        var match = Regex.Match(text.TrimEnd(),
            @"(?:\A|\n) {0,3}(?<bold>\*\*)?(?:VERDICT[ \t:]+(?<verdict>CLEAN|NOT[ \t]+CLEAN)|(?<verdict>NOT[ \t]+CLEAN))(?(bold)\*\*)\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) { return match; }
        string? fence = null;
        foreach (Match marker in Regex.Matches(text[..match.Index], @"(?m)^ {0,3}(?<fence>`{3,}|~{3,})(?<tail>[^\r\n]*)"))
        {
            var run = marker.Groups["fence"].Value;
            if (fence is null) { fence = run; }
            else if (run[0] == fence[0] && run.Length >= fence.Length && string.IsNullOrWhiteSpace(marker.Groups["tail"].Value))
                fence = null;
        }
        return fence is null ? match : Match.Empty;
    }
}
