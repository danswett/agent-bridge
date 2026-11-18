using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using AgentBridge.App.Core;
using AgentBridge.Host.Core.Processes;

namespace AgentBridge.HostLauncher;

public interface IUpdateCoordinatorProcess : IDisposable
{
    int Id { get; }
    long StartedUtcTicks { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancel);
}

public sealed class UpdateCoordinatorAdapter(UpdateCoordinatorRequest request,
    Func<int, IUpdateCoordinatorProcess?>? open = null,
    Func<ProcessStartInfo, IUpdateCoordinatorProcess>? start = null,
    TimeProvider? time = null, Func<string, IDisposable>? pinUpdater = null,
    Func<UpdateCoordinatorRequest, CancellationToken, Task>? slotApply = null)
{
    const string AppExe = "AgentBridge.exe";
    readonly Func<int, IUpdateCoordinatorProcess?> opener = open ?? Open;
    readonly Func<ProcessStartInfo, IUpdateCoordinatorProcess> starter = start ?? Start;
    int running;
    public int? ApplyProcessId { get; private set; }
    public string ApplyPath => Path.Combine(request.Root, slotApply is null ? "Update.exe" : "active-slot.json");
    public Task Ownership { get; private set; } = Task.CompletedTask;

    public Task<UpdateApplyResult> RunAsync(CancellationToken cancel = default)
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("coordinator still owns an unfinished operation");
        return RunOwnedAsync(cancel);
    }

    async Task<UpdateApplyResult> RunOwnedAsync(CancellationToken cancel)
    {
        ApplyProcessId = null;
        Task<UpdateApplyEvidence>? observation = null;
        SlotPointer.Snapshot? active = null;
        PackageDigest? appliedDigest = null;
        var applyStep = "apply-start";
        async Task WaitOriginal(CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.OriginalProcessId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.OriginalStartedUtcTicks);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(request.OriginalStartedUtcTicks, DateTime.MaxValue.Ticks);
            using var original = opener(request.OriginalProcessId);
            if (original is not null && original.StartedUtcTicks == request.OriginalStartedUtcTicks)
                await original.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        async Task<UpdateApplyEvidence> Apply(Guid generation, CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            applyStep = "package-custody";
            using var package = DownloadMark.Hold(request.Package, request.Digest)
                ?? throw new PackageMismatchException("coordinator package no longer matches its verified digest");
            ct.ThrowIfCancellationRequested();
            if (slotApply is not null)
            {
                appliedDigest = new(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(package)));
                applyStep = "slot-activation";
                await slotApply(request, ct).ConfigureAwait(false);
                return new(true, 0, null, generation);
            }
            applyStep = "apply-start";
            using var updater = (pinUpdater ?? PinUpdater)(Path.Combine(request.Root, "Update.exe"));
            ct.ThrowIfCancellationRequested();
            using var child = starter(ApplyStart());
            ApplyProcessId = child.Id;
            applyStep = "apply-completion";
            await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var exit = child.ExitCode;
            // A failing parent may have timed out waiting for an elevated worker.
            return new(exit == 0 ? true : null, exit, null, generation);
        }
        var flow = new UpdateCoordinator(WaitOriginal,
            (generation, ct) => observation ??= Apply(generation, ct),
            () => new FileStream(Path.Combine(request.Packages, ".velopack_lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
            () =>
            {
                if (slotApply is null) { return ReadVersion(); }
                active = new SlotPointer(request.Root).Read() ?? throw new InvalidDataException("Missing activated slot.");
                if (appliedDigest is null || active.Target.Slot.Digest.Algorithm != appliedDigest.Algorithm
                    || !string.Equals(active.Target.Slot.Digest.Hex, appliedDigest.Hex, StringComparison.OrdinalIgnoreCase))
                    throw new PackageMismatchException("Activated slot differs from the verified coordinator package.");
                return active.Target.Slot.Version;
            }, () => { using var app = starter(AppStart(active)); }, time);
        try
        {
            var result = await flow.RunAsync(request.TargetVersion, cancel).ConfigureAwait(false);
            return result.Step == "apply-completion" && result.ErrorType is not null
                ? result with { Step = applyStep } : result;
        }
        finally
        {
            Ownership = Drain();
            async Task Drain()
            {
                try
                {
                    try { await flow.ObservationFinished.ConfigureAwait(false); }
                    catch (OperationCanceledException) { } // The result already records observation timeout/cancellation.
                    await Task.WhenAll(observation ?? Task.CompletedTask, flow.RespawnFinished).ConfigureAwait(false);
                }
                finally { active?.Dispose(); Volatile.Write(ref running, 0); }
            }
        }
    }

    ProcessStartInfo ApplyStart()
    {
        var info = new ProcessStartInfo(Path.Combine(request.Root, "Update.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = request.Root
        };
        foreach (var arg in new[] { "--silent", "apply", "--norestart", "--package", request.Package,
            "--rootDir", request.Root, "--packageDir", request.Packages }) info.ArgumentList.Add(arg);
        return info;
    }

    ProcessStartInfo AppStart(SlotPointer.Snapshot? active)
    {
        var current = active?.Directory ?? Path.Combine(request.Root, "current");
        var info = new ProcessStartInfo(Path.Combine(current, AppExe))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = current
        };
        foreach (var arg in request.RestartArguments) info.ArgumentList.Add(arg);
        return info;
    }

    string? ReadVersion()
    {
        using var file = File.OpenRead(Path.Combine(request.Root, "current", "sq.version"));
        if (file.Length > 1024 * 1024) throw new InvalidDataException("active manifest exceeds its limit");
        using var xml = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(xml);
        if (document.Root?.Name.LocalName != "package") throw new InvalidDataException("active manifest is not a package");
        var metadata = document.Root.Elements().SingleOrDefault(e => e.Name.LocalName == "metadata");
        string? Value(string name) => metadata?.Elements().SingleOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        if (metadata is null || !string.Equals(Value("id"), request.PackageId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Value("mainExe"), AppExe, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("active manifest is not the expected app package");
        return Value("version");
    }

    static FileStream PinUpdater(string path)
    {
        var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (FileVersionInfo.GetVersionInfo(path) is not { ProductName: "Velopack", ProductVersion: "1.2.161" })
                throw new InvalidDataException("unsupported updater completion contract");
            return file;
        }
        catch { file.Dispose(); throw; }
    }

    static IUpdateCoordinatorProcess? Open(int id)
    {
        Process process;
        try { process = Process.GetProcessById(id); }
        catch (ArgumentException) { return null; }
        return Hold(process);
    }

    static IUpdateCoordinatorProcess Start(ProcessStartInfo info) =>
        Hold(ProcessLaunch.Start(info) ?? throw new InvalidOperationException("no process was created"));

    static IUpdateCoordinatorProcess Hold(Process process)
    {
        try { _ = process.Handle; return new Child(process); }
        catch { process.Dispose(); throw; }
    }

    sealed class Child(Process process) : IUpdateCoordinatorProcess
    {
        public int Id => process.Id;
        public long StartedUtcTicks => process.StartTime.ToUniversalTime().Ticks;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken cancel) => process.WaitForExitAsync(cancel);
        public void Dispose() => process.Dispose();
    }
}
