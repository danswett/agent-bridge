using System.Globalization;
using System.Text;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Staging;

namespace AgentBridge.Host.Delivery.Mac;

// Discovery may use these launch bindings, but must still identify the agent's native
// session/transcript. Arguments and environment are deliberately not a public snapshot.
public sealed record ManagedTmuxSession(Started Started, string PaneId, string SessionName,
    string Executable, string WorkingDirectory, string? BridgeSession);

// A launch command may have reached tmux. Callers must preserve an uncertain outcome,
// including ownership/attachment state, and must not replay it as a fresh launch.
public sealed class ManagedTmuxStartUnconfirmedException(string message, Exception? inner = null) : IOException(message, inner);

// One instance is shared by launch, answer, reply and stop. It never discovers/adopts a
// pane from the user's tmux server. After a host restart old panes remain read-only.
// G5 and configured read-only policy remain mandatory in the calling delivery services.
public sealed class ManagedTmuxBackend : IAgentStarter, IConsoleInjector, ITargetGuard, IProcessControl
{
    const string PaneFormat = "#{pane_id}\t#{pane_pid}\t#{session_name}\t#{pane_dead}\t#{pid}";
    const string Unknown = "this process is not a verified session launched by this host; use its terminal at the machine";
    readonly ITmuxCommand commands;
    readonly IMacProcessProbe processes;
    readonly Func<string, string?> imageFor;
    readonly string tmuxImage;
    readonly bool supported;
    readonly SemaphoreSlim typing = new(1, 1);
    readonly Lock registryGate = new();
    readonly Dictionary<int, Entry> entries = [];
    readonly Dictionary<int, MacProcessIdentity> boundChildren = [];
    sealed record Entry(ManagedTmuxSession Session, MacProcessIdentity Root, MacProcessIdentity Server);
    sealed record Pane(string Id, int ProcessId, string Session, int ServerId);

    public string SocketPath { get; }

    public ManagedTmuxBackend(string tmuxExecutable, string privateRuntimeDirectory)
    {
        supported = OperatingSystem.IsMacOS();
        if (!OperatingSystem.IsMacOS()) { throw new PlatformNotSupportedException("managed tmux delivery requires macOS"); }
        if (!Path.IsPathFullyQualified(tmuxExecutable) || !File.Exists(tmuxExecutable)) { throw new ArgumentException("tmux must be an existing absolute executable", nameof(tmuxExecutable)); }
        var runtime = PrivateFolder.Ensure(privateRuntimeDirectory, "the managed tmux runtime folder", "no session is started");
        var directory = Path.Combine(runtime, "tmux-" + Guid.NewGuid().ToString("N")[..12]);
        SocketPath = Path.Combine(directory, "s");
        if (Encoding.UTF8.GetByteCount(SocketPath) > 100) { throw new ArgumentException("the private runtime path is too long for a macOS socket", nameof(privateRuntimeDirectory)); }
        if (Directory.Exists(directory) || File.Exists(directory)) { throw new IOException("the private tmux directory already exists"); }
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        tmuxImage = Canonical(tmuxExecutable);
        processes = new MacProcessProbe();
        imageFor = ExecutableImage;
        commands = new TmuxCommand(tmuxExecutable, SocketPath);
    }

    internal ManagedTmuxBackend(ITmuxCommand commands, IMacProcessProbe processes, Func<string, string?> imageFor)
    {
        this.commands = commands; this.processes = processes; this.imageFor = imageFor;
        supported = true; tmuxImage = "/test/tmux"; SocketPath = "/test/private-socket";
    }

    public string? Unavailable() => !supported ? "managed tmux delivery requires macOS"
        : processes.UserId == 0 ? "the host cannot launch or control sessions as root"
        : !Safe(processes.Capture(Environment.ProcessId)) ? "the host's unelevated user identity cannot be verified" : null;

