using System.Text.Json;
using AgentBridge.Host.Core;

namespace AgentBridge.Host.Fleet;

// Red-proof stub: deliberately publishes nothing until the failing-first hosted run is saved.
public sealed class FleetPublisher(ISessionSource source, string machine, Func<string, string?>? name = null, TimeProvider? time = null)
{
    public Task<bool> PublishAsync(Func<JsonElement, CancellationToken, Task> put, bool share = true, bool force = false, CancellationToken ct = default)
    {
        _ = source; _ = machine; _ = name; _ = time;
        return Task.FromResult(false);
    }
}
