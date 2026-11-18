using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using System.Runtime.Versioning;

namespace AgentBridge.Host;

[SupportedOSPlatform("windows")]
public sealed class SlotVelopackLocator : WindowsVelopackLocator
{
    readonly IProcessImpl? actual;
    readonly string? content, entry;
    readonly SemanticVersion? version;

    SlotVelopackLocator(IProcessImpl process, IVelopackLogger log, string root, string content,
        string entry, SemanticVersion version)
        : base(new LegacyProcess(process, Path.Combine(root, "current", entry)), log)
    {
        if (!string.Equals(base.RootAppDir, root, StringComparison.OrdinalIgnoreCase)
            || base.CurrentlyInstalledVersion is null || base.AppId != "AgentBridgeApp")
            throw new InvalidDataException("Slot installation has no matching retained legacy manifest.");
        actual = process; this.content = content; this.entry = entry; this.version = version;
    }

    // Not enabled by either app or host until D3c completes atomic migration.
    public static IVelopackLocator Create(IProcessImpl? process = null, IVelopackLogger? log = null)
    {
        log ??= new NullVelopackLogger();
        process ??= new DefaultProcessImpl(log);
        var path = Path.GetFullPath(process.GetCurrentProcessPath());
        var content = new DirectoryInfo(Path.GetDirectoryName(path)!);
        if (!string.Equals(content.Parent?.Name, "slots", StringComparison.OrdinalIgnoreCase))
            return new WindowsVelopackLocator(process, log);
        var name = content.Name;
        var dash = name.LastIndexOf('-');
        if (dash <= 0 || name.Length - dash - 1 is not (40 or 64) || !name[(dash + 1)..].All(Uri.IsHexDigit)
            || !SemanticVersion.TryParse(name[..dash], out var version)
            || Path.GetFileName(path) is not ("AgentBridge.exe" or "agent-bridge-host.exe" or "agent-bridge-hostw.exe"))
            throw new InvalidDataException("Malformed running slot identity.");
        for (var directory = content; directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Slot locator refuses linked installation ancestry.");
        return new SlotVelopackLocator(process, log, content.Parent!.Parent!.FullName, content.FullName,
            Path.GetFileName(path), version);
    }

    public override string? AppContentDir => content ?? base.AppContentDir;
    public override SemanticVersion? CurrentlyInstalledVersion => version ?? base.CurrentlyInstalledVersion;
    public override IProcessImpl Process => actual ?? base.Process;
    public override string? ThisExeRelativePath => entry ?? base.ThisExeRelativePath;

    sealed class LegacyProcess(IProcessImpl process, string legacyPath) : IProcessImpl
    {
        public string GetCurrentProcessPath() => legacyPath;
        public uint GetCurrentProcessId() => process.GetCurrentProcessId();
        public void StartProcess(string file, IEnumerable<string> args, string workingDirectory, bool silent) =>
            process.StartProcess(file, args, workingDirectory, silent);
        public void Exit(int code) => process.Exit(code);
    }
}
