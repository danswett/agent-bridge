using System.Text;
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
    // never leaves an agent's hook command pointing at nothing.
    public required string HookFolder { get; init; }

    // The hook program as the app ships it, beside the host.
    public required string HookSource { get; init; }

    // %LOCALAPPDATA%, for where agent-ha-bridge staged attachments.
    public string? LocalAppData { get; init; }

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromMilliseconds(250);

    public string HookPath => Path.Combine(HookFolder, OperatingSystem.IsWindows() ? "agent-bridge-host-hook.exe" : "agent-bridge-host-hook");
    public string PointerPath => Path.Combine(HookFolder, HookRunner.PointerFile);
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

    public async Task<MigrationReport> MigrateAsync(bool dryRun, CancellationToken cancellationToken = default)
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
        if (install.PublicationProblem() is { } publication) { Say("Refused: " + publication); return new(false, lines); }
        if (!File.Exists(options.HookSource)) { Say($"Refused: the hook program is missing beside the host ({options.HookSource}); reinstall the app."); return new(false, lines); }
        if (!dryRun && !await host.RunningAsync(cancellationToken))
        {
            Say("Refused: no host is serving this profile, so the hand-over could not be checked. Start it (the app, or agent-bridge-host serve) and migrate again.");
            return new(false, lines);
        }

        var plans = new[] { Registrations.Copilot(install, options.HookPath), Registrations.Claude(install, options.HookPath), Registrations.Codex(install, options.HookPath) };
        var hostConfig = Path.Combine(options.ProfileFolder, HostSettings.FileName);
        var import = ConfigImport.Build(install.Config, File.Exists(hostConfig) ? File.ReadAllText(hostConfig) : null);
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
        Directory.CreateDirectory(snapshot);
        foreach (var plan in plans.Where(p => p.NewText is not null))
        {
            var backup = $"{plan.Role}.bak";
            File.WriteAllBytes(Path.Combine(snapshot, backup), plan.Original);
            record.Files.Add(new MigrationRecord.FileRecord { Role = plan.Role, Path = plan.Path, Existed = true, Backup = backup, Before = MigrationRecord.Hash(plan.Original), Changes = [.. plan.Changes] });
        }
        if (import.Text is not null)
        {
            var existed = File.Exists(hostConfig);
            if (existed) { File.Copy(hostConfig, Path.Combine(snapshot, "config.bak"), overwrite: true); }
            record.Files.Add(new MigrationRecord.FileRecord { Role = "host-config", Path = hostConfig, Existed = existed, Backup = "config.bak", Before = MigrationRecord.Hash(hostConfig) });
        }
        foreach (var path in Checked(install)) { record.Checked.Add(new(path, MigrationRecord.Hash(path))); }
        foreach (var name in new[] { install.TaskName, install.DevBoxTaskName })
        {
            if (runtime.Task(name) is { } task) { record.Tasks.Add(new(name, task.Enabled)); }
        }
        record.Save(options.ProfileFolder);

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

            // Recorded after each file, so a migration cut short still rolls back what it wrote.
            foreach (var plan in plans.Where(p => p.NewText is not null))
            {
                WriteText(plan.Path, plan.NewText!);
                record.Files.Single(f => f.Role == plan.Role).After = MigrationRecord.Hash(plan.Path);
                record.Save(options.ProfileFolder);
            }
            if (import.Text is not null)
            {
                Directory.CreateDirectory(options.ProfileFolder);
                WriteText(hostConfig, import.Text);
                record.Files.Single(f => f.Role == "host-config").After = MigrationRecord.Hash(hostConfig);
                record.Save(options.ProfileFolder);
            }
            new IntakeState(install.RuntimeRoot, install.BridgeHome, install.Id, _time.GetUtcNow()).Save(options.ProfileFolder);
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
        say($"* Hook: install {options.HookPath}, pointing at {install.RuntimeRoot}.");
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
        Directory.CreateDirectory(options.HookFolder);
        var created = !File.Exists(options.HookPath);
        if (created || MigrationRecord.Hash(options.HookPath) != MigrationRecord.Hash(options.HookSource))
        {
            var staged = options.HookPath + ".new";
            File.Copy(options.HookSource, staged, overwrite: true);
            // A running hook can be renamed but not replaced.
            if (File.Exists(options.HookPath)) { File.Move(options.HookPath, options.HookPath + ".old", overwrite: true); }
            File.Move(staged, options.HookPath);
            try { File.Delete(options.HookPath + ".old"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var pointerCreated = !File.Exists(options.PointerPath);
        File.WriteAllBytes(options.PointerPath, HookRunner.Pointer(install.RuntimeRoot));
        var (exit, version) = hook.Run(options.HookPath, ["--version"], "");
        if (exit != 0 || string.IsNullOrWhiteSpace(version)) { throw new IOException($"the hook at {options.HookPath} did not run (blocked by antivirus?)"); }
        return new(options.HookPath, options.PointerPath, created, pointerCreated);
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
        // check records nothing anywhere.
        var (exit, reply) = hook.Run(options.HookPath, ["copilot", "agent_stop"], "{\"agentBridgeMigrationCheck\":true}");
        if (exit != 0 || reply != "{}\n") { return $"the installed hook replied {reply.Trim()} (exit {exit}) instead of Copilot's fixed reply"; }
        var spool = SpoolContract.SpoolDirectory(install.RuntimeRoot);
        var taken = await UntilAsync(() => !Directory.Exists(spool) || !Directory.EnumerateFiles(spool, "*.json").Any(f => f.EndsWith(".json", StringComparison.Ordinal)), options.VerifyTimeout, cancellationToken);
        return taken ? null : $"the host did not take the hook's event from {spool} within {options.VerifyTimeout.TotalSeconds:0} s";
    }

    async Task<MigrationReport> Undo(MigrationRecord record, string why, List<string> lines, CancellationToken cancellationToken)
    {
        lines.Add($"Migration failed: {why}. Rolling back.");
        var back = await RollbackAsync(record, dryRun: false, cancellationToken);
        lines.AddRange(back.Lines.Select(l => "  " + l));
        return new(false, lines);
    }

    // ------------------------------------------------------------------ roll back

    public async Task<MigrationReport> RollbackAsync(bool dryRun, CancellationToken cancellationToken = default)
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

        // Hand the spool back first: the host stands back before the daemon starts.
        if (dryRun) { Say($"* Hook spool: remove {IntakeState.FileName} and restart the host, which then stops taking it."); }
        else
        {
            IntakeState.Delete(options.ProfileFolder);
            if (await host.RunningAsync(cancellationToken))
            {
                Say(await host.RestartAsync(cancellationToken) is { } why ? $"The host could not be restarted ({why}); it stands back on its own once the daemon runs (G5)." : "The host restarted without the hook spool.");
            }
        }

        foreach (var file in record.Files)
        {
            var backup = Path.Combine(snapshot, file.Backup);
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
                    if (file.Existed) { WriteBytes(file.Path, File.ReadAllBytes(backup)); }
                    else { File.Delete(file.Path); }
                }
                continue;
            }
            // Edited since the migration: put back only what it changed.
            if (file.Changes.Count > 0 && Registrations.Revert(current, file.Changes) is { Text: { } reverted } revert)
            {
                if (revert.Replaced == 0)
                {
                    Say($"* {file.Path}: changed since the migration, and none of the hooks it re-pointed are in it any more; left as it is.");
                    continue;
                }
                Say($"* {file.Path}: changed since the migration, so only its {revert.Replaced} re-pointed hook(s) were put back; the rest keeps the later changes.");
                if (!dryRun) { WriteText(file.Path, reverted); }
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

        foreach (var check in record.Checked)
        {
            if (MigrationRecord.Hash(check.Path) != check.Hash) { Say($"* {check.Path}: changed while migrated (not by the migration); left as it is."); }
        }

        var tasks = record.Tasks.Where(t => t.Enabled).ToList();
        if (dryRun)
        {
            Say($"* Daemon: re-enable {string.Join(" and ", tasks.Select(t => t.Name).DefaultIfEmpty("(no task)"))} and start agent-ha-bridge.");
            Say("Dry run: nothing was changed.");
            return new(ok, lines);
        }
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
            if (await UntilAsync(() => runtime.Active() is not null, options.StopTimeout, cancellationToken)) { Say($"* Re-enabled {string.Join(" and ", tasks.Select(t => t.Name))}; agent-ha-bridge is running again."); }
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
            File.Delete(MigrationRecord.PathIn(options.ProfileFolder));
            try { Directory.Delete(snapshot, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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

    static IEnumerable<string> Checked(AgentHaBridgeInstall install) =>
        new[] { install.ConfigPath, install.MetadataPath }.Concat(install.InstructionFiles);

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

    // Replaced in one step, so an agent starting never reads half a settings file.
    static void WriteText(string path, string text) => WriteBytes(path, new UTF8Encoding(false).GetBytes(text));

    static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(partial, bytes);
        File.Move(partial, path, overwrite: true);
    }
}