    public string? CommandLine(StartSpec spec, out string? problem)
    {
        problem = Unavailable();
        if (problem is not null) { return null; }
        if (!Path.IsPathFullyQualified(spec.Executable) || !Path.IsPathFullyQualified(spec.WorkingDirectory)
            || spec.Executable.Contains('\0') || spec.WorkingDirectory.Contains('\0') || spec.Arguments.Any(a => a.Contains('\0'))
            || spec.Environment?.Any(p => p.Key.Length == 0 || p.Key.Contains('=') || p.Key.Contains('\0') || p.Value.Contains('\0')) == true)
        { problem = "the launch paths, arguments or environment are invalid"; return null; }
        if (spec.Environment?.Keys.Any(k => k is "HOME" or "CODEX_HOME") == true)
        { problem = "managed sessions do not replace HOME or CODEX_HOME; use explicit agent options"; return null; }
        if (spec.Arguments.Sum(a => (long)Encoding.UTF8.GetByteCount(a) + 1) + Encoding.UTF8.GetByteCount(spec.Executable)
            + (spec.Environment?.Sum(p => (long)Encoding.UTF8.GetByteCount(p.Key) + Encoding.UTF8.GetByteCount(p.Value) + 2) ?? 0) > 128 * 1024)
        { problem = "the launch command is too large"; return null; }
        if (imageFor(spec.Executable) is null) { problem = "the agent's executable or script interpreter cannot be verified"; return null; }
        // This is only a display string. Start always passes separate argv entries.
        return string.Join(' ', new[] { spec.Executable }.Concat(spec.Arguments).Select(Quote));
    }

