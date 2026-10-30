using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Hooks;
using AgentBridge.Host.Core.Intake;

namespace AgentBridge.Host.Migration;

public sealed class MigrationOptions
{
    // The profile's folder: config.json, intake.json and the migration record go here.
    public required string ProfileFolder { get; init; }

    // agent-ha-bridge's installation, ~/.agent-ha-bridge by default.
    public required string BridgeHome { get; init; }

    // Where the hook every migrated agent runs is installed, with its pointer: a folder of the
    // product's own (%LOCALAPPDATA%\AgentBridge\bin), so an app update replacing the app's folder
    // never leaves an agent's hook command pointing at nothing. Each installation's in a folder of
    // its own (HookPath): a rollback keeps the hook and pointer a session started while migrated
    // still runs, and a later migration of another installation must not point it elsewhere.
    public required string HookFolder { get; init; }

    // The hook program as the app ships it, beside the host.
    public required string HookSource { get; init; }

    // %LOCALAPPDATA%, for where agent-ha-bridge staged attachments.
    public string? LocalAppData { get; init; }

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromMilliseconds(250);

    // Held for the whole of a migrate or rollback, by every profile of this user: two at once
    // could each pass the checks before either saved its record, and the second would snapshot
    // the first's imported settings and disabled tasks as the state to restore.
    public string LockPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ProfileFolder))!, "migration.lock");

    public string HookPath(string installation) => Path.Combine(HookFolder, installation, OperatingSystem.IsWindows() ? "agent-bridge-host-hook.exe" : "agent-bridge-host-hook");
    public string PointerPath(string installation) => Path.Combine(HookFolder, installation, HookRunner.PointerFile);
}

public sealed record MigrationReport(bool Ok, IReadOnlyList<string> Lines)
{
    public override string ToString() => string.Join(Environment.NewLine, Lines);
}

// Design G8: moving one machine from agent-ha-bridge's PowerShell daemon to this host, and back,
// explicitly, reversibly and verified (docs/migration.md).
//
// Migrate: check (never the publication writer, G7); snapshot; stop and disable the daemon's
// task; install this product's hook; re-point the Copilot, Claude and Codex hook registrations at
// it; carry the settings over; hand the hook spool to the host (intake.json); and prove the
// hand-over by restarting the host and seeing a hook's event spooled and taken. Any failure from
// the snapshot on rolls back on its own.
//
// Roll back: hand the spool back; restore every file migration rewrote, byte for byte (or, for a
// file edited since, its entries alone); turn the tasks back on and start the daemon.
// ~/.agent-ha-bridge's settings are never written in either direction.
public sealed class Migrator(MigrationOptions options, IAgentHaBridgeRuntime runtime, IMigrationHost host, IHookRunner hook, TimeProvider? time = null)
{
    readonly TimeProvider _time = time ?? TimeProvider.System;

    // ------------------------------------------------------------------ migrate

