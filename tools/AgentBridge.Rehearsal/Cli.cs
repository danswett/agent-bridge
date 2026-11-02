namespace AgentBridge.Rehearsal;

// One command the tool takes: its words, how many arguments, and the options it accepts.
// Declared, so an option the tool does not know is an error rather than ignored, and so the
// checklist's command lines can be checked against the tool in CI.
internal sealed record CommandSpec(
    string Name,
    int MinArgs = 0,
    int MaxArgs = 0,
    string[]? Options = null,
    string[]? Flags = null,
    string[]? Repeatable = null,
    string[]? Required = null,
    string Usage = "")
{
    public string[] Options { get; init; } = Options ?? [];
    public string[] Flags { get; init; } = Flags ?? [];
    public string[] Repeatable { get; init; } = Repeatable ?? [];
    public string[] Required { get; init; } = Required ?? [];
}

internal sealed class Parsed(string name, IReadOnlyList<string> args, Dictionary<string, List<string>> options, HashSet<string> flags)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Args { get; } = args;

    public string? Option(string name) => options.TryGetValue(name, out var v) ? v[^1] : null;
    public IReadOnlyList<string> All(string name) => options.TryGetValue(name, out var v) ? v : [];
    public bool Flag(string name) => flags.Contains(name);

    public int? Int(string name) => Option(name) is { } s ? (int.TryParse(s, out var n) ? n : throw new UsageException($"{name} must be a whole number")) : null;
    public TimeSpan Timeout(int defaultSeconds) => TimeSpan.FromSeconds(Int("--timeout") ?? defaultSeconds);
}

internal sealed class UsageException(string message) : Exception(message);

internal static class Cli
{
    // Every command takes these: which checklist step the result is evidence for, where the
    // evidence goes, whose home folder to read (another Windows user's, or a test's), and
    // which host profile.
    public static readonly string[] Common = ["--step", "--evidence", "--home", "--profile"];

    public static readonly CommandSpec[] Commands =
    [
        new("baseline save", Options: ["--out"], Required: ["--out"],
            Usage: "baseline save --out <file>: hash every file agent-ha-bridge installed or registered, and its scheduled tasks"),
        new("baseline compare", Options: ["--baseline"], Repeatable: ["--allow", "--ignore"], Required: ["--baseline"],
            Usage: "baseline compare --baseline <file> [--allow install|hooks|agents|tasks]... [--ignore <path prefix>]...: what changed since the baseline"),
        new("g5", Options: ["--expect", "--timeout"],
            Usage: "g5 [--expect active|inactive] [--timeout <s>]: whether agent-ha-bridge counts as running for this user, as the host decides it"),
        new("host", Options: ["--timeout"], Repeatable: ["--expect-cap", "--expect-no-cap"], Flags: ["--expect-delivering", "--expect-read-only", "--try-restart"],
            Usage: "host [--expect-cap <c>]... [--expect-no-cap <c>]... [--expect-delivering|--expect-read-only] [--try-restart] [--timeout <s>]: the running host, over its pipe"),
        new("sessions", Options: ["--id", "--agent", "--title", "--status", "--model", "--expect-count", "--expect-min", "--expect-compactions", "--timeout"],
            Flags: ["--expect-question", "--expect-answer-at-machine", "--expect-gone", "--expect-background", "--id-only"],
            Usage: "sessions [--id <id>] [--agent <a>] [--title <text>] [--status <s>] [--model <text>] [--expect-count <n>|--expect-min <n>] [--expect-question|--expect-answer-at-machine|--expect-gone] [--expect-background] [--expect-compactions <n>] [--id-only] [--timeout <s>]: the host's sessions, asked again each second until the expectation holds or the timeout passes; --id-only prints only the id of the one session that matches"),
        new("mark", MinArgs: 1, MaxArgs: 1,
            Usage: "mark <session id>: remember where the session's transcript and the host log end now; expect reads only what comes after"),
        new("newest", MinArgs: 1, MaxArgs: 1,
            Usage: "newest copilot|claude|codex: print only the id of the agent's most recently written session, read from its transcripts, so no host is needed"),
        new("expect answer", MinArgs: 1, MaxArgs: 1, Options: ["--text", "--timeout", "--settle"], Repeatable: ["--field"], Flags: ["--expect-once"],
            Usage: "expect answer <session id> [--text <words>] [--field <name>=<value>]... [--timeout <s>] [--expect-once [--settle <s>]]: with --expect-once, also that exactly one answer was recorded after waiting --settle seconds (15)"),
        new("expect reply", MinArgs: 1, MaxArgs: 1, Options: ["--text", "--attachments", "--timeout"], Required: ["--text"],
            Usage: "expect reply <session id> --text <words> [--attachments <n>] [--timeout <s>]"),
        new("expect steering", MinArgs: 1, MaxArgs: 1, Options: ["--text", "--timeout"], Required: ["--text"],
            Usage: "expect steering <session id> --text <words> [--timeout <s>]: a reply Copilot took as a steering or queued prompt; run sessions --status working just before replying, since Copilot marks interactive prompts steering even when idle"),
        new("expect permission", MinArgs: 1, MaxArgs: 1, Options: ["--decision", "--timeout"], Required: ["--decision"],
            Usage: "expect permission <session id> --decision approved|denied [--timeout <s>]"),
        new("expect shutdown", MinArgs: 1, MaxArgs: 1, Options: ["--timeout"],
            Usage: "expect shutdown <session id> [--timeout <s>]"),
        new("log", Options: ["--contains", "--timeout"], Required: ["--contains"],
            Usage: "log --contains <text> [--timeout <s>]: a host log line written since the last mark"),
        new("ha", Options: ["--url", "--token-file", "--slug", "--expect-sessions", "--timeout"],
            Repeatable: ["--expect-entity", "--expect-no-entity", "--expect-state", "--expect-session", "--expect-no-session"], Flags: ["--expect-online", "--expect-offline", "--expect-no-duplicates"],
            Required: ["--token-file"],
            Usage: "ha --token-file <file> [--url http://127.0.0.1:8123] [--slug <machine slug>] [--expect-online|--expect-offline] [--expect-sessions <n>] [--expect-no-duplicates] [--expect-entity <id>]... [--expect-no-entity <id>]... [--expect-state <id>=<state>]... [--expect-session <session id>]... [--expect-no-session <session id>]... [--timeout <s>]: a disposable Home Assistant only; a session's entities are named as the connector names them"),
        new("staging", Options: ["--expect-min-files"],
            Usage: "staging [--expect-min-files <n>]: the host's attachment staging folder is this user's alone"),
        new("worktrees", MinArgs: 1, MaxArgs: 1, Options: ["--expect-host-trees", "--expect-max-host-trees", "--expect-old-trees"],
            Usage: "worktrees <repository> [--expect-host-trees <n>] [--expect-max-host-trees <n>] [--expect-old-trees <n>]: the worktrees each bridge marked (old: agent-ha-bridge's)"),
        new("note", Options: ["--result", "--detail", "--screenshot"], Required: ["--step", "--result", "--detail"],
            Usage: "note --step <id> --result pass|fail|manual --detail <what was seen> [--screenshot <file>]: record a step a person or Hyperloop checked"),
        new("report", Options: ["--checklist"],
            Usage: "report [--checklist docs/parity-checklist.md]: the evidence so far, one line per step, and with --checklist the steps not yet run"),
        new("soak run", Options: ["--url", "--token-file", "--state", "--slug", "--workspace", "--count", "--every", "--hold", "--turn-timeout", "--restart-daemon-at", "--restart-shadow-at", "--only"],
            Repeatable: ["--model", "--effort"], Required: ["--token-file", "--state"],
            Usage: "soak run --token-file <file> --state <dir> [--url http://127.0.0.1:8123] [--slug <machine slug>] [--workspace Rehearsal] [--count 66] [--every <minutes, 22>] [--hold <s, 35>] [--turn-timeout <minutes, 6>] [--model <Agent>=<model>]... [--effort <Agent>=<effort>]... [--restart-daemon-at <episode>] [--restart-shadow-at <episode>] [--only <scenario>]: the shadow soak's scripted load through a disposable Home Assistant (docs/host-shadow.md); carries on from --state's soak.json"),
        new("soak status", Options: ["--state"], Required: ["--state"],
            Usage: "soak status --state <dir>: what the soak has done so far, by agent"),
        new("soak collect", MinArgs: 1, MaxArgs: 1, Options: ["--out"], Required: ["--out"],
            Usage: "soak collect <session id> --out <zip>: a session's raw transcripts and its sub-agents', zipped"),
        new("soak resumable", Options: ["--url", "--token-file", "--slug"], Required: ["--token-file"],
            Usage: "soak resumable --token-file <file> [--url] [--slug]: agent-ha-bridge's resume list (from Home Assistant) beside the host's (launch.options), as the shadow compares them"),
    ];

