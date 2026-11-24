using System.Text;

namespace AgentBridge.ScriptRun;

// What the host answered to scripts.admit: the admission id, and the name of the script's own protected credential file (never a path).
public sealed record Admission(Guid Id, string File);

public sealed class HostUnavailableException : Exception;
public sealed class HostRefusedException : Exception;

// The three things the launcher touches, behind ports so the order and the secrecy rules are tested with fakes; the real ones (the local pipe, the DPAPI file,
// CreateProcessW) are the next slice.
public interface IHostPort
{
    Task<Admission> AdmitAsync(string name, int version, CancellationToken ct);
    Task FinishedAsync(Guid admission, int exit, CancellationToken ct);
}

public interface ITokenSource
{
    // The token's bytes (the caller zeroes them), or null when the file cannot be read.
    byte[]? Read(string file);
}

public interface IChildStarter
{
    // Starts the program with exactly this environment block (Unicode, sorted, double-null terminated), waits, and returns its exit code.
    Task<int> RunAsync(string program, IReadOnlyList<string> arguments, char[] environment, CancellationToken ct);
}

public sealed record ChildCommand(string Name, string Program, IReadOnlyList<string> Arguments);

// `script-run <name> -- <absolute program> [args]`. The same allow-list the host pins the task's action with (ScriptTaskXml), so the launcher refuses exactly what the
// pin refuses: a program that is a locked, fully qualified path on its own, or pwsh / powershell with -NoProfile, -NonInteractive and -File <fully qualified script>.
// Inline code, a relative or environment-variable path and an unknown switch are refused, so no file the child runs can go unpinned.
public static class CommandLine
{
    static readonly HashSet<string> Interpreters = new(["pwsh.exe", "powershell.exe"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> PlainSwitches = ["nologo", "mta", "sta"];
    static readonly HashSet<string> ValuedSwitches = ["executionpolicy", "windowstyle"];

    public static ChildCommand? Parse(IReadOnlyList<string> args)
    {
        if (args.Count < 3 || args[1] != "--" || !ValidName(args[0]) || !Pinnable(args[2])) { return null; }
        var program = args[2];
        var rest = args.Skip(3).ToList();
        if (!Interpreters.Contains(program[(program.LastIndexOfAny(['\\', '/']) + 1)..])) { return rest.Count == 0 ? new(args[0], program, rest) : null; }
        bool noProfile = false, nonInteractive = false;
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i].Length < 2 || rest[i][0] is not ('-' or '/')) { return null; }
            var name = rest[i][1..].ToLowerInvariant();
            if (name.Length <= 4 && "file".StartsWith(name, StringComparison.Ordinal))
            {
                return noProfile && nonInteractive && i + 1 < rest.Count && Pinnable(rest[i + 1]) ? new(args[0], program, rest) : null;
            }
            if (name == "noprofile") { noProfile = true; }
            else if (name == "noninteractive") { nonInteractive = true; }
            else if (ValuedSwitches.Contains(name)) { i++; }
            else if (!PlainSwitches.Contains(name)) { return null; }
        }
        return null;
    }

    static bool ValidName(string name) => name is { Length: >= 1 and <= 64 } && name is not ("." or "..") && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    // A Windows drive path with no environment variable, wildcard, stream, control character or relative segment (decided the same on every platform).
    static bool Pinnable(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length is > 3 and <= 260 && char.IsAsciiLetter(path[0]) && path[1] == ':'
        && path[2] is '\\' or '/' && path.IndexOfAny(['%', '$', '*', '?', '<', '>', '|', '"'], 2) < 0 && !path.Any(char.IsControl) && path.IndexOf(':', 2) < 0
        && !path[3..].Split('\\', '/').Any(s => s is "." or "..");

    // A task action's Arguments line, split the way the scheduler hands it to the program (quotes group, spaces separate).
    public static List<string> Tokens(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; }
            else if (char.IsWhiteSpace(c) && !quoted) { if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); } }
            else { current.Append(c); }
        }
        if (current.Length > 0) { tokens.Add(current.ToString()); }
        return tokens;
    }
}

public sealed class Launcher(IHostPort host, ITokenSource tokens, IChildStarter starter, TextWriter error)
{
    public const int Version = 1;
    // A refusal's exit code; a child that itself exits with it is mapped to 1, so the two never mix.
    public const int Refusal = 125;
    const string TokenVariable = "AGENT_BRIDGE_TOKEN";

    public async Task<int> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        if (CommandLine.Parse(args) is not { } command) { return Refuse("bad-command"); }
        Admission admission;
        try { admission = await host.AdmitAsync(command.Name, Version, ct); }
        catch (HostUnavailableException) { return Refuse("host-unavailable"); }
        catch (HostRefusedException) { return Refuse("refused"); }
        // The token is read only after the host has admitted this caller, held as bytes, written straight into the child's environment block and zeroed on every path.
        if (tokens.Read(admission.File) is not { } token) { return Refuse("refused"); }
        char[]? block = null;
        int exit;
        try
        {
            block = Environment(environment, token);
            try { exit = await starter.RunAsync(command.Program, command.Arguments, block, ct); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return Refuse("bad-command"); }
        }
        finally
        {
            Array.Clear(token);
            if (block is not null) { Array.Clear(block); }
        }
        // The host hears how it ended, best effort: a lost message is not a refusal and changes nothing.
        try { await host.FinishedAsync(admission.Id, exit, ct); }
        catch (Exception e) when (e is IOException or HostRefusedException or HostUnavailableException or OperationCanceledException) { }
        return exit == Refusal ? 1 : exit;
    }

    int Refuse(string code)
    {
        error.WriteLine(code);
        return Refusal;
    }

    // The task's own environment minus every AGENT_BRIDGE_* variable (a planted one cannot shadow the real one), plus the token, sorted by name, Unicode, double-null
    // terminated. The token goes from its bytes straight into the block: it is never a string.
    static char[] Environment(IReadOnlyDictionary<string, string> environment, byte[] token)
    {
        // The token takes its place among the sorted names, so the order holds whatever the task's own variables are called.
        var entries = environment.Where(p => !p.Key.StartsWith("AGENT_BRIDGE_", StringComparison.OrdinalIgnoreCase)).Select(p => (p.Key, (string?)p.Value))
            .Append((TokenVariable, null)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var block = new char[entries.Sum(p => p.Key.Length + 1 + (p.Item2?.Length ?? token.Length) + 1) + 1];
        var at = 0;
        foreach (var (key, value) in entries)
        {
            key.CopyTo(0, block, at, key.Length);
            at += key.Length;
            block[at++] = '=';
            if (value is null) { foreach (var b in token) { block[at++] = (char)b; } }
            else { value.CopyTo(0, block, at, value.Length); at += value.Length; }
            block[at++] = ' ';
        }
        block[at] = ' ';
        return block;
    }
}
