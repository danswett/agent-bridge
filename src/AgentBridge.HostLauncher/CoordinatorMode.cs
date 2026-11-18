using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.App.Core;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Local;

namespace AgentBridge.HostLauncher;

internal static class CoordinatorMode
{
    internal static bool Requested(string[] args) =>
        args.Length > 0 && args[0].StartsWith("--coordinator", StringComparison.Ordinal);

    internal static Task<int> RunAsync(string[] args) => RunAsync(args,
        Environment.ProcessPath ?? throw new InvalidDataException("coordinator-image-unavailable."),
        CoordinatorHandoff.RuntimeTarget, request => SlotAdapter(request, CoordinatorHandoff.RuntimeTarget));

    internal static UpdateCoordinatorAdapter SlotAdapter(UpdateCoordinatorRequest request, string runtime,
        Func<Stream, string?>? metadata = null, Func<int, IUpdateCoordinatorProcess?>? open = null,
        Func<ProcessStartInfo, IUpdateCoordinatorProcess>? start = null, TimeProvider? time = null) =>
        new(request, open, start, time, slotApply: (apply, cancel) => UpdateApplyRetry.BeforeCommitAsync(commit => Task.Run(() =>
            new SlotMigration(apply.Root, runtime, metadata).Activate(apply.Package, apply.TargetVersion,
                apply.Digest, beforeCommit: commit, cancel: cancel), CancellationToken.None), time, cancel));

    internal static async Task<int> RunAsync(string[] args, string imagePath, string runtimeTarget,
        Func<UpdateCoordinatorRequest, UpdateCoordinatorAdapter> factory,
        Func<Stream, string?>? metadata = null, Action<UpdateApplyResult>? report = null)
    {
        var began = Stopwatch.GetTimestamp();
        var step = "arguments";
        try
        {
            if (args.Length != 2 || args[0] != "--coordinator-v1") throw Bad("coordinator-arguments-refused.");
            step = "handoff-read";
            var manifestPath = CoordinatorHandoff.Full(args[1]);
            imagePath = CoordinatorHandoff.Full(imagePath);
            var directory = Path.GetDirectoryName(manifestPath)!;
            if (!string.Equals(Path.GetDirectoryName(imagePath), directory, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(manifestPath) != "coordinator.json"
                || Path.GetFileName(imagePath) != "agent-bridge-hostw.exe" || !PrivateFolder.IsPrivate(directory))
                throw Bad("coordinator-layout-refused.");
            using var manifest = File.Open(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (manifest.Length > 64 * 1024) throw Bad("handoff-request-too-large.");
            using var document = JsonDocument.Parse(manifest, new() { AllowDuplicateProperties = false, MaxDepth = 16 });
            var info = document.RootElement.Deserialize<UpdateCoordinatorHandoff>(
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
                ?? throw Bad("handoff-request-missing.");
            var request = info.Apply ?? throw Bad("handoff-apply-missing.");
            var digest = info.ImageDigest ?? throw Bad("handoff-image-digest-missing.");
            var root = CoordinatorHandoff.Full(request.Root);
            var packages = CoordinatorHandoff.Full(request.Packages);
            var packagePath = CoordinatorHandoff.Full(request.Package);
            var extract = CoordinatorHandoff.Full(Path.Combine(directory, "extract"));
            if (!CoordinatorHandoff.Within(packages, root) || CoordinatorHandoff.Within(directory, root)
                || CoordinatorHandoff.Within(root, directory) || !PrivateFolder.IsPrivate(extract)
                || !string.Equals(packagePath, Path.Combine(directory, "target.nupkg"), StringComparison.OrdinalIgnoreCase)
                || info.RuntimeTarget != runtimeTarget || digest.Algorithm != PackageDigest.Sha256
                || string.IsNullOrWhiteSpace(request.PackageId) || UpdateVersions.Identity(request.TargetVersion) is null
                || request.Digest is null || request.RestartArguments is null || request.RestartArguments.Length > 32
                || request.RestartArguments.Any(arg => arg is null || arg.Length > 4096 || arg.Contains('\0')))
                throw Bad("handoff-request-refused.");
            step = "helper-copy";
            using var image = DownloadMark.Hold(imagePath, digest)
                ?? throw new PackageMismatchException("coordinator-image-changed.");
            HostwBundle.Verify(image, digest, runtimeTarget);
            CoordinatorImage.RequireV1(image, metadata);
            step = "package-custody";
            using var package = DownloadMark.Hold(packagePath, request.Digest)
                ?? throw new PackageMismatchException("coordinator-target-changed.");
            step = "handoff-claim";
            var resultPath = CoordinatorHandoff.Full(Path.Combine(directory, "coordinator-result.json"));
            using var claim = PrivateFolder.CreateNew(CoordinatorHandoff.Full(Path.Combine(directory, "coordinator.lock")));
            var adapter = factory(request);
            UpdateApplyResult result;
            try
            {
                step = "coordinator-run";
                result = await adapter.RunAsync().ConfigureAwait(false);
                var payload = JsonSerializer.SerializeToNode(result)!.AsObject();
                payload["ApplyProcessId"] = adapter.ApplyProcessId;
                payload["ApplyPath"] = adapter.ApplyPath;
                PrivateFolder.WriteNew(resultPath, JsonSerializer.SerializeToUtf8Bytes(payload));
                report?.Invoke(result);
            }
            finally
            {
                step = "ownership-drain";
                await adapter.Ownership.ConfigureAwait(false);
            }
            return result.End == UpdateApplyEnd.Completed ? 0 : 4;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or ArgumentException or InvalidOperationException or Win32Exception)
        {
            Console.Error.WriteLine($"coordinator-failed step={step} error={error.GetType().Name} hresult={error.HResult} elapsed={Stopwatch.GetElapsedTime(began)}");
            return 4;
        }
    }

    static InvalidDataException Bad(string reason) => new(reason);
}