    public Started Start(StartSpec spec)
    {
        if (CommandLine(spec, out var refusal) is null) { throw new InvalidOperationException(refusal); }
        typing.Wait();
        try
        {
            var name = "bridge-" + Guid.NewGuid().ToString("N");
            var args = new List<string> { "new-session", "-d", "-P", "-F", PaneFormat, "-s", name, "-c", spec.WorkingDirectory.Replace("#", "##", StringComparison.Ordinal), "-x", "220", "-y", "50" };
            // An already-running server does not inherit a later launch's PATH.
            args.AddRange(["-e", "PATH=" + (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin")]);
            foreach (var item in spec.Environment ?? new Dictionary<string, string>()) { args.AddRange(["-e", item.Key + "=" + item.Value]); }
            // tmux invokes a shell for a single command argument. env ensures even an
            // argument-free native executable takes its direct-exec, multiple-argv path.
            args.AddRange(["--", "/usr/bin/env", spec.Executable]);
            args.AddRange(spec.Arguments);
            TmuxResult result;
            try { result = Run(args); }
            catch (Exception e) { throw new ManagedTmuxStartUnconfirmedException("tmux did not confirm whether the session started; check at the machine before retrying", e); }
            var pane = result.ExitCode == 0 ? ParsePane(result.Output) : null;
            if (pane is null || pane.Session != name) { throw new ManagedTmuxStartUnconfirmedException("tmux did not confirm the new managed pane; check at the machine before retrying"); }
            var expected = imageFor(spec.Executable);
            MacProcessIdentity? root = null;
            for (var attempt = 0; attempt < 80; attempt++)
            {
                root = processes.Capture(pane.ProcessId);
                if (Safe(root) && root!.Executable == expected) { break; }
                root = null;
                Thread.Sleep(25);
            }
            var server = processes.Capture(pane.ServerId);
            if (root is null || !Safe(server) || server!.Executable != tmuxImage)
            { throw new ManagedTmuxStartUnconfirmedException("the launched pane's process identity could not be verified; it remains read-only"); }
            var started = new Started(root.ProcessId, root.ProcessStartTime);
            var hint = spec.Environment?.GetValueOrDefault("AGENT_BRIDGE_SESSION");
            // Only the known session UUID (or fixed Codex launch marker) is published.
            if (hint != "launched" && !Guid.TryParseExact(hint, "D", out _)) { hint = null; }
            var entry = new Entry(new(started, pane.Id, name, spec.Executable, spec.WorkingDirectory, hint), root, server);
            if (!VerifyPane(entry)) { throw new ManagedTmuxStartUnconfirmedException("the launched pane changed before it could be registered; it remains read-only"); }
            lock (registryGate) { entries[root.ProcessId] = entry; }
            return started;
        }
        finally { typing.Release(); }
    }

    public IReadOnlyList<ManagedTmuxSession> Snapshot()
    {
        Entry[] all;
        lock (registryGate)
        {
            foreach (var (pid, entry) in entries.ToArray()) { if (processes.HasExited(entry.Root)) { entries.Remove(pid); } }
            foreach (var (pid, child) in boundChildren.ToArray()) { if (processes.HasExited(child)) { boundChildren.Remove(pid); } }
            all = [.. entries.Values];
        }
        return all.Where(VerifyPane).Select(e => e.Session).ToArray();
    }

    public bool Running(Started started) => Find(started.ProcessId, out _) is { } entry && entry.Session.Started == started;

    public bool? Owns(Started started, int processId)
    {
        var entry = Find(processId, out _);
        // Unknown is false, never a timing-only invitation to associate a different pane.
        return entry is not null && entry.Session.Started == started;
    }

    public string? Refuse(int processId) => Unavailable() ?? (Find(processId, out _) is null ? Unknown : null);

    public async Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default)
    {
        await typing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Unavailable() is { } unavailable) { return new(null, unavailable); }
            var entry = Find(processId, out _);
            if (entry is null) { return new(null, Unknown); }
            return await ScreenAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { return new(null, "the managed pane's screen could not be read"); }
        finally { typing.Release(); }
    }

    public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
    {
        var possible = false;
        await typing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Unavailable() is { } unavailable) { return Result(unavailable); }
            var entry = Find(processId, out var target);
            if (entry is null || target is null) { return Result(Unknown); }
            if (plan.Proceed?.Invoke() is { } reason) { return Result(reason); }
            // Validate the whole plan before the first write, including later sends.
            if (plan.Sends.Any(s => s.EnterDelayMs < 0 || s.PauseAfterMs < 0 || s.EnterDelayMs > 30_000 || s.PauseAfterMs > 30_000 || !ValidKeys(s.Text)))
            { return Result("the key plan contains unsupported terminal controls or delays"); }
            foreach (var send in plan.Sends)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!StillTarget(entry, target)) { return Result(Unknown); }
                if (send.Expect is { Count: > 0 })
                {
                    var screen = await ScreenAsync(entry, cancellationToken).ConfigureAwait(false);
                    if (screen.Rows is not { } rows || send.Expect.Any(r => r.Row < 0 || r.Row >= rows.Count || rows[r.Row].TrimEnd(' ') != r.Text.TrimEnd(' ')))
                    { return Result("the prompt on the managed pane changed; look again before answering"); }
                }
                var first = true;
                foreach (var (text, literal) in Keys(send.Text))
                {
                    if (!await Send(text, literal, first ? send.Expect : null).ConfigureAwait(false)) { return Result("tmux did not confirm the key delivery; check the session before retrying"); }
                    first = false;
                }
                if (send.Enter)
                {
                    await Task.Delay(send.EnterDelayMs, cancellationToken).ConfigureAwait(false);
                    if (!await Send("Enter", false, first ? send.Expect : null).ConfigureAwait(false)) { return Result("tmux did not confirm Enter; check the session before retrying"); }
                }
                await Task.Delay(send.PauseAfterMs, cancellationToken).ConfigureAwait(false);
            }
            return new(InjectionStatus.Typed, "keys delivered to the verified managed pane");

            async Task<bool> Send(string text, bool literal, IReadOnlyList<ScreenRow>? expected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!StillTarget(entry, target)) { return false; }
                string? buffer = null;
                try
                {
                    if (literal)
                    {
                        // The words may contain secrets. They travel over stdin and the
                        // private server socket, never a process's visible argv.
                        buffer = "bridge-" + Guid.NewGuid().ToString("N");
                        if ((await commands.RunAsync(["load-buffer", "-b", buffer, "--", "-"], cancellationToken, text).ConfigureAwait(false)).ExitCode != 0) { return false; }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!StillTarget(entry, target)) { return false; }
                    // Loading a buffer introduced another await. Recheck the held rows
                    // at the actual delivery boundary, before the first key or paste.
                    if (expected is { Count: > 0 })
                    {
                        var screen = await ScreenAsync(entry, cancellationToken).ConfigureAwait(false);
                        if (screen.Rows is not { } rows || expected.Any(r => r.Row < 0 || r.Row >= rows.Count || rows[r.Row].TrimEnd(' ') != r.Text.TrimEnd(' '))) { return false; }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!StillTarget(entry, target)) { return false; }
                    IReadOnlyList<string> args = buffer is not null
                        ? ["paste-buffer", "-d", "-p", "-b", buffer, "-t", entry.Session.PaneId]
                        : ["send-keys", "-t", entry.Session.PaneId, "--", text];
                    // Loading alone cannot type anything. From paste/key dispatch onward
                    // every failure is uncertain, including a lost command response.
                    possible = true;
                    return (await commands.RunAsync(args, cancellationToken).ConfigureAwait(false)).ExitCode == 0;
                }
                finally
                {
                    if (buffer is not null)
                    {
                        // -d removes a successfully pasted buffer. This also removes one
                        // left by cancellation, a changed prompt or an uncertain paste.
                        try { await commands.RunAsync(["delete-buffer", "-b", buffer]).ConfigureAwait(false); }
                        catch (Exception) { }
                    }
                }
            }
        }
        catch (Exception) { return Result("delivery to the managed pane was interrupted; check the session before retrying"); }
        finally { typing.Release(); }

        InjectionResult Result(string reason) => new(possible ? InjectionStatus.Interrupted : InjectionStatus.NothingTyped, reason);
    }

    public HeldOpen Open(int processId, DateTimeOffset startedAt)
    {
        var entry = Find(processId, out var target);
        if (Unavailable() is not null || entry is null || target is null
            || (target.StartedAt != startedAt && target.ProcessStartTime != startedAt)) { return HeldOpen.Refused(Unknown); }
        return HeldOpen.Of(new HeldPane(this, entry, target));
    }

    sealed class HeldPane(ManagedTmuxBackend owner, Entry entry, MacProcessIdentity target) : IHeldProcess
    {
        public int Id => target.ProcessId;
        public void Dispose() { }
        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                if (owner.processes.HasExited(target)) { return true; }
                await Task.Delay(50).ConfigureAwait(false);
            } while (clock.Elapsed < timeout);
            return owner.processes.HasExited(target);
        }
        public string? Terminate()
        {
            owner.typing.Wait();
            try
            {
                if (owner.Unavailable() is not null || !owner.StillTarget(entry, target)) { return Unknown; }
                // macOS has no Windows-style retained process handle. Closing only this
                // validated owned pane avoids a kill(pid) race with PID reuse entirely.
                return owner.Run(["kill-pane", "-t", entry.Session.PaneId]).ExitCode == 0 ? null : "tmux did not confirm closing the managed pane";
            }
            catch (Exception) { return "the managed pane could not be closed; check at the machine"; }
            finally { owner.typing.Release(); }
        }
    }

    Entry? Find(int processId, out MacProcessIdentity? target)
    {
        target = processes.Capture(processId);
        if (!Safe(target)) { return null; }
        var current = target;
        for (var depth = 0; depth < 32 && current is not null; depth++)
        {
            Entry? entry;
            lock (registryGate) { entries.TryGetValue(current.ProcessId, out entry); }
            if (entry is not null)
            {
                if (!Same(entry.Root, current) || !VerifyPane(entry)) { return null; }
                lock (registryGate)
                {
                    if (boundChildren.TryGetValue(processId, out var previous) && !Same(previous, target!)
                        && !processes.HasExited(previous)) { return null; }
                    boundChildren[processId] = target!;
                }
                return entry;
            }
            var parent = processes.Capture(current.ParentProcessId);
            if (!Safe(parent) || parent!.StartedAt > current.StartedAt || parent.ProcessId == current.ProcessId) { return null; }
            current = parent;
        }
        return null;
    }

    bool StillTarget(Entry entry, MacProcessIdentity target) => Same(target, processes.Capture(target.ProcessId))
        && Find(target.ProcessId, out _) == entry;

    bool VerifyPane(Entry entry)
    {
        try
        {
            if (!Same(entry.Root, processes.Capture(entry.Root.ProcessId)) || !Same(entry.Server, processes.Capture(entry.Server.ProcessId))) { return false; }
            var result = Run(["display-message", "-p", "-t", entry.Session.PaneId, PaneFormat]);
            var pane = result.ExitCode == 0 ? ParsePane(result.Output) : null;
            return pane is not null && pane.Id == entry.Session.PaneId && pane.ProcessId == entry.Root.ProcessId
                && pane.Session == entry.Session.SessionName && pane.ServerId == entry.Server.ProcessId;
        }
        catch (Exception) { return false; }
    }

    async Task<ConsoleScreen> ScreenAsync(Entry entry, CancellationToken token)
    {
        var read = await commands.RunAsync(["capture-pane", "-p", "-t", entry.Session.PaneId], token).ConfigureAwait(false);
        if (read.ExitCode != 0 || !VerifyPane(entry)) { return new(null, "the managed pane changed while its screen was read"); }
        return new(read.Output.TrimEnd('\n').Split('\n'), null);
    }

    bool Safe(MacProcessIdentity? p) => p is not null && processes.UserId != 0 && p.UserId == processes.UserId
        && p.RealUserId == processes.UserId && p.SavedUserId == processes.UserId && Path.IsPathFullyQualified(p.Executable);
    bool Same(MacProcessIdentity expected, MacProcessIdentity? actual) => Safe(actual) && expected == actual;
    TmuxResult Run(IReadOnlyList<string> args) => commands.RunAsync(args).GetAwaiter().GetResult();
    static Pane? ParsePane(string value)
    {
        var parts = value.TrimEnd('\r', '\n').Split('\t');
        return parts.Length == 5 && parts[0].StartsWith('%') && int.TryParse(parts[0].AsSpan(1), out _)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
            && parts[3] == "0" && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var server) && server > 0
            ? new(parts[0], pid, parts[2], server) : null;
    }

    static bool ValidKeys(string text) => Keys(text).All(k => !k.Literal || InjectableText.Collapse(k.Text) == k.Text);
    static IEnumerable<(string Text, bool Literal)> Keys(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (text.AsSpan(i).StartsWith(KeyPlan.Up)) { yield return ("Up", false); i += 3; }
            else if (text.AsSpan(i).StartsWith(KeyPlan.Down)) { yield return ("Down", false); i += 3; }
            else if (text[i] == '\u001b')
            {
                // A lone Escape is an intentional key; an unknown escape sequence is
                // refused by ValidKeys rather than interpreted as terminal input.
                if (text.Length != 1) { yield return (text[i..], true); yield break; }
                yield return ("Escape", false); i++;
            }
            else
            {
                var end = text.IndexOf('\u001b', i);
                if (end < 0) { end = text.Length; }
                yield return (text[i..end], true); i = end;
            }
        }
    }

    static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    static string Canonical(string path) => new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path);
    static string? ExecutableImage(string path)
    {
        try
        {
            path = Canonical(path);
            using var stream = File.OpenRead(path);
            var header = new byte[512];
            var count = stream.Read(header);
            if (count < 2 || header[0] != '#' || header[1] != '!') { return path; }
            var line = Encoding.UTF8.GetString(header, 2, count - 2).Split('\n')[0].Trim();
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || !Path.IsPathFullyQualified(words[0])) { return null; }
            if (words[0] != "/usr/bin/env") { return File.Exists(words[0]) ? Canonical(words[0]) : null; }
            // Only an unambiguous env PROGRAM shebang; env options and shell fragments
            // require explicit executable configuration, never guessed interpretation.
            if (words.Length != 2 || words[1].StartsWith('-') || words[1].Contains('/')) { return null; }
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (!Path.IsPathFullyQualified(folder)) { continue; }
                var candidate = Path.Combine(folder, words[1]);
                if (File.Exists(candidate)) { return Canonical(candidate); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }
}
