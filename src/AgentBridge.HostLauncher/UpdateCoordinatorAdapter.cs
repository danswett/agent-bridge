using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using AgentBridge.App.Core;
using AgentBridge.Host.Core.Processes;
using static AgentBridge.HostLauncher.UpdateCoordinatorStartInfo;

[assembly: InternalsVisibleTo("AgentBridge.App.Tests")]

namespace AgentBridge.HostLauncher;

public sealed class UpdateCoordinatorAdapter(UpdateCoordinatorRequest request,
    Func<int, IUpdateCoordinatorProcess?>? open = null,
    Func<ProcessStartInfo, IUpdateCoordinatorProcess>? start = null,
    TimeProvider? time = null, Func<string, IDisposable>? pinUpdater = null)
{
    readonly Func<int, IUpdateCoordinatorProcess?> opener = open ?? Open;
    readonly Func<ProcessStartInfo, IUpdateCoordinatorProcess> starter = start ?? Start;
    int running;
    public int? ApplyProcessId { get; private set; }
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
            applyStep = "apply-start";
            using var updater = (pinUpdater ?? PinUpdater)(Path.Combine(request.Root, "Update.exe"));
            ct.ThrowIfCancellationRequested();
            using var child = starter(ApplyStart(request));
            ApplyProcessId = child.Id;
            applyStep = "apply-completion";
            await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var exit = child.ExitCode;
            // A failing parent may have timed out waiting for an elevated worker.
            return new(exit == 0 ? true : null, exit, null, generation);
        }
        var (publication, closed) = (new object(), false);
        // Publish the apply before any of it runs, so a racing cancellation still retains it in Ownership.
        var flow = new UpdateCoordinator(WaitOriginal, (generation, ct) =>
        {
            lock (publication)
            {
                if (observation is not null || closed) return observation ?? Task.FromCanceled<UpdateApplyEvidence>(new(true));
                var apply = new Task<Task<UpdateApplyEvidence>>(() => Apply(generation, ct));
                Ownership = observation = apply.Unwrap();
                apply.Start(TaskScheduler.Default);
                return observation;
            }
        },
            () => new FileStream(Path.Combine(request.Packages, ".velopack_lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
            ReadVersion, () => { using var app = starter(AppStart(request)); }, time);
        try
        {
            var result = await flow.RunAsync(request.TargetVersion, cancel).ConfigureAwait(false);
            return result.Step == "apply-completion" && result.ErrorType is not null
                ? result with { Step = applyStep } : result;
        }
        finally
        {
            lock (publication) closed = true;
            Ownership = Task.WhenAll(observation ?? Task.CompletedTask, flow.RespawnFinished);
            _ = Ownership.ContinueWith(owned =>
            {
                // A late apply or respawn fault may never be drained; observing it here keeps it from escalating
                // as unobserved, while a drain of Ownership still sees it.
                _ = owned.Exception;
                Volatile.Write(ref running, 0);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
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
        Hold(ProcessLaunch.Start(info) ?? throw new InvalidOperationException("no process was created"))
            ?? throw new InvalidOperationException("process exited before its handle was held");

    static IUpdateCoordinatorProcess? Hold(Process process) =>
        Hold(process, () => _ = process.Handle, () => process.HasExited, () => new Child(process));

    internal static IUpdateCoordinatorProcess? Hold(IDisposable process, Action pin, Func<bool> exited,
        Func<IUpdateCoordinatorProcess> held)
    {
        try { pin(); return held(); }
        // An original gone between lookup and handle acquisition has exited; anything else stays an error.
        catch (InvalidOperationException) when (exited()) { process.Dispose(); return null; }
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
