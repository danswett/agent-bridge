using System.ComponentModel;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Host.Delivery.Staging;

namespace AgentBridge.HostLauncher;

internal sealed class CoordinatorObservation(string path, string applyPath, string targetVersion,
    TimeProvider? time = null)
{
    const int MaxBytes = 64 * 1024;
    readonly TimeProvider clock = time ?? TimeProvider.System;
    bool published;

    internal async Task<UpdateApplyResult> RunAsync(UpdateCoordinatorAdapter adapter,
        Action<UpdateApplyResult>? report = null)
    {
        var began = clock.GetTimestamp();
        var step = "coordinator-run";
        UpdateApplyResult result = new(UpdateApplyEnd.Unknown, null, step, targetVersion,
            null, null, clock.GetUtcNow(), TimeSpan.Zero);
        UpdateApplyResult Failure(UpdateApplyResult previous, string failedStep, Exception error) => previous with
        {
            End = UpdateApplyEnd.Unknown, Code = "coordinator-failed", Step = failedStep, ActiveVersion = null,
            At = clock.GetUtcNow(), Elapsed = clock.GetElapsedTime(began), ErrorType = error.GetType().Name,
            ErrorCode = error is Win32Exception native ? native.NativeErrorCode : error.HResult
        };
        UpdateApplyResult Publish(UpdateApplyResult current)
        {
            var bytes = Encode(current, adapter.ApplyProcessId);
            if (bytes.Length > MaxBytes)
            {
                current = Failure(current, "observation-write",
                    new InvalidDataException("coordinator-observation-too-large."));
                bytes = Encode(current, adapter.ApplyProcessId);
            }
            if (bytes.Length > MaxBytes) throw new InvalidDataException("coordinator-observation-too-large.");
            if (current.End != UpdateApplyEnd.Completed)
                Console.Error.WriteLine(Diagnostic(current, adapter.ApplyProcessId));
            Write(bytes);
            return current;
        }

        try
        {
            try
            {
                result = await adapter.RunAsync().ConfigureAwait(false);
                step = "observation-write";
                result = Publish(result);
                report?.Invoke(result);
            }
            finally
            {
                try { await adapter.Ownership.ConfigureAwait(false); }
                catch { step = "ownership-drain"; throw; }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException or Win32Exception or OperationCanceledException)
        {
            return Publish(Failure(result, step, error));
        }
        return result.End == UpdateApplyEnd.Completed ? result : Publish(result);
    }

    byte[] Encode(UpdateApplyResult result, int? pid)
    {
        var payload = JsonSerializer.SerializeToNode(result)!.AsObject();
        payload["ApplyProcessId"] = pid;
        payload["ApplyPath"] = applyPath;
        payload["ProcessRole"] = "updater";
        payload["Diagnostic"] = Diagnostic(result, pid);
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    string Diagnostic(UpdateApplyResult result, int? pid) =>
        $"updater-process pid={pid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} path={JsonSerializer.Serialize(applyPath)}"
        + $" end={result.End} step={result.Step} code={result.Code ?? "none"} error={result.ErrorType ?? "none"}"
        + $" error-code={result.ErrorCode} at={result.At:O} elapsed={result.Elapsed:c}";

    void Write(byte[] bytes)
    {
        var destination = CoordinatorHandoff.Full(path);
        var temporary = CoordinatorHandoff.Full(path + "." + Guid.NewGuid().ToString("N") + ".partial");
        var created = false;
        try
        {
            using (var stream = PrivateFolder.CreateNew(temporary))
            {
                created = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: published);
            published = true;
        }
        finally { if (created) File.Delete(temporary); }
    }
}