    // An agent whose registrations run agent-ha-bridge's PowerShell directly, while any of its
    // processes runs: re-pointing the file does not reach a session that started before, and it
    // would go on running those scripts beside the host. Null when there is none.
    //
    // Entries that run the Go hook hand their events to the spool, but only a session started
    // since the file was last written is known to have loaded them: one started earlier may hold
    // the PowerShell hooks the installer replaced. So those count too, while any process of the
    // agent started before that write (or when, cannot be told) runs.
    string? StillRunningPowerShell(AgentHaBridgeInstall install, IReadOnlyList<FilePlan> plans)
    {
        foreach (var plan in plans)
        {
            if (plan.Changes.Count == 0) { continue; }
            var direct = plan.Changes.Any(c => Registrations.RanPowerShell(install, c));
            var (kind, name) = plan.Role switch
            {
                "copilot" => (AgentBridge.Protocol.AgentKind.Copilot, "Copilot"),
                "claude" => (AgentBridge.Protocol.AgentKind.Claude, "Claude"),
                _ => (AgentBridge.Protocol.AgentKind.Codex, "Codex"),
            };
            DateTimeOffset? written = null;
            if (!direct)
            {
                // A few seconds' slack for file-time granularity: a session started in the same
                // moment is not known to have read the file after the write.
                try { written = new DateTimeOffset(File.GetLastWriteTimeUtc(plan.Path), TimeSpan.Zero) + TimeSpan.FromSeconds(2); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            var running = direct || written is null ? runtime.Running(kind) : runtime.Running(kind, written);
            if (running == 0) { continue; }
            var state = running is { } n ? $"{n} {name} process{(n == 1 ? " is" : "es are")} running" : $"whether any {name} process is running could not be checked";
            return direct
                ? $"{name}'s hooks run agent-ha-bridge's PowerShell directly ({plan.Path}), and {state}. A session keeps the hooks it started with, so it would go on writing registrations, approval markers and Home Assistant state beside the host. End {name}'s sessions and migrate again (or let agent-ha-bridge's installer register its native hook first)."
                : $"{name}'s hooks ({plan.Path}) were last written after a {name} session still running started: {state} from before then. A session keeps the hooks it started with, which may be agent-ha-bridge's PowerShell ones its installer since replaced, and those would go on writing registrations, approval markers and Home Assistant state beside the host. End those sessions and migrate again.";
        }
        return null;
    }

    public async Task<MigrationReport> MigrateAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        if (dryRun) { return await MigrateUnlockedAsync(dryRun, cancellationToken); }
        using var held = Lock(out var busy);
        return held is null ? new(false, [busy!]) : await MigrateUnlockedAsync(dryRun, cancellationToken);
    }

    // Opened for this process alone and deleted when closed; null (and why) while another
    // migrate or rollback holds it.
    FileStream? Lock(out string? busy)
    {
        busy = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.LockPath)!);
            return new FileStream(options.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            busy = $"Refused: another migrate or rollback is running ({options.LockPath} is held); wait for it to finish.";
            return null;
        }
    }

    async Task<MigrationReport> MigrateUnlockedAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        void Say(string line) => lines.Add(line);

        // Preflight: every reason to refuse, before anything is touched.
        var (existing, recordProblem) = MigrationRecord.Load(options.ProfileFolder);
        if (recordProblem is not null)
        {
            Say("Refused: " + recordProblem);
            return new(false, lines);
        }
        if (existing is not null)
        {
            Say($"Refused: this profile is already {(existing.State == "migrated" ? "migrated" : "part-way through a migration")} from installation {existing.InstallationId}; roll it back first (agent-bridge-host rollback).");
            return new(false, lines);
        }
        if (IntakeState.Load(options.ProfileFolder).State is not null || File.Exists(Path.Combine(options.ProfileFolder, IntakeState.FileName)))
        {
            Say($"Refused: this profile already takes a hook spool ({IntakeState.FileName}) with no migration record; remove it by hand if it is stale.");
            return new(false, lines);
        }
        var (install, problem) = AgentHaBridgeInstall.Read(options.BridgeHome);
        if (install is null) { Say("Refused: " + problem); return new(false, lines); }
        // One migrated profile at a time: two hosts taking one spool would handle every event
        // twice, and the hook this product installs has one pointer (hook.json), so a second
        // installation's migration would send the first's hooks into the second's spool.
        if (OtherProfileTaking(install) is { } other)
        {
            Say(other.Installation == install.Id
                ? $"Refused: profile {Path.GetFileName(other.Folder)} already takes installation {install.Id}'s hook spool ({Path.Combine(other.Folder, IntakeState.FileName)}); roll that profile back first."
                : $"Refused: profile {Path.GetFileName(other.Folder)} has migrated agent-ha-bridge installation {other.Installation}, and this host takes one installation's hooks at a time; roll that profile back first.");
            return new(false, lines);
        }
        if (install.PublicationProblem() is { } publication) { Say("Refused: " + publication); return new(false, lines); }
        if (!File.Exists(options.HookSource)) { Say($"Refused: the hook program is missing beside the host ({options.HookSource}); reinstall the app."); return new(false, lines); }
        if (!dryRun && !await host.RunningAsync(cancellationToken))
        {
            Say("Refused: no host is serving this profile, so the hand-over could not be checked. Start it (the app, or agent-bridge-host serve) and migrate again.");
            return new(false, lines);
        }

        var hookPath = options.HookPath(install.Id);
        var plans = new[] { Registrations.Copilot(install, hookPath), Registrations.Claude(install, hookPath), Registrations.Codex(install, hookPath) };
        if (plans.FirstOrDefault(p => p.Refusal is not null) is { } blocked) { Say("Refused: " + blocked.Refusal + "."); return new(false, lines); }
        if (StillRunningPowerShell(install, plans) is { } running) { Say("Refused: " + running); return new(false, lines); }
        // Rewriting a hard-linked file either leaves its other names behind (a rename) or risks
        // every name truncated if cut short (in place), so it is not done at all.
        var hostConfigPath = Path.Combine(options.ProfileFolder, HostSettings.FileName);
        if (plans.Where(p => p.NewText is not null).Select(p => p.Path).Append(hostConfigPath).FirstOrDefault(DurableFile.HardLinked) is { } linked)
        {
            Say($"Refused: {linked} has another hard link, which a rewrite would break or could leave truncated. Make it a plain file (or a symbolic link) and migrate again.");
            return new(false, lines);
        }
        var hostConfig = Path.Combine(options.ProfileFolder, HostSettings.FileName);
        // Read once: these bytes are what the import is built from, what is snapshotted, and what
        // the file must still be when it is written.
        var hostBytes = File.Exists(hostConfig) ? File.ReadAllBytes(hostConfig) : null;
        var import = ConfigImport.Build(install.Config, hostBytes is null ? null : Decode(hostBytes));
        if (import.Problems.Count > 0)
        {
            Say("Refused: the settings carried over would leave this profile's config.json with problems: " + string.Join("; ", import.Problems));
            return new(false, lines);
        }

        Say($"{(dryRun ? "Would migrate" : "Migrating")} agent-ha-bridge installation {install.Id} ({install.BridgeHome}) to this host.");
        Describe(install, plans, import, Say);
        if (dryRun)
        {
            Say("Dry run: nothing was changed.");
            return new(true, lines);
        }

        // Snapshot, recorded before anything changes.
        var record = new MigrationRecord { InstallationId = install.Id, BridgeHome = install.BridgeHome, StartedAt = _time.GetUtcNow() };
        var snapshot = MigrationRecord.SnapshotIn(options.ProfileFolder);
        try
        {
            Directory.CreateDirectory(snapshot);
            foreach (var plan in plans.Where(p => p.NewText is not null))
            {
                var backup = $"{plan.Role}.bak";
                DurableFile.Write(Path.Combine(snapshot, backup), plan.Original);
                record.Files.Add(new MigrationRecord.FileRecord { Role = plan.Role, Path = plan.Path, Existed = true, Backup = backup, Before = MigrationRecord.Hash(plan.Original), Link = DurableFile.Link(plan.Path), Changes = [.. plan.Changes] });
            }
            if (import.Text is not null)
            {
                if (hostBytes is not null) { DurableFile.Write(Path.Combine(snapshot, "config.bak"), hostBytes); }
                record.Files.Add(new MigrationRecord.FileRecord { Role = "host-config", Path = hostConfig, Existed = hostBytes is not null, Backup = "config.bak", Before = hostBytes is null ? null : MigrationRecord.Hash(hostBytes), Link = DurableFile.Link(hostConfig) });
            }
            foreach (var path in Checked(install)) { record.Checked.Add(new(path, MigrationRecord.Hash(path))); }
            foreach (var name in new[] { install.TaskName, install.DevBoxTaskName })
            {
                if (runtime.Task(name) is { } task) { record.Tasks.Add(new(name, task.Enabled)); }
            }
            record.Save(options.ProfileFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing has changed yet, so nothing is left behind to roll back.
            try { File.Delete(MigrationRecord.PathIn(options.ProfileFolder)); Directory.Delete(snapshot, recursive: true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            Say($"Refused: the snapshot a rollback needs could not be written ({e.Message}); nothing was changed.");
            return new(false, lines);
        }

        try
        {
            // Turn over: the task first, so nothing restarts what is stopped next.
            foreach (var task in record.Tasks.Where(t => t.Enabled)) { runtime.SetEnabled(task.Name, false); }
            runtime.Stop(install);
            if (!await UntilAsync(() => runtime.Active() is null, options.StopTimeout, cancellationToken))
            {
                return await Undo(record, $"agent-ha-bridge was still running after {options.StopTimeout.TotalSeconds:0} s ({runtime.Active()})", lines, cancellationToken);
            }
            Say($"Stopped agent-ha-bridge's daemon and disabled {string.Join(" and ", record.Tasks.Select(t => t.Name).DefaultIfEmpty("no task"))} (not deleted).");

            record.Hook = InstallHook(install);
            record.Save(options.ProfileFolder);

            // Each file's hash after is recorded before it is written: a migration cut short
            // between the two still rolls back what it wrote (a file still as it was before is
            // left alone).
            // Each is written only if it is still what it was planned from (an agent already on its
            // new hook may have written its settings meanwhile); otherwise the migration is undone,
            // and the change it found is left as it is.
            var writes = plans.Where(p => p.NewText is not null).Select(p => (Role: p.Role, Path: p.Path, Bytes: Utf8(p.NewText!))).ToList();
            if (import.Text is not null) { writes.Add(("host-config", hostConfig, Utf8(import.Text))); }
            foreach (var (role, path, bytes) in writes)
            {
                var file = record.Files.Single(f => f.Role == role);
                file.After = MigrationRecord.Hash(bytes);
                record.Save(options.ProfileFolder);
                try { DurableFile.Replace(path, bytes, file.Before); }
                catch (DurableFile.ChangedMeanwhileException changed)
                {
                    file.After = null;
                    record.Save(options.ProfileFolder);
                    return await Undo(record, changed.Message, lines, cancellationToken);
                }
            }
            new IntakeState(install.RuntimeRoot, install.BridgeHome, install.Id, _time.GetUtcNow()) { Homes = new(install.CopilotHome, install.ClaudeHome, install.CodexHome) }.Save(options.ProfileFolder);
            record.Save(options.ProfileFolder);

            if (await VerifyAsync(install, cancellationToken) is { } failed)
            {
                return await Undo(record, failed, lines, cancellationToken);
            }
            record.State = "migrated";
            record.MigratedAt = _time.GetUtcNow();
            record.Save(options.ProfileFolder);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return await Undo(record, $"{e.GetType().Name}: {e.Message}", lines, cancellationToken);
        }

        Say("Verified: the host restarted with the hook spool, and a hook's event was spooled and taken.");
        Say("Migrated. Roll back at any time with: agent-bridge-host rollback");
        return new(true, lines);
    }

    // The rest of the plan, in words: what changes, and what does not and why.
    void Describe(AgentHaBridgeInstall install, IReadOnlyList<FilePlan> plans, ConfigImport import, Action<string> say)
    {
        var tasks = new[] { install.TaskName, install.DevBoxTaskName }.Where(n => runtime.Task(n) is not null).ToList();
        say(tasks.Count == 0
            ? $"* Daemon: stop it. There is no {install.TaskName} task, so nothing starts it at logon here."
            : $"* Daemon: stop it and disable {string.Join(" and ", tasks)}; the tasks are kept for a rollback.");
        say($"* Hook: install {options.HookPath(install.Id)}, pointing at {install.RuntimeRoot}.");
        say($"* Agent homes: the host reads sessions where agent-ha-bridge's hooks were registered (Copilot {install.CopilotHome}, Claude {install.ClaudeHome}, Codex {install.CodexHome}), unless started with --copilot-home, --claude-home or --codex-home.");
        foreach (var plan in plans)
        {
            if (!plan.Existed) { say($"* {plan.Role}: no {Path.GetFileName(plan.Path)} here; nothing to re-point."); continue; }
            say(plan.Changes.Count == 0
                ? $"* {plan.Role}: no agent-ha-bridge hooks in {plan.Path}; left as it is."
                : $"* {plan.Role}: re-point {plan.Changes.Count} hook(s) in {plan.Path} ({string.Join(", ", plan.Changes.Select(c => c.Event).Distinct())}); nothing else in it changes.");
            foreach (var note in plan.Notes) { say($"  {note}"); }
        }
        say("* Settings carried into this profile's config.json:");
        foreach (var line in import.Imported.DefaultIfEmpty("(none)")) { say($"  {line}"); }
        foreach (var line in import.Kept) { say($"  kept: {line}"); }
        if (import.NotCarried.Count > 0) { say($"  Not carried (no equivalent here; they stay in {install.ConfigPath}): {string.Join(", ", import.NotCarried)}"); }
        say("* Instruction blocks: left in place; they name agent-ha-bridge's agent token, which still works.");
        say($"* Worktrees: agent-ha-bridge's own (marked agent-bridge-created, under {install.WorktreeRoot()}) are neither counted nor claimed by this host. While migrated nobody reclaims them; a rollback lets agent-ha-bridge's daemon resume.");
        var attachments = install.AttachmentRoot(options.LocalAppData);
        say($"* Attachments: files agent-ha-bridge staged stay in {attachments}; its 24 h sweep resumes after a rollback. This host stages its own in its profile and keeps them 24 h too.");
        say("* Home Assistant: the connector is pointed at the same Home Assistant and left off; it needs a token of its own (agent-bridge-host ha token set). The dashboard, its resources and the publication policy are never touched.");
    }

    // The hook beside the host, copied to the product's own folder with a pointer to the runtime.
    MigrationRecord.HookRecord InstallHook(AgentHaBridgeInstall install)
    {
        var hookPath = options.HookPath(install.Id);
        var pointerPath = options.PointerPath(install.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(hookPath)!);
        RecoverHook(hookPath);
        var created = !File.Exists(hookPath);
        if (created || MigrationRecord.Hash(hookPath) != MigrationRecord.Hash(options.HookSource)) { CopyHook(options.HookSource, hookPath); }
        var pointerCreated = !File.Exists(pointerPath);
        DurableFile.Write(pointerPath, HookRunner.Pointer(install.RuntimeRoot));
        var (exit, version, _) = hook.Run(hookPath, ["--version"], "");
        if (exit != 0 || string.IsNullOrWhiteSpace(version)) { throw new IOException($"the hook at {hookPath} did not run (blocked by antivirus?)"); }
        return new(hookPath, pointerPath, created, pointerCreated);
    }

    static void CopyHook(string source, string destination)
    {
        var staged = destination + ".new";
        var previous = destination + ".old";
        File.Copy(source, staged, overwrite: true);
        // A running hook can be renamed but not replaced; one still running finishes on the old.
        var moved = false;
        if (File.Exists(destination)) { File.Move(destination, previous, overwrite: true); moved = true; }
        try
        {
            BeforeHookSwap.Value?.Invoke(staged);
            File.Move(staged, destination);
        }
        catch (Exception e) when (moved && e is IOException or UnauthorizedAccessException)
        {
            // Every migrated registration runs this path: never left pointing at nothing.
            try { File.Move(previous, destination); }
            catch (Exception put) when (put is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{e.Message}; and the previous hook could not be put back ({put.Message}), so it is {previous}", e);
            }
            throw;
        }
        try { File.Delete(previous); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    internal static readonly AsyncLocal<Action<string>?> BeforeHookSwap = new();

    // A replacement cut short (a crash between moving the hook aside and the new one into place)
    // leaves only the previous one, beside where every registration looks: put back. False when
    // there was nothing to put back.
    static bool RecoverHook(string destination)
    {
        var previous = destination + ".old";
        if (File.Exists(destination) || !File.Exists(previous)) { return false; }
        File.Move(previous, destination);
        return true;
    }

    // The hook migrated agents run is a copy of the program beside the host, in a folder of its
    // own (an app update replaces the app's folder, and a registration must never point at
    // nothing). So once an update has brought a new hook, the host brings the copy up to it when
    // it starts: an already-migrated profile cannot migrate again to get it. Its hook.json is left
    // as it is. Null when nothing needed doing (not migrated, the same hook, or a migrate or
    // rollback is running); otherwise what was done, or why not.
    public static string? RefreshHook(string profileFolder, string hookSource)
    {
        var options = new MigrationOptions { ProfileFolder = profileFolder, BridgeHome = "", HookFolder = "", HookSource = hookSource, LocalAppData = "" };
        FileStream? held;
        try { held = new FileStream(options.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        using (held)
        {
            if (MigrationRecord.Load(profileFolder).Record is not { State: "migrated", Hook: { } hook }) { return null; }
            string? recovered = null;
            try
            {
                if (RecoverHook(hook.Path)) { recovered = $"put back the hook migrated agents run ({hook.Path}), whose replacement had been cut short"; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"the hook migrated agents run ({hook.Path}) is missing, and its previous version could not be put back ({e.Message}); migrated agents give no events until it is";
            }
            if (!File.Exists(hookSource)) { return recovered; }
            if (File.Exists(hook.Path) && MigrationRecord.Hash(hook.Path) == MigrationRecord.Hash(hookSource)) { return recovered; }
            try
            {
                CopyHook(hookSource, hook.Path);
                return $"brought the hook migrated agents run ({hook.Path}) up to this version";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"could not bring the hook migrated agents run ({hook.Path}) up to this version ({e.Message}); they keep the previous one";
            }
        }
    }

    // The hand-over, proven: the host restarts and keeps the heartbeat, and an event the hook
    // spools is taken. Null when it worked.
    async Task<string?> VerifyAsync(AgentHaBridgeInstall install, CancellationToken cancellationToken)
    {
        var asked = _time.GetUtcNow();
        if (await host.RestartAsync(cancellationToken) is { } refused) { return $"the host could not be restarted ({refused})"; }
        var fresh = await UntilAsync(() =>
        {
            var at = File.Exists(install.Heartbeat) ? new DateTimeOffset(File.GetLastWriteTimeUtc(install.Heartbeat), TimeSpan.Zero) : DateTimeOffset.MinValue;
            return at >= asked.AddSeconds(-1) && SpoolContract.ConsumerState(install.RuntimeRoot, _time.GetUtcNow()).Alive;
        }, options.VerifyTimeout, cancellationToken);
        if (!fresh) { return $"the host did not start taking the hook spool within {options.VerifyTimeout.TotalSeconds:0} s (no fresh heartbeat in {install.RuntimeRoot})"; }

        // An agent_stop that names no session: Copilot's are only passed on to subscribers, so the
        // check records nothing anywhere. The fixed reply is also what a hook gives when it could
        // not spool, so the reply proves nothing; nor does the hook log, or the spool draining,
        // since a session's own agent_stop meanwhile looks the same. The proof is this run's own
        // spool file, named by its pid and time, appearing and then being taken.
        var spool = SpoolContract.SpoolDirectory(install.RuntimeRoot);
        var named = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var missed = false;
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(spool) { IncludeSubdirectories = false, InternalBufferSize = 64 * 1024 };
            watcher.Created += (_, e) => named.Enqueue(e.Name ?? "");
            watcher.Renamed += (_, e) => named.Enqueue(e.Name ?? "");
            watcher.Error += (_, _) => missed = true;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return $"the installed hook did not spool its event ({spool} cannot be watched: {e.Message}; is it a folder the hook can write to?)";
        }
        using (watcher)
        {
            var ran = SpoolContract.UnixNanoseconds(_time.GetUtcNow().AddMilliseconds(-50));
            var (exit, reply, pid) = hook.Run(options.HookPath(install.Id), ["copilot", "agent_stop"], "{\"agentBridgeMigrationCheck\":true}");
            if (exit != 0 || reply != "{}\n") { return $"the installed hook replied {reply.Trim()} (exit {exit}) instead of Copilot's fixed reply"; }
            string? mine = null;
            await UntilAsync(() => (mine = named.FirstOrDefault(n => Probe(n, pid, ran))) is not null || missed, options.VerifyTimeout, cancellationToken);
            if (mine is null)
            {
                return missed
                    ? $"whether the installed hook spooled its event could not be told (watching {spool} overflowed); migrate again"
                    : $"the installed hook did not spool its event (no spool file from its run, pid {pid}, appeared in {spool}; can it write to {SpoolContract.SpoolFolder}?)";
            }
            var path = Path.Combine(spool, mine);
            var taken = await UntilAsync(() => !File.Exists(path), options.VerifyTimeout, cancellationToken);
            return taken ? null : $"the host did not take the hook's event ({path}) within {options.VerifyTimeout.TotalSeconds:0} s";
        }
    }

    // A spool file written by the hook run with this pid, at or after the check began:
    // SpoolContract.FileName, "<20-digit Unix ns>-<pid>-<hex>.json". The pid alone could be an
    // earlier process's, reused; one running now is the probe.
    static bool Probe(string name, int pid, long since)
    {
        if (!name.EndsWith(".json", StringComparison.Ordinal) || name.Length < 22 || name[20] != '-') { return false; }
        if (!long.TryParse(name.AsSpan(0, 20), out var at) || at < since) { return false; }
        var rest = name.AsSpan(21);
        var dash = rest.IndexOf('-');
        return dash > 0 && int.TryParse(rest[..dash], out var written) && written == pid;
    }

    async Task<MigrationReport> Undo(MigrationRecord record, string why, List<string> lines, CancellationToken cancellationToken)
    {
        lines.Add($"Migration failed: {why}. Rolling back.");
        try
        {
            var back = await RollbackAsync(record, dryRun: false, cancellationToken);
            lines.AddRange(back.Lines.Select(l => "  " + l));
        }
        // The report is the only account of what happened: it is never lost to the undo failing.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lines.Add($"  The rollback failed part-way ({e.GetType().Name}: {e.Message}); the record and snapshot are kept in {options.ProfileFolder}. Run agent-bridge-host rollback.");
        }
        return new(false, lines);
    }

    // ------------------------------------------------------------------ roll back

    public async Task<MigrationReport> RollbackAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        if (dryRun) { return await RollbackUnlockedAsync(dryRun, cancellationToken); }
        using var held = Lock(out var busy);
        return held is null ? new(false, [busy!]) : await RollbackUnlockedAsync(dryRun, cancellationToken);
    }

    async Task<MigrationReport> RollbackUnlockedAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var (record, problem) = MigrationRecord.Load(options.ProfileFolder);
        if (record is null)
        {
            return new(false, [problem ?? "Nothing to roll back: this profile has no migration record."]);
        }
        return await RollbackAsync(record, dryRun, cancellationToken);
    }

    async Task<MigrationReport> RollbackAsync(MigrationRecord record, bool dryRun, CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        void Say(string line) => lines.Add(line);
        var snapshot = MigrationRecord.SnapshotIn(options.ProfileFolder);
        var ok = true;
        Say($"{(dryRun ? "Would roll back" : "Rolling back")} the migration of agent-ha-bridge installation {record.InstallationId} ({record.BridgeHome}).");

        // The spool is handed back before the daemon starts; the host lets go of it when it
        // restarts below, once its config.json is restored too, since it reads both only then.
        if (dryRun) { Say($"* Hook spool: remove {IntakeState.FileName} and restart the host, which then stops taking it and reads its restored settings."); }
        else
        {
            try { IntakeState.Delete(options.ProfileFolder); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ok = false;
                Say($"* {Path.Combine(options.ProfileFolder, IntakeState.FileName)} could not be removed ({e.Message}); the host stands back on its own once the daemon runs (G5). Delete it by hand.");
            }
        }

        // One file that cannot be put back costs that file, never the rest: whatever happens here,
        // the host is restarted and agent-ha-bridge turned back on below, since with intake.json
        // gone nothing else would take the hooks' events.
        void Restore(IEnumerable<MigrationRecord.FileRecord> files)
        {
            foreach (var file in files)
            {
                var backup = Path.Combine(snapshot, file.Backup);
                try
                {
                    RestoreLink(file, dryRun, Say);
                    var current = File.Exists(file.Path) ? File.ReadAllBytes(file.Path) : null;
                    var currentHash = current is null ? null : MigrationRecord.Hash(current);
                    if (file.After is null || currentHash == file.Before)
                    {
                        Say($"* {file.Path}: unchanged by the migration.");
                        continue;
                    }
                    if (currentHash == file.After || current is null)
                    {
                        Say($"* {file.Path}: restored byte for byte.");
                        if (!dryRun)
                        {
                            if (file.Existed) { DurableFile.Replace(file.Path, File.ReadAllBytes(backup), currentHash); }
                            else { DurableFile.Remove(file.Path, currentHash); }
                        }
                        continue;
                    }
                    // Edited since the migration: put back only what it changed.
                    if (file.Changes.Count > 0 && Registrations.Revert(current, file.Changes) is { Text: { } reverted } revert)
                    {
                        // A hook still running this host's program after the revert is one the person
                        // changed past recognition: it is reported, and the snapshot kept for it.
                        var hookProgram = record.Hook?.Path ?? options.HookPath(record.InstallationId);
                        if (Registrations.Names(Utf8(reverted), hookProgram))
                        {
                            ok = false;
                            Say($"* {file.Path}: changed since the migration, and a hook in it still runs {hookProgram} that could not be matched to what it replaced; restore it by hand from {backup}.");
                            if (!dryRun && revert.Replaced > 0) { DurableFile.Replace(file.Path, Utf8(reverted), currentHash); }
                            continue;
                        }
                        if (revert.Replaced == 0)
                        {
                            Say($"* {file.Path}: changed since the migration, and none of the hooks it re-pointed are in it any more; left as it is.");
                            continue;
                        }
                        Say($"* {file.Path}: changed since the migration, so only the command of its {revert.Replaced} re-pointed hook(s) was put back; the rest keeps the later changes.");
                        if (!dryRun) { DurableFile.Replace(file.Path, Utf8(reverted), currentHash); }
                        continue;
                    }
                    if (file.Role == "host-config")
                    {
                        // The host's own settings, changed by a person since: not agent-ha-bridge's to want back.
                        Say($"* {file.Path}: changed since the migration (this host's own settings), so left as it is.");
                        continue;
                    }
                    ok = false;
                    Say($"* {file.Path}: changed since the migration and left as it is; the original is in {backup}.");
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    ok = false;
                    Say($"* {file.Path}: could not be put back ({e.Message}); the original is in {backup}. Restore it by hand.");
                }
            }
        }

        // The host's own settings first: it reads them only when it restarts, below. The hook
        // registrations only once it has let go of the spool: a session starting meanwhile on a
        // restored hook that runs agent-ha-bridge's PowerShell itself would write registrations
        // and approval markers while the intake still does.
        Restore(record.Files.Where(f => f.Role == "host-config"));
        var registrations = record.Files.Where(f => f.Role != "host-config").ToList();
        foreach (var check in record.Checked)
        {
            try
            {
                if (MigrationRecord.Hash(check.Path) != check.Hash) { Say($"* {check.Path}: changed while migrated (not by the migration); left as it is."); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say($"* {check.Path}: could not be read ({e.Message})."); }
        }

        if (!dryRun)
        {
            try
            {
                if (await host.RunningAsync(cancellationToken))
                {
                    Say(await host.RestartAsync(cancellationToken) is { } why
                        ? $"* The host could not be restarted ({why}); it stands back on its own once the daemon runs (G5), and reads its restored settings when it next starts."
                        : "* The host restarted without the hook spool, on its restored settings.");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ok = false;
                Say($"* The host could not be restarted ({e.Message}); it stands back on its own once the daemon runs (G5).");
            }
        }

        var tasks = record.Tasks.Where(t => t.Enabled).ToList();
        if (dryRun)
        {
            Say("* Hook registrations, once the host has let go of the hook spool:");
            Restore(registrations);
            Say($"* Daemon: then re-enable {string.Join(" and ", tasks.Select(t => t.Name).DefaultIfEmpty("(no task)"))} and start agent-ha-bridge.");
            Say("Dry run: nothing was changed.");
            return new(ok, lines);
        }
        // The restart is only asked for: the old host may still be part-way through a pass. The
        // daemon starts only once the intake's claim on the spool is gone, so the two never
        // take the same events.
        var claim = Path.Combine(record.BridgeHome, "runtime", HookIntake.ClaimFile);
        if (!await UntilAsync(() => ClaimFree(claim), options.StopTimeout, cancellationToken))
        {
            // Turned back on, so agent-ha-bridge starts at the next logon whatever happens next; not
            // started now, while the host may still be taking the same events.
            foreach (var task in tasks)
            {
                try { runtime.SetEnabled(task.Name, true); }
                catch (Exception e) when (e is not OperationCanceledException) { Say($"* {task.Name} could not be turned back on ({e.Message}); run agent-ha-bridge configure."); }
            }
            Say($"* The host still holds the hook spool ({claim}) after {options.StopTimeout.TotalSeconds:0} s, so the hook registrations were left as migrated (they hand their events to whichever bridge takes the spool) and agent-ha-bridge was re-enabled but not started. Stop the host (or wait for it to restart) and run rollback again.");
            Say($"Rolled back with problems (above); the record and snapshot are kept in {options.ProfileFolder}.");
            return new(false, lines);
        }
        Restore(registrations);
        foreach (var task in tasks)
        {
            try
            {
                runtime.SetEnabled(task.Name, true);
                runtime.Start(task.Name);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                ok = false;
                Say($"* {task.Name} could not be turned back on ({e.Message}); run agent-ha-bridge configure.");
            }
        }
        var daemonTask = tasks.FirstOrDefault(t => t.Name.StartsWith("AgentBridgeDaemon_", StringComparison.Ordinal));
        if (daemonTask is not null)
        {
            // Seen, not merely uncheckable: a probe that cannot tell would otherwise pass for a daemon
            // back, and the record and snapshot be deleted with nothing running.
            if (await UntilAsync(() => runtime.Seen(record.InstallationId), options.StopTimeout, cancellationToken)) { Say($"* Re-enabled {string.Join(" and ", tasks.Select(t => t.Name))}; agent-ha-bridge is running again."); }
            else
            {
                ok = false;
                Say($"* Re-enabled {daemonTask.Name}, but agent-ha-bridge was not seen running within {options.StopTimeout.TotalSeconds:0} s; check agent-ha-bridge status.");
            }
        }

        if (record.Hook is { } h)
        {
            Say($"* The hook {h.Path} is kept: a session started while migrated still runs it, and it hands its events to agent-ha-bridge's daemon now.");
        }
        if (ok)
        {
            try
            {
                File.Delete(MigrationRecord.PathIn(options.ProfileFolder));
                Directory.Delete(snapshot, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say($"* The migration record or snapshot in {options.ProfileFolder} could not be removed ({e.Message}); delete them by hand."); }
            Say("Rolled back.");
        }
        else { Say($"Rolled back with problems (above); the record and snapshot are kept in {options.ProfileFolder}."); }
        return new(ok, lines);
    }

    // ------------------------------------------------------------------ status

    public string Status()
    {
        var (record, problem) = MigrationRecord.Load(options.ProfileFolder);
        if (problem is not null) { return problem; }
        if (record is null) { return "Not migrated: agent-ha-bridge (if installed) handles this machine's hooks."; }
        var since = record.MigratedAt is { } at ? $" since {at.ToLocalTime():yyyy-MM-dd HH:mm}" : "";
        var text = new StringBuilder($"{(record.State == "migrated" ? "Migrated" : "Part-way through a migration")} from agent-ha-bridge installation {record.InstallationId}{since}.");
        foreach (var f in record.Files.Where(f => f.After is not null))
        {
            var now = MigrationRecord.Hash(f.Path);
            text.Append($"{Environment.NewLine}* {f.Path}: {(now == f.After ? "as migrated" : "changed since")}");
        }
        return text.ToString();
    }

    // ------------------------------------------------------------------ helpers

    // No intake holds it: absent, or free to open alone (a claim a crashed process left behind on a
    // system that does not delete it on close).
    static bool ClaimFree(string path)
    {
        if (!File.Exists(path)) { return true; }
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static IEnumerable<string> Checked(AgentHaBridgeInstall install) =>
        new[] { install.ConfigPath, install.MetadataPath }.Concat(install.InstructionFiles);

    // Another profile of this product (a sibling folder) with an intake.json or a migration
    // record, and the installation it names (this one's id when it takes this one's spool).
    (string Folder, string Installation)? OtherProfileTaking(AgentHaBridgeInstall install)
    {
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(options.ProfileFolder));
        if (root is null || !Directory.Exists(root)) { return null; }
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (AgentHaBridgeInstall.SamePath(folder, options.ProfileFolder)) { continue; }
            if (IntakeState.Load(folder).State is { } state)
            {
                return (folder, AgentHaBridgeInstall.SamePath(state.RuntimeRoot, install.RuntimeRoot) ? install.Id : state.InstallationId);
            }
            if (MigrationRecord.Load(folder).Record is { } record) { return (folder, record.InstallationId); }
            if (File.Exists(Path.Combine(folder, IntakeState.FileName))) { return (folder, "(unreadable intake.json)"); }
            // A migration that profile cannot yet roll back (cut short before intake.json, or a
            // rollback that kept its record): starting another would snapshot its disabled tasks
            // and re-pointed hooks as the state to restore.
            if (File.Exists(MigrationRecord.PathIn(folder))) { return (folder, "(unreadable migration.json)"); }
        }
        return null;
    }

    async Task<bool> UntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (true)
        {
            if (condition()) { return true; }
            if (_time.GetUtcNow() >= deadline) { return false; }
            await Task.Delay(options.PollEvery, _time, cancellationToken);
        }
    }

    // Seams for tests: making the link (Windows lets only an administrator, or developer mode,
    // make one), and the moment before the copy is moved aside.
    internal static Func<string, string, FileSystemInfo> MakeLink { get; set; } = File.CreateSymbolicLink;
    internal static Action<string>? BeforeLinkSwap { get; set; }

    // A file that was a symbolic link when migrated, and is now a plain file holding exactly what
    // the migration wrote through it (something replaced the link with a copy): the link is made
    // again, so the restore below goes through to its target as before.
    //
    // The link is made beside it first, so a link that cannot be made leaves the file as it is
    // (restored below as a plain file). The copy is then moved aside and checked again: an agent
    // saving after the check above keeps its save, and the file is never missing for longer than
    // the two renames; the copy is deleted only once the link is in its place.
    static void RestoreLink(MigrationRecord.FileRecord file, bool dryRun, Action<string> say)
    {
        if (file.Link is not { } link || !link.StartsWith("symlink:", StringComparison.Ordinal)) { return; }
        var info = new FileInfo(file.Path);
        if (!info.Exists || info.LinkTarget is not null || file.After is null || MigrationRecord.Hash(file.Path) != file.After) { return; }
        var target = link["symlink:".Length..];
        if (dryRun) { say($"* {file.Path}: a symbolic link to {target} again, as it was when migrated."); return; }
        var folder = Path.GetDirectoryName(file.Path)!;
        var tag = $"{Path.GetFileName(file.Path)}.agent-bridge-{Guid.NewGuid():N}";
        var staged = Path.Combine(folder, $".{tag}.link");
        var displaced = Path.Combine(folder, $".{tag}.copy");
        try { MakeLink(staged, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            say($"* {file.Path}: could not be made a symbolic link to {target} again ({e.Message}); restored as a plain file.");
            return;
        }
        try
        {
            BeforeLinkSwap?.Invoke(file.Path);
            File.Move(file.Path, displaced);
            if (MigrationRecord.Hash(displaced) != file.After)
            {
                File.Move(displaced, file.Path);
                say($"* {file.Path}: changed while its link to {target} was being made again, so it is left a plain file with that change.");
                return;
            }
            File.Move(staged, file.Path);
            File.Delete(displaced);
            say($"* {file.Path}: a symbolic link to {target} again, as it was when migrated.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Put back unless something new took its place meanwhile; the copy holds only what
            // the migration wrote, which the snapshot records.
            if (File.Exists(displaced))
            {
                if (!File.Exists(file.Path)) { File.Move(displaced, file.Path); } else { File.Delete(displaced); }
            }
            say($"* {file.Path}: could not be made a symbolic link to {target} again ({e.Message}); restored as a plain file.");
        }
        finally
        {
            try { File.Delete(staged); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // As File.ReadAllText reads it (a byte order mark names the encoding).
    static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

}
