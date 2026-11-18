using System.Globalization;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Host.Delivery.Staging;

namespace AgentBridge.HostLauncher;

internal sealed class UpdateApplyObserver(string resultPath)
{
    public void Report(UpdateApplyResult result, int? applyProcessId, string applyPath)
    {
        var pid = applyProcessId?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var payload = JsonSerializer.SerializeToNode(result)!.AsObject();
        payload["ApplyProcessId"] = applyProcessId;
        payload["ApplyPath"] = applyPath;
        payload["ApplyProcessRole"] = "updater";
        payload["Diagnostic"] = result.End == UpdateApplyEnd.Completed ? null
            : FormattableString.Invariant($"Update apply {result.End}: step={result.Step}; code={result.Code}; updater PID={pid}; updater path={applyPath}; elapsed={result.Elapsed:c}");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (bytes.Length > 16 * 1024) throw new InvalidDataException("coordinator-result-too-large.");
        using var file = PrivateFolder.CreateNew(resultPath);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
}
