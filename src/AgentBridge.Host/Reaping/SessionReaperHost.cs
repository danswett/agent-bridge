using AgentBridge.Host.Core;

namespace AgentBridge.Host.Reaping;

public static class SessionReaperHost
{
    public static SessionReaperService Create(string folder, string machine, ISessionSource source, Action<string> log,
        CancellationToken stopping = default) =>
        new(folder, machine, async ct =>
        {
            await source.RefreshAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var rows = source.Snapshot().Select(session => new ReaperSession(session.Id, session.Title ?? "", machine)
            {
                Role = SessionReaperPolicy.RoleOf(session.Title ?? ""), At = now, IdleSince = session.StatusSince,
                State = session.Status.ToString().ToLowerInvariant(),
            }).ToArray();
            return new(machine, now, source.Held is null, null, null, rows);
        }, () =>
        {
            var (settings, problem) = HostSettings.Load(folder);
            if (problem is not null) { throw new InvalidDataException("host config unavailable"); }
            return settings.Reaper;
        }, log, stopping: stopping);
}
