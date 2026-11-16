using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;

namespace AgentBridge.Host.Fleet;

internal sealed class FleetNameEnrollment(ISessionSource source, FleetNames names, string machine,
    IScopedFleetRoster? roster, TimeProvider time, Action<string> log)
{
    public async Task<object> RenameAsync(JsonElement data, Task closed)
    {
        var (id, name) = FleetNames.Validate(source, data);
        if (closed.IsCompleted) { throw new OperationCanceledException("session.rename: caller disconnected"); }
        bool LocalDuplicate() => name.Length > 0 && source.Snapshot().Any(s => s.Id != id && names.Get(s.Id) == name);
        var duplicateStatus = LocalDuplicate() ? "present" : await CheckAccountAsync(id, name, closed);
        if (LocalDuplicate()) { duplicateStatus = "present"; }
        else if (duplicateStatus == "absent" && source.Held is not null) { duplicateStatus = "unknown"; }
        if (closed.IsCompleted) { throw new OperationCanceledException("session.rename: caller disconnected"); }
        names.Rename(source, data);
        return new { id, name, duplicateStatus };
    }

    async Task<string> CheckAccountAsync(string id, string name, Task closed)
    {
        if (roster is null)
        {
            log("fleet name enrollment: account registry unavailable; duplicate status unknown");
            return "unknown";
        }
        using var cancel = new CancellationTokenSource();
        var limit = TimeSpan.FromSeconds(2);
        var started = time.GetTimestamp();
        var deadline = Task.Delay(limit, time, cancel.Token);
        Task<FleetRosterSnapshot>? reading = null;
        try
        {
            reading = roster.ReadScopedAsync(cancel.Token);
            var completed = await Task.WhenAny(reading, deadline, closed);
            if (closed.IsCompleted) { throw new OperationCanceledException("session.rename: caller disconnected"); }
            if (completed != reading || time.GetElapsedTime(started) >= limit)
            {
                log("fleet name enrollment: account registry deadline; duplicate status unknown");
                return "unknown";
            }
            var snapshot = await reading;
            if (string.IsNullOrWhiteSpace(snapshot.FleetScope))
            {
                log("fleet name enrollment: account scope unavailable; duplicate status unknown");
                return "unknown";
            }
            // Registry ids are the host-derived 16-hex form, not the full native session id.
            var self = HaContract.NodeId(id)["agent_bridge_".Length..];
            var live = snapshot.Machines.Where(m => m.Online).ToArray();
            if (name.Length > 0 && live.Any(m => m.Sessions.Any(s => s.Name?.Trim() == name
                && !(m.Id == machine && (s.Id == id || s.Id == self))))) { return "present"; }
            return live.Any(m => m.Truncated) ? "unknown" : "absent";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException
            or OperationCanceledException or JsonException or FormatException or System.Net.WebSockets.WebSocketException)
        {
            if (closed.IsCompleted) { throw new OperationCanceledException("session.rename: caller disconnected", e); }
            log("fleet name enrollment: account registry unreadable; duplicate status unknown");
            return "unknown";
        }
        finally
        {
            cancel.Cancel();
            // Observe faults even when a timed-out reader ignores cancellation and finishes late.
            if (reading is not null) { _ = reading.ContinueWith(t => { _ = t.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
        }
    }
}
