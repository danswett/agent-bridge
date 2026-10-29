using System.Text;

namespace AgentBridge.Host.Delivery.Launch;

// A Windows command line for an argument list, as the started program will read it back.
//
// An executable reads its command line with CommandLineToArgvW's rules, which agent-ha-bridge's
// ConvertTo-BridgeArgumentString implements. A batch file (an npm shim such as copilot.cmd or
// codex.cmd) is different: CreateProcess hands it to cmd.exe, which expands %VARIABLES% and
// acts on & | < > outside quotes before the shim ever passes %* on, so a prompt typed on a
// phone could run commands on the machine. agent-ha-bridge quotes for CommandLineToArgvW alone
// there too. For a batch file this host runs cmd.exe itself, with AutoRun off and delayed
// expansion off, and quotes each argument the way Rust's standard library does since its fix
// for that hole (CVE-2024-24576): every argument with anything but a small safe set is quoted,
// a quote inside is doubled (which keeps cmd's own quote state in step), and every % is written
// as %%cd:~,% so that nothing can be read as a variable.
public static class WindowsCommandLine
{
    // CreateProcess takes at most 32,767 characters; cmd.exe at most 8,191 for its whole line.
    public const int MaxProcessLine = 32_767;
    public const int MaxBatchLine = 8_191;

    public static bool IsBatch(string file) => Path.GetExtension(file).ToLowerInvariant() is ".cmd" or ".bat";

    // What to start and with what command line: the program itself, or cmd.exe for a batch file.
    // Null with a reason when the line is too long for it.
    public static (string Application, string CommandLine)? For(string file, IReadOnlyList<string> arguments, out string? problem)
    {
        problem = null;
        if (IsBatch(file))
        {
            // As Rust does: the script's own path goes in plain quotes, so one that a quote or a
            // variable could break out of is not run at all.
            if (file.Contains('"') || file.Contains('%') || file.EndsWith('\\')) { problem = $"{file} has a character cmd.exe would misread in its path"; return null; }
            if (arguments.Any(a => a.Any(char.IsControl))) { problem = "an argument holds a control character, which cmd.exe would cut the command line at"; return null; }
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var inner = new StringBuilder();
            inner.Append('"').Append(file).Append('"');
            foreach (var a in arguments) { inner.Append(' '); AppendBatch(inner, a, quote: false); }
            var line = $"\"{cmd}\" /e:ON /v:OFF /d /c \"{inner}\"";
            if (line.Length > MaxBatchLine) { problem = $"the command line would be {line.Length} characters, and cmd.exe takes at most {MaxBatchLine}"; return null; }
            return (cmd, line);
        }
        var b = new StringBuilder();
        AppendQuoted(b, file);
        foreach (var a in arguments) { b.Append(' '); AppendQuoted(b, a); }
        if (b.Length >= MaxProcessLine) { problem = $"the command line would be {b.Length} characters, and Windows takes at most {MaxProcessLine - 1}"; return null; }
        return (file, b.ToString());
    }

    // CommandLineToArgvW's rules: quote an argument holding whitespace or a quote (or none at
    // all), double each run of backslashes before a quote, and escape the quote.
    public static void AppendQuoted(StringBuilder b, string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            b.Append(arg);
            return;
        }
        b.Append('"');
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { b.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            if (slashes > 0) { b.Append('\\', slashes); slashes = 0; }
            b.Append(c);
        }
        b.Append('\\', slashes * 2).Append('"');
    }

    // Rust's append_bat_arg. Control characters never get here: a prompt is flattened first,
    // and anything else holding one is refused before a line is built.
    static void AppendBatch(StringBuilder b, string arg, bool quote)
    {
        if (arg.Length == 0 || arg[^1] == '\\') { quote = true; }
        const string Unquoted = @"#$*+-./:?@\_";
        foreach (var c in arg)
        {
            if ((c < 0x80 && !char.IsAsciiLetterOrDigit(c) && !Unquoted.Contains(c)) || char.IsControl(c)) { quote = true; }
        }
        if (quote) { b.Append('"'); }
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { slashes++; }
            else
            {
                if (c == '"') { b.Append('\\', slashes).Append('"'); }
                else if (c == '%') { b.Append("%%cd:~,"); }
                slashes = 0;
            }
            b.Append(c);
        }
        if (quote) { b.Append('\\', slashes).Append('"'); }
    }
}