    public static Parsed Parse(IReadOnlyList<string> argv)
    {
        var spec = Commands
            .Where(c => c.Name.Split(' ') is var words && argv.Count >= words.Length && words.Select((w, i) => argv[i] == w).All(x => x))
            .OrderByDescending(c => c.Name.Length)
            .FirstOrDefault()
            ?? throw new UsageException(argv.Count == 0 ? "no command" : $"unknown command: {string.Join(' ', argv.Take(2))}");
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var args = new List<string>();
        var valued = spec.Options.Concat(spec.Repeatable).Concat(Common).ToHashSet(StringComparer.Ordinal);
        for (var i = spec.Name.Split(' ').Length; i < argv.Count; i++)
        {
            var a = argv[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (spec.Flags.Contains(a)) { flags.Add(a); continue; }
                if (!valued.Contains(a)) { throw new UsageException($"{spec.Name} does not take {a}"); }
                if (i + 1 >= argv.Count) { throw new UsageException($"{a} needs a value"); }
                if (options.ContainsKey(a) && !spec.Repeatable.Contains(a)) { throw new UsageException($"{a} is given twice"); }
                (options.TryGetValue(a, out var list) ? list : options[a] = []).Add(argv[++i]);
                continue;
            }
            args.Add(a);
        }
        if (args.Count < spec.MinArgs || args.Count > spec.MaxArgs)
        {
            throw new UsageException($"usage: {spec.Usage}");
        }
        foreach (var required in spec.Required)
        {
            if (!options.ContainsKey(required)) { throw new UsageException($"{spec.Name} needs {required}"); }
        }
        return new Parsed(spec.Name, args, options, flags);
    }

    public static string Help() =>
        "agent-bridge-rehearsal: the automated checks of the parity rehearsal (docs/parity-checklist.md).\n" +
        "Every command also takes --step <id> --evidence <dir> --home <dir> --profile <name>.\n" +
        "Exit codes: 0 pass, 1 fail, 2 the check could not be made, 3 left to a person.\n\n" +
        string.Join("\n", Commands.Select(c => "  " + c.Usage));
}
