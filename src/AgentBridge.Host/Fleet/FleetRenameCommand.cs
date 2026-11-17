using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Fleet;

internal static class FleetRenameCommand
{
    internal static async Task RunAsync(Func<Task<JsonElement>> rename, TextWriter output, TextWriter error)
    {
        await error.WriteLineAsync(FleetNamePresentation.Disclosure);
        var result = await rename();
        await output.WriteLineAsync(result.GetRawText());
        await error.WriteLineAsync(FleetNamePresentation.Saved(result));
    }
}
