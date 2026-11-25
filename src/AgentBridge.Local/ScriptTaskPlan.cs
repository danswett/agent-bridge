using System.Security;
using System.Text.RegularExpressions;

namespace AgentBridge.Local;

// What a scheduled script is, decided once (docs/design/script-credentials-run.md sections 4 and 8, owner answers Q-run-2 to Q-run-4, 2026-11-24). The app shows its
// Summary to the person, the elevated step registers its TaskXml, and the host pins its Files, so what was shown is what runs. It is built only from the launcher's own
// form and the same allow-list the pin reads (a fully qualified program, pwsh or powershell with -NoProfile -NonInteractive -File <script>), so it never names a file the pin
// would not. Pure: it reads nothing and starts nothing.
public sealed record ScriptTaskPlan(string Name, string Task, string UserSid, string Launcher, string Interpreter, string Script, IReadOnlyList<string> Extra, int EveryMinutes)
{
    public const string Folder = @"\Slipgates\";
    // The pin holds at most 16 files: the launcher, the interpreter and the script, and so at most 13 more.
    public const int MaxExtra = 13;
    public static readonly IReadOnlyList<int> Intervals = [5, 10, 15, 30, 60];
    static readonly Regex Sid = new(@"^S-1-5-21(-\d{1,10}){4}\z", RegexOptions.CultureInvariant);
    static readonly string[] Interpreters = ["pwsh.exe", "powershell.exe"];

    public string Arguments => $"{Name} -- \"{Interpreter}\" -NoProfile -NonInteractive -File \"{Script}\"";

    // The files the token can reach, the launcher first (the order of ScriptTaskBinding.Pin).
    public IReadOnlyList<string> Files => [Launcher, Interpreter, Script, .. Extra];

    // Everything the person is shown before the Windows prompt: the task, the user, the exact program and arguments, every file and how often it runs.
    public string Summary => string.Join('\n', (string[])[
        $"Task: {Task} (runs only while Windows user {UserSid} is logged on; no password is stored)",
        $"Every {EveryMinutes} minutes",
        $"Program: {Launcher}",
        $"Arguments: {Arguments}",
        "Files the credential can reach (each must be changeable only by administrators):",
        .. Files.Select(f => "  " + f)]);

    // The task as the Scheduler stores it: one InteractiveToken principal, one action, one repeating trigger.
    public string TaskXml => "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
        + $"<Triggers><TimeTrigger><Repetition><Interval>PT{EveryMinutes}M</Interval></Repetition><StartBoundary>2026-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>"
        + $"<Principals><Principal id=\"Author\"><UserId>{UserSid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>"
        + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT1H</ExecutionTimeLimit></Settings>"
        + $"<Actions Context=\"Author\"><Exec><Command>{SecurityElement.Escape(Launcher)}</Command><Arguments>{SecurityElement.Escape(Arguments)}</Arguments></Exec></Actions></Task>";

    public static bool TryCreate(string name, string userSid, string launcher, string interpreter, string script, IReadOnlyList<string> extra, int everyMinutes,
        out ScriptTaskPlan? plan, out string reason)
    {
        plan = null;
        reason = Check(name, userSid, launcher, interpreter, script, extra, everyMinutes);
        if (reason.Length > 0) { return false; }
        plan = new(name, Folder + name, userSid, launcher, interpreter, script, [.. extra], everyMinutes);
        return true;
    }

    static string Check(string name, string userSid, string launcher, string interpreter, string script, IReadOnlyList<string> extra, int every)
    {
        if (name is not { Length: >= 1 and <= 64 } || name is "." or ".." || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        { return "A script name is 1 to 64 letters, digits, dots, dashes or underscores."; }
        if (!Sid.IsMatch(userSid ?? "")) { return "The Windows user could not be identified."; }
        if (!Intervals.Contains(every)) { return "Choose one of the listed repeat intervals."; }
        if (!Interpreters.Contains(System.IO.Path.GetFileName(interpreter ?? "").ToLowerInvariant())) { return "The program must be PowerShell (pwsh.exe or powershell.exe)."; }
        if (!Pinnable(launcher) || !Pinnable(interpreter)) { return "The launcher or PowerShell is not a full path the task pin can read."; }
        if (!Pinnable(script) || !script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) { return "The script must be a full path to a .ps1 file, with no variables or relative parts."; }
        if (extra.Count > MaxExtra) { return $"At most {MaxExtra} extra files can be pinned."; }
        if (extra.Any(e => !Pinnable(e))) { return "Every extra file must be a full path with no variables or relative parts."; }
        var all = new[] { launcher, interpreter, script }.Concat(extra).ToList();
        return all.Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Count ? "" : "The same file is named twice.";
    }

    // The pin's own rule (ScriptTaskXml.Pinnable): a drive path with no environment variable, wildcard, stream, control character or relative segment.
    static bool Pinnable(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length is > 3 and <= 260 && char.IsAsciiLetter(path[0]) && path[1] == ':'
        && path[2] is '\\' or '/' && path.IndexOfAny(['%', '$', '*', '?', '<', '>', '|', '"'], 2) < 0 && !path.Any(char.IsControl) && path.IndexOf(':', 2) < 0
        && !path[3..].Split('\\', '/').Any(s => s is "." or "..");
}
