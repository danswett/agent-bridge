using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Local;

namespace AgentBridge.Host.Fleet;

// Tests-first seam; replaced by the connection-scoped publication loop.
public sealed class FleetPublication
{
    public FleetPublication(ISessionSource source, string machine, bool share, Func<string, string?>? name = null,
        TimeProvider? time = null, Action? changed = null, Action<string>? log = null) { }
    public FleetPublicationStatus Status => new("not-started");
    public Task<bool> PollAsync(Func<JsonElement, CancellationToken, Task> put, bool force = false, CancellationToken ct = default) => Task.FromResult(false);
    public Task RunAsync(Func<JsonElement, CancellationToken, Task> put, Task disconnected, CancellationToken ct = default) => Task.CompletedTask;
}
