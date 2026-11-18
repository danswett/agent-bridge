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
            var needsGit = SessionReaperPolicy.Plan(snapshot, now, config.ProtectedIds, config.ProtectedNames)
                .Where(row => row.Reason == "git containment or cleanliness unproved").Select(row => row.Id).ToHashSet();
            for (var i = 0; i < rows.Length; i++)
            {
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
                    && source.Held is null && source.Get(row.Id) is { Status: SessionStatus.Idle, Question: null } current
                    && current.Title == row.Name, ct, principal: "host:session-reaper")
                : Task.FromResult(new StopResult(StopOutcome.Refused, "native session.stop unavailable")),
        };
    }

    public static ReaperSession Native(Session session, string machine, DateTimeOffset now)
    {
        var own = session.Activity.Source?.Response is null ? session.Activity.Response : session.Activity.Own?.Response;
        var matches = Regex.Matches(own ?? "", @"(?im)^VERDICT[: ]+(CLEAN|NOT CLEAN)[ \t]*(?:\r?\n|\z)");
        var cut = session.Activity.Source?.Response is null ? session.Activity.ResponseTruncated : session.Activity.Own?.ResponseTruncated;
        var verdict = matches.Count == 1 && cut != true ? matches[0].Groups[1].Value.ToUpperInvariant() : null;
        return new(session.Id, session.Title ?? "", machine)
        {
            Role = SessionReaperPolicy.RoleOf(session.Title ?? ""), State = session.Status.ToString().ToLowerInvariant(),
            At = now, IdleSince = session.StatusSince,
            RunningTools = session.Agent == AgentKind.Copilot ? (session.Activity.CurrentTool?.Status == ToolStatus.Running ? 1 : 0)
                + (session.Activity.Background?.Agents ?? 0) + (session.Activity.Background?.Shells ?? 0) : null,
            AskingQuestion = session.Question is not null || session.WaitingOn is not null || session.Status == SessionStatus.Waiting
                || own?.TrimEnd().EndsWith('?') == true,
            Verdict = verdict == "CLEAN" ? ReaperVerdict.Clean : verdict == "NOT CLEAN" ? ReaperVerdict.NotClean : null,
            VerdictAt = verdict == "CLEAN" ? now : session.Activity.Own?.At,
        };
    }
}
